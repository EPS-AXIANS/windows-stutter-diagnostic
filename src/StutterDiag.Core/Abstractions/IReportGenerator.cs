using StutterDiag.Core.Model;

namespace StutterDiag.Core.Abstractions;

/// <summary>
/// Produces one export format. The HTML generator must emit a fully self-contained file
/// (no external http/https references) and include the
/// "Correlation does not prove causation" disclaimer.
/// </summary>
public interface IReportGenerator
{
    ReportFormat Format { get; }

    /// <summary>Writes the report and returns the path actually written.</summary>
    Task<string> GenerateAsync(ReportRequest request, CancellationToken ct);
}
