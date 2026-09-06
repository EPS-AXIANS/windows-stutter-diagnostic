using StutterDiag.Core.Model;

namespace StutterDiag.Service;

/// <summary>One monitor's health, flattened for the status snapshot.</summary>
public sealed record MonitorHealthSnapshot(string Name, HealthStatus Status, string? Note);

/// <summary>
/// A recently aggregated stutter kept in the orchestrator's in-memory ring so the GUI/CLI can
/// show it without a database round-trip. <see cref="TopCorrelation"/> is filled in once the
/// post-roll correlation pass finishes (see <c>MonitorOrchestrator</c>).
/// </summary>
public sealed record RecentStutterSnapshot
{
    public long Id { get; init; }
    public DateTime TimestampUtc { get; init; }
    public double DurationMs { get; init; }
    public StutterSeverity Severity { get; init; }
    public DetectorKind Detector { get; init; }
    public bool UserMarked { get; init; }
    public int CorrelationCount { get; set; }
    public string? TopCorrelation { get; set; }
}

/// <summary>
/// Immutable point-in-time view of the orchestrator, surfaced over IPC as <c>StatusDto</c>.
/// Every timestamp here is wall-clock UTC (derived from the QPC axis) for display only.
/// </summary>
public sealed record StatusSnapshot
{
    public bool Monitoring { get; init; }
    public long SessionId { get; init; }
    public DateTime? StartedUtc { get; init; }
    public double MonitoringSeconds { get; init; }
    public string Mode { get; init; } = "Standard";

    public int StuttersDetected { get; init; }
    public int MajorStutters { get; init; }
    public int TpmEvents { get; init; }
    public int WheaEvents { get; init; }
    public int DpcSpikes { get; init; }

    public DateTime? LastEventUtc { get; init; }
    public string? LastEventText { get; init; }

    public bool IsElevated { get; init; }
    public IReadOnlyList<MonitorHealthSnapshot> Monitors { get; init; } = Array.Empty<MonitorHealthSnapshot>();

    /// <summary>Non-fatal notes for the UI, e.g. "Kernel ETW unavailable (requires administrator)".</summary>
    public IReadOnlyList<string> Warnings { get; init; } = Array.Empty<string>();
}
