using CommunityToolkit.Mvvm.ComponentModel;
using StutterDiag.Ipc;

namespace StutterDiag.Gui.Models;

/// <summary>A label/value pair for the System page. <see cref="IsUnavailable"/> drives grey styling.</summary>
public sealed record KeyValueRow(string Key, string Value)
{
    public bool IsUnavailable =>
        string.IsNullOrWhiteSpace(Value)
        || Value.Equals("Unavailable", StringComparison.OrdinalIgnoreCase)
        || Value.StartsWith("Not available", StringComparison.OrdinalIgnoreCase);
}

/// <summary>One driver row for the System page drivers table.</summary>
public sealed record DriverRow(string Name, string Version, string Date, string Vendor, string DeviceClass, string Path);

/// <summary>Per-monitor health line on the Dashboard (name + Ok/Degraded/Unavailable + note).</summary>
public sealed record MonitorHealthRow(string Name, string Status, string Note)
{
    public static MonitorHealthRow From(MonitorHealthDto d) =>
        new(d.Name, string.IsNullOrWhiteSpace(d.Status) ? "Unknown" : d.Status, d.Note ?? "");
}

/// <summary>A session wrapped with a checkbox state for multi-select on the Report page.</summary>
public sealed partial class SelectableSession : ObservableObject
{
    public SelectableSession(SessionDto session) => Session = session;

    public SessionDto Session { get; }

    [ObservableProperty]
    private bool _isSelected;

    public string Display =>
        $"#{Session.Id}  {Session.StartedUtcIso}  ·  {Session.Label}  ·  {Session.Mode}  ·  {Session.StutterCount} stutters";
}

/// <summary>A single mark on the Timeline canvas, in seconds from the session start.</summary>
public sealed record TimelineItem(
    long SyntheticId,
    DateTime TimestampUtc,
    double OffsetSeconds,
    string Kind,
    string Label,
    double? DurationMs,
    long? StutterId = null)
{
    public bool IsStutter =>
        Kind.Contains("stutter", StringComparison.OrdinalIgnoreCase)
        || Kind.Contains("mark", StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// One automatic-diagnostic line for the Dashboard. Strictly observation / hypothesis /
/// not-proven — the tool never asserts causation (docs/ARCHITECTURE.md §1, §29).
/// </summary>
public sealed record FindingRow(
    string Score, string Signal, string Observation, string Hypothesis, string NotProven, string Coverage)
{
    public static FindingRow From(RecentFindingDto d) => new(
        string.IsNullOrWhiteSpace(d.Score) ? "NoEvidence" : d.Score,
        d.DisplayName,
        d.Observation,
        d.Hypothesis,
        d.NotProven,
        d.TotalStutters == 0
            ? "no stutters yet"
            : $"correlated with {d.CorrelatedStutters}/{d.TotalStutters} stutters"
              + (d.BaseRatePercent > 0 ? $" · base rate ~{d.BaseRatePercent:0}%" : string.Empty));
}
