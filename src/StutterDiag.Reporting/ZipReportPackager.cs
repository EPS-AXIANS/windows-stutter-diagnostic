using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using StutterDiag.Core.Abstractions;
using StutterDiag.Core.Model;

namespace StutterDiag.Reporting;

/// <summary>
/// <see cref="IReportGenerator"/> for <see cref="ReportFormat.Zip"/>. Bundles
/// <c>report.html</c>, <c>events.json</c>, <c>stutters.csv</c>, <c>system.json</c>,
/// <c>drivers.csv</c>, <c>tpm.json</c>, <c>whea.csv</c>, <c>timeline.csv</c> and a <c>logs/</c>
/// folder (a copy of the configured log directory, when present) into one archive.
/// </summary>
public sealed class ZipReportPackager : IReportGenerator
{
    private static readonly UTF8Encoding Utf8NoBom = new(false);

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    private readonly ReportDataLoader _loader;
    private readonly string? _logDirectory;

    public ZipReportPackager(ReportDataLoader loader, string? logDirectory)
    {
        _loader = loader ?? throw new ArgumentNullException(nameof(loader));
        _logDirectory = logDirectory;
    }

    public ReportFormat Format => ReportFormat.Zip;

    public async Task<string> GenerateAsync(ReportRequest request, CancellationToken ct)
    {
        var model = await _loader.LoadAsync(request, ct).ConfigureAwait(false);

        string zipPath = ReportPaths.ResolveZip(request.OutputPath);
        string? dir = Path.GetDirectoryName(zipPath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        string html = new HtmlReportGenerator(_loader).Render(model);

        var sessions = ReportModelQuery.Sessions(model).ToList();
        string eventsJson = JsonSerializer.Serialize(
            sessions.Select(s => new { session = s.Session.Id, events = s.Events }), Json);
        string systemJson = JsonSerializer.Serialize(
            sessions.Select(s => new { session = s.Session.Id, system = s.System }), Json);
        string tpmJson = JsonSerializer.Serialize(
            sessions.Select(s => new { session = s.Session.Id, tpm = s.System?.Tpm }), Json);

        if (File.Exists(zipPath)) File.Delete(zipPath);

        await using (var fs = new FileStream(zipPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        using (var zip = new ZipArchive(fs, ZipArchiveMode.Create))
        {
            AddText(zip, "report.html", html);
            AddText(zip, "events.json", eventsJson);
            AddText(zip, "stutters.csv", CsvReportGenerator.StuttersCsv(model));
            AddText(zip, "system.json", systemJson);
            AddText(zip, "drivers.csv", CsvReportGenerator.DriversCsv(model));
            AddText(zip, "tpm.json", tpmJson);
            AddText(zip, "whea.csv", CsvReportGenerator.WheaCsv(model));
            AddText(zip, "timeline.csv", CsvReportGenerator.TimelineCsv(model));
            CopyLogs(zip);
        }

        return zipPath;
    }

    private static void AddText(ZipArchive zip, string entryName, string content)
    {
        var entry = zip.CreateEntry(entryName, CompressionLevel.Optimal);
        using var s = entry.Open();
        byte[] bytes = Utf8NoBom.GetBytes(content);
        s.Write(bytes, 0, bytes.Length);
    }

    private void CopyLogs(ZipArchive zip)
    {
        if (string.IsNullOrWhiteSpace(_logDirectory) || !Directory.Exists(_logDirectory)) return;

        foreach (var file in Directory.EnumerateFiles(_logDirectory, "*", SearchOption.AllDirectories))
        {
            string rel = Path.GetRelativePath(_logDirectory, file).Replace(Path.DirectorySeparatorChar, '/');
            var entry = zip.CreateEntry("logs/" + rel, CompressionLevel.Optimal);
            try
            {
                using var input = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                using var output = entry.Open();
                input.CopyTo(output);
            }
            catch (IOException)
            {
                // Log file locked by the running service — skip it; the report is still valid.
            }
            catch (UnauthorizedAccessException)
            {
                // No read access to this file — skip it.
            }
        }
    }
}
