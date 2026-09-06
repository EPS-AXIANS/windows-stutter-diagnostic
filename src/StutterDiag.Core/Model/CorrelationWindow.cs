namespace StutterDiag.Core.Model;

/// <summary>
/// Everything captured in the time window around one stutter, plus the temporal
/// correlations derived from it. This is the unit the report renders per stutter.
/// </summary>
public sealed record CorrelationWindow(
    Stutter Stutter,
    long FromQpc,
    long ToQpc,
    IReadOnlyList<MonitorEvent> Events,
    IReadOnlyList<MetricSample> Samples,
    IReadOnlyList<DpcIsrStat> DpcIsr,
    ProcessSnapshot? Snapshot,
    IReadOnlyList<StutterCorrelation> Correlations);
