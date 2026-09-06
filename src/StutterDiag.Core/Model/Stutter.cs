namespace StutterDiag.Core.Model;

/// <summary>A raw detection from one <c>IStutterDetector</c>, before aggregation.</summary>
public sealed record StutterCandidate(
    long TimestampQpc,
    double EstimatedDurationMs,
    DetectorKind Detector,
    DetectionConfidence Confidence,
    string? Note = null);

/// <summary>
/// A consolidated stutter: one or more <see cref="StutterCandidate"/>s within the merge
/// window, classified by <see cref="Severity"/> and annotated with the other detectors
/// that independently saw it (<see cref="CorroboratedBy"/>).
/// </summary>
public sealed record Stutter
{
    public long Id { get; init; }               // 0 until persisted by the event store
    public long SessionId { get; init; }
    public long TimestampQpc { get; init; }
    public DateTime TimestampUtc { get; init; }
    public double DurationMs { get; init; }
    public StutterSeverity Severity { get; init; }
    public DetectorKind Detector { get; init; } // the detector that reported the longest duration
    public DetectionConfidence Confidence { get; init; }
    public IReadOnlyList<DetectorKind> CorroboratedBy { get; init; } = Array.Empty<DetectorKind>();
    public bool UserMarked { get; init; }
}

/// <summary>
/// A purely temporal association between a signal (driver DPC, TPM event, disk latency
/// spike, …) and a stutter. <see cref="Score"/> reflects proximity/anomaly only.
/// <see cref="BaseRate"/> is the fraction of non-stutter windows in which the same
/// signal appears — the number that makes the correlation meaningful.
/// </summary>
public sealed record StutterCorrelation(
    long StutterId,
    string SignalType,
    double ProximityMs,
    CorrelationScore Score,
    double BaseRate,
    string Detail);
