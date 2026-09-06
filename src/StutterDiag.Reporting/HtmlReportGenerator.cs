using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Scriban;
using Scriban.Runtime;
using StutterDiag.Core.Abstractions;
using StutterDiag.Core.Model;

namespace StutterDiag.Reporting;

/// <summary>
/// <see cref="IReportGenerator"/> for <see cref="ReportFormat.Html"/>. Renders a single
/// self-contained file: the Scriban template plus the inlined stylesheet and canvas timeline
/// script, with no external http/https references of any kind. Every page carries the sentence
/// "Correlation does not prove causation." and the diagnostic section is rendered strictly as
/// OBSERVATION / HYPOTHESIS / NOT PROVEN / BASE RATE / NO EVIDENCE blocks.
/// </summary>
public sealed class HtmlReportGenerator : IReportGenerator
{
    private readonly ReportDataLoader _loader;

    public HtmlReportGenerator(ReportDataLoader loader)
        => _loader = loader ?? throw new ArgumentNullException(nameof(loader));

    public ReportFormat Format => ReportFormat.Html;

    public async Task<string> GenerateAsync(ReportRequest request, CancellationToken ct)
    {
        var model = await _loader.LoadAsync(request, ct).ConfigureAwait(false);
        string html = Render(model);

        string path = ReportPaths.ResolveFile(request.OutputPath, "report.html");
        string? dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        await File.WriteAllTextAsync(path, html, new UTF8Encoding(false), ct).ConfigureAwait(false);
        return path;
    }

    /// <summary>Renders the model to a self-contained HTML string (used directly by the ZIP packager).</summary>
    public string Render(ReportModel model)
    {
        if (model is null) throw new ArgumentNullException(nameof(model));

        var template = Template.Parse(EmbeddedAssets.Template, "report.sbn");
        if (template.HasErrors)
            throw new InvalidOperationException("report.sbn failed to parse: " + string.Join("; ", template.Messages));

        var root = new ScriptObject
        {
            ["model"] = model,
            ["css"] = EmbeddedAssets.Css,
            ["timeline_js"] = EmbeddedAssets.TimelineJs,
            ["timeline_data_json"] = TimelineJson.Serialize(model),
            ["disclaimer"] = ReportModel.CausationDisclaimer,
            ["comparer_note"] = SessionComparer.HeaderNote,
        };

        var context = new TemplateContext
        {
            // Keep .NET member names as-is so the template reads like the C# model.
            MemberRenamer = member => member.Name,
            EnableRelaxedMemberAccess = true,
            StrictVariables = false,
        };
        context.PushGlobal(root);

        return template.Render(context);
    }
}

/// <summary>Loads the embedded template / stylesheet / timeline script once per process.</summary>
internal static class EmbeddedAssets
{
    public static string Template { get; } = Load("report.sbn");
    public static string Css { get; } = Load("report.css");
    public static string TimelineJs { get; } = Load("timeline.js");

    private static string Load(string endsWith)
    {
        var asm = typeof(EmbeddedAssets).Assembly;
        string suffix = "." + endsWith;

        string? name = null;
        foreach (var candidate in asm.GetManifestResourceNames())
            if (candidate.EndsWith(suffix, StringComparison.Ordinal)) { name = candidate; break; }

        if (name is null)
            throw new InvalidOperationException(
                $"Embedded resource ending in '{suffix}' was not found in assembly {asm.FullName}.");

        using var stream = asm.GetManifestResourceStream(name)
            ?? throw new InvalidOperationException($"Embedded resource stream '{name}' could not be opened.");
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    }
}

/// <summary>Serialises the timeline for the inline canvas script. Default JSON encoder escapes
/// &lt;, &gt; and &amp; so the payload is safe to embed inside a &lt;script&gt; element.</summary>
internal static class TimelineJson
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = false,
        NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals,
    };

    public static string Serialize(ReportModel model)
    {
        long freq = model.Meta.QpcFrequency;

        var sessions = ReportModelQuery.Sessions(model).Select(sr =>
        {
            long t0 = long.MaxValue, t1 = long.MinValue;
            foreach (var e in sr.Timeline)
            {
                if (e.Qpc < t0) t0 = e.Qpc;
                if (e.Qpc > t1) t1 = e.Qpc;
            }
            if (sr.Timeline.Count == 0) { t0 = 0; t1 = freq; }

            return new
            {
                id = sr.Session.Id,
                label = sr.Session.Label,
                t0,
                t1,
                entries = sr.Timeline.Select(e => new
                {
                    qpc = e.Qpc,
                    utc = e.Utc,
                    kind = e.Kind,
                    label = e.Label,
                    durationMs = e.DurationMs,
                    severity = e.Severity,
                }).ToList(),
                stutters = sr.Stutters
                    .Select(s => new { id = s.Id, index = s.Index, qpc = s.TimestampQpc })
                    .ToList(),
            };
        }).ToList();

        return JsonSerializer.Serialize(new { freq, sessions }, Options);
    }
}
