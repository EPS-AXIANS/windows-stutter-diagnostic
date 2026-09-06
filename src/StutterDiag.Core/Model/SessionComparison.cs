namespace StutterDiag.Core.Model;

/// <summary>One metric compared across two sessions (A/B, e.g. fTPM vs dTPM).</summary>
public sealed record ComparisonRow(string Metric, string ValueA, string ValueB, string Delta);

/// <summary>
/// Side-by-side comparison of two monitoring sessions. Purely descriptive: it reports
/// differences in stutter frequency/duration, TPM events, DPC, drivers, WHEA, CPU, GPU,
/// storage and power-state activity. It does not attribute the difference to any cause.
/// </summary>
public sealed record SessionComparison(
    MonitoringSession A,
    MonitoringSession B,
    IReadOnlyList<ComparisonRow> Rows);
