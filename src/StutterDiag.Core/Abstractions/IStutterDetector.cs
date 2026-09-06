using StutterDiag.Core.Model;

namespace StutterDiag.Core.Abstractions;

/// <summary>
/// A method of detecting a stutter / micro-freeze. Several implementations run at once
/// (heartbeat scheduler probe, DPC/ISR accumulation, ready-thread latency, frametime,
/// GPU packet gap, user-marked) and their <see cref="StutterCandidate"/>s are merged by
/// the aggregator.
/// </summary>
public interface IStutterDetector : IMonitor
{
    DetectorKind Kind { get; }

    /// <summary>True if this detector can produce finer data while a high-res window is active.</summary>
    bool SupportsHighResolutionMode { get; }

    void SetHighResolutionMode(bool enabled);

    event EventHandler<StutterCandidate>? StutterDetected;
}
