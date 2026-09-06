using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using StutterDiag.Core.Abstractions;
using StutterDiag.Core.Model;

namespace StutterDiag.Reporting;

/// <summary>
/// <see cref="IReportGenerator"/> for <see cref="ReportFormat.Json"/>. Serialises the full
/// <see cref="ReportModel"/> with System.Text.Json: camelCase names, enums as strings, indented.
/// </summary>
public sealed class JsonReportGenerator : IReportGenerator
{
    internal static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        // DiagnosticFinding.Lift / CorrelationRankingRow.Lift are +Infinity when the base rate is 0.
        NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    private readonly ReportDataLoader _loader;

    public JsonReportGenerator(ReportDataLoader loader)
        => _loader = loader ?? throw new ArgumentNullException(nameof(loader));

    public ReportFormat Format => ReportFormat.Json;

    public async Task<string> GenerateAsync(ReportRequest request, CancellationToken ct)
    {
        var model = await _loader.LoadAsync(request, ct).ConfigureAwait(false);

        string path = ReportPaths.ResolveFile(request.OutputPath, "report.json");
        string? dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        await File.WriteAllTextAsync(path, Serialize(model), new UTF8Encoding(false), ct).ConfigureAwait(false);
        return path;
    }

    /// <summary>Serialises a model to the report's canonical JSON form.</summary>
    public static string Serialize(ReportModel model) => JsonSerializer.Serialize(model, Options);
}
