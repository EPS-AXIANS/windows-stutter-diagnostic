using StutterDiag.Core.Abstractions;
using StutterDiag.Core.Model;
using StutterDiag.Core.Time;
using StutterDiag.Etw;
using StutterDiag.Monitors.Common;

namespace StutterDiag.Monitors.Stutter;

/// <summary>
/// Detects a stutter as scheduling latency: the gap between a thread being made runnable
/// (Dispatcher / ReadyThread) and it actually getting a core (CSwitch). A large gap for a
/// high-priority or system thread means something held the CPU or the scheduler could not
/// place the thread — a hitch that frame-producing and audio threads feel directly.
/// </summary>
/// <remarks>
/// Needs the high-resolution keyword set (ContextSwitch + Dispatcher), so it produces nothing
/// until <see cref="SetHighResolutionMode"/>(true). Runs entirely on the ETW worker thread.
/// The pending-ready map is size-capped: a ready with no following CSwitch (thread migrated
/// cores, or the window closed) is dropped rather than leaked.
/// </remarks>
public sealed class ReadyThreadLatencyDetector : MonitorBase, IStutterDetector
{
    private readonly IEtwKernelStream _stream;
    private readonly double _latencyThresholdMs;
    private readonly int _highPriorityFloor;

    private readonly Dictionary<int, long> _readyAt = new(4096);
    private const int MapCap = 20_000;

    private volatile bool _active;
    private int _started;

    /// <param name="highPriorityFloor">Windows priority at/above which a thread is "high priority" (24 ≈ real-time band).</param>
    public ReadyThreadLatencyDetector(
        IEtwKernelStream stream,
        QpcClock clock,
        double latencyThresholdMs = 10.0,
        int highPriorityFloor = 24)
        : base("Stutter.ReadyThread", clock)
    {
        _stream = stream;
        _latencyThresholdMs = latencyThresholdMs;
        _highPriorityFloor = highPriorityFloor;
    }

    public DetectorKind Kind => DetectorKind.ReadyThreadLatency;
    public bool SupportsHighResolutionMode => true;

    public void SetHighResolutionMode(bool enabled)
    {
        _active = enabled;
        // The worker clears the map lazily when it next sees _active == false (avoids a
        // cross-thread mutation of the dictionary while the worker is iterating).
    }

    public event EventHandler<StutterCandidate>? StutterDetected;

    public override Task StartAsync(CancellationToken ct)
    {
        if (Interlocked.Exchange(ref _started, 1) == 1)
            return Task.CompletedTask;

        if (!_stream.Health.IsUsable)
        {
            SetHealth(MonitorHealth.Unavailable("context-switch ETW unavailable (requires administrator)"));
            return Task.CompletedTask;
        }

        _stream.ReadyThreadObserved += OnReady;
        _stream.ContextSwitchObserved += OnContextSwitch;
        _stream.HealthChanged += OnStreamHealth;
        SetHealth(MonitorHealth.Ok);
        return Task.CompletedTask;
    }

    public override Task StopAsync(CancellationToken ct)
    {
        if (Interlocked.Exchange(ref _started, 0) == 0)
            return Task.CompletedTask;
        _stream.ReadyThreadObserved -= OnReady;
        _stream.ContextSwitchObserved -= OnContextSwitch;
        _stream.HealthChanged -= OnStreamHealth;
        _readyAt.Clear();
        return Task.CompletedTask;
    }

    public override async ValueTask DisposeAsync()
    {
        await StopAsync(CancellationToken.None).ConfigureAwait(false);
        await base.DisposeAsync().ConfigureAwait(false);
    }

    private void OnStreamHealth(object? sender, MonitorHealth h)
    {
        if (!h.IsUsable) SetHealth(MonitorHealth.Unavailable("context-switch ETW stopped"));
    }

    private void OnReady(object? sender, ReadyThreadObservation o)
    {
        if (!_active) { if (_readyAt.Count > 0) _readyAt.Clear(); return; }

        if (_readyAt.Count >= MapCap)
            _readyAt.Clear();   // stale entries with no matching CSwitch — bound the memory

        _readyAt[o.AwakenedThreadId] = o.TimestampQpc;
    }

    private void OnContextSwitch(object? sender, ContextSwitchObservation o)
    {
        if (!_active) { if (_readyAt.Count > 0) _readyAt.Clear(); return; }

        if (!_readyAt.Remove(o.NewThreadId, out long readyQpc))
            return;

        double gapMs = (o.TimestampQpc - readyQpc) * 1000.0 / Clock.Frequency;
        if (gapMs < _latencyThresholdMs)
            return;

        bool interesting = o.NewThreadPriority >= _highPriorityFloor || o.NewProcessId == 4;
        if (!interesting)
            return;

        var confidence = gapMs >= _latencyThresholdMs * 3 ? DetectionConfidence.Medium : DetectionConfidence.Low;
        try
        {
            StutterDetected?.Invoke(this, new StutterCandidate(
                TimestampQpc: o.TimestampQpc,
                EstimatedDurationMs: gapMs,
                Detector: DetectorKind.ReadyThreadLatency,
                Confidence: confidence,
                Note: $"tid {o.NewThreadId} (pid {o.NewProcessId}, prio {o.NewThreadPriority}) waited {gapMs:F2} ms ready→running"));
        }
        catch { /* isolation */ }
    }
}
