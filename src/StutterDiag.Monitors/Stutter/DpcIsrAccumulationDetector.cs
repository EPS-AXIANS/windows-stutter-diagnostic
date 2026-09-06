using StutterDiag.Core.Abstractions;
using StutterDiag.Core.Config;
using StutterDiag.Core.Model;
using StutterDiag.Core.Time;
using StutterDiag.Etw;
using StutterDiag.Monitors.Common;

namespace StutterDiag.Monitors.Stutter;

/// <summary>
/// Detects a stutter caused by the CPU being monopolised by kernel work: over a short sliding
/// window (~10 ms) it sums the execution time of every DPC and ISR. When that sum approaches
/// the whole window the processor had almost no time left for normal threads — a hitch that a
/// user-mode heartbeat would also feel, but here we know it was DPC/ISR and (via
/// <see cref="ModuleResolver"/>) which driver dominated.
/// </summary>
/// <remarks>
/// Consumes the always-on kernel keyword set through <see cref="IEtwKernelStream"/>, so it does
/// not need the high-resolution window. All processing happens on the single ETW worker thread,
/// so the ring is not locked. Requires per-event DPC/ISR duration from TraceEvent
/// (<c>ElapsedTimeMSec</c>); if that is unavailable this detector cannot fire — see docs/ETW.md.
/// </remarks>
public sealed class DpcIsrAccumulationDetector : MonitorBase, IStutterDetector
{
    private readonly IEtwKernelStream _stream;
    private readonly double _windowMs;
    private readonly double _cpuStarvationMs;
    private readonly long _windowTicks;

    // Sliding window of recent DPC/ISR intervals. Single-threaded (ETW worker) => no lock.
    private const int Capacity = 8192;
    private readonly long[] _qpc = new long[Capacity];
    private readonly double[] _ms = new double[Capacity];
    private readonly string[] _driver = new string[Capacity];
    private int _head, _count;
    private double _sum;
    private readonly Dictionary<string, double> _byDriver = new(64);

    private long _lastRaisedQpc;
    private int _started;

    /// <param name="cpuStarvationMs">
    /// How much DPC+ISR time inside one window counts as "the CPU was unavailable". Defaults to
    /// 60% of the window; the stutter aggregator applies the real <see cref="StutterSeverity"/>
    /// thresholds afterwards.
    /// </param>
    public DpcIsrAccumulationDetector(
        IEtwKernelStream stream,
        QpcClock clock,
        double windowMs = 10.0,
        double cpuStarvationMs = 6.0)
        : base("Stutter.DpcIsr", clock)
    {
        _stream = stream;
        _windowMs = windowMs;
        _cpuStarvationMs = cpuStarvationMs;
        _windowTicks = clock.MsToTicks(windowMs);
    }

    public DetectorKind Kind => DetectorKind.DpcIsrAccumulation;
    public bool SupportsHighResolutionMode => false;
    public void SetHighResolutionMode(bool enabled) { /* always-on keywords; nothing to toggle */ }

    public event EventHandler<StutterCandidate>? StutterDetected;

    public override Task StartAsync(CancellationToken ct)
    {
        if (Interlocked.Exchange(ref _started, 1) == 1)
            return Task.CompletedTask;

        if (!_stream.Health.IsUsable)
        {
            SetHealth(MonitorHealth.Unavailable("kernel DPC/ISR ETW unavailable (requires administrator)"));
            return Task.CompletedTask;
        }

        _stream.DpcIsrObserved += OnDpcIsr;
        _stream.HealthChanged += OnStreamHealth;
        SetHealth(MonitorHealth.Ok);
        return Task.CompletedTask;
    }

    public override Task StopAsync(CancellationToken ct)
    {
        if (Interlocked.Exchange(ref _started, 0) == 0)
            return Task.CompletedTask;
        _stream.DpcIsrObserved -= OnDpcIsr;
        _stream.HealthChanged -= OnStreamHealth;
        return Task.CompletedTask;
    }

    public override async ValueTask DisposeAsync()
    {
        await StopAsync(CancellationToken.None).ConfigureAwait(false);
        await base.DisposeAsync().ConfigureAwait(false);
    }

    private void OnStreamHealth(object? sender, MonitorHealth h)
    {
        if (!h.IsUsable) SetHealth(MonitorHealth.Unavailable("kernel DPC/ISR ETW stopped"));
    }

    private void OnDpcIsr(object? sender, DpcIsrObservation o)
    {
        Push(o.TimestampQpc, o.DurationMs, o.Driver);
        EvictOlderThan(o.TimestampQpc - _windowTicks);

        if (_sum < _cpuStarvationMs)
            return;

        // One candidate per window at most, so a sustained storm does not flood the aggregator.
        if (o.TimestampQpc - _lastRaisedQpc < _windowTicks)
            return;
        _lastRaisedQpc = o.TimestampQpc;

        var (topDriver, topMs) = Top();
        var confidence = _sum >= _windowMs * 0.9 ? DetectionConfidence.High : DetectionConfidence.Medium;

        try
        {
            StutterDetected?.Invoke(this, new StutterCandidate(
                TimestampQpc: o.TimestampQpc,
                EstimatedDurationMs: _sum,
                Detector: DetectorKind.DpcIsrAccumulation,
                Confidence: confidence,
                Note: $"DPC/ISR used {_sum:F2} ms of a {_windowMs:F0} ms window; top {topDriver} ({topMs:F2} ms)"));
        }
        catch { /* isolation */ }
    }

    private void Push(long qpc, double ms, string driver)
    {
        int tail = (_head + _count) % Capacity;
        if (_count == Capacity)
        {
            // Overflow (pathological DPC storm inside one window): drop the oldest first.
            Subtract(_head);
            _head = (_head + 1) % Capacity;
            _count--;
        }
        _qpc[tail] = qpc;
        _ms[tail] = ms;
        _driver[tail] = driver;
        _count++;
        _sum += ms;
        _byDriver[driver] = _byDriver.TryGetValue(driver, out var d) ? d + ms : ms;
    }

    private void EvictOlderThan(long horizonQpc)
    {
        while (_count > 0 && _qpc[_head] < horizonQpc)
        {
            Subtract(_head);
            _head = (_head + 1) % Capacity;
            _count--;
        }
    }

    private void Subtract(int index)
    {
        _sum -= _ms[index];
        var drv = _driver[index];
        if (_byDriver.TryGetValue(drv, out var d))
        {
            d -= _ms[index];
            if (d <= 1e-6) _byDriver.Remove(drv);
            else _byDriver[drv] = d;
        }
        _driver[index] = null!;
    }

    private (string Driver, double Ms) Top()
    {
        string drv = "unresolved";
        double best = 0;
        foreach (var kv in _byDriver)
        {
            if (kv.Value > best) { best = kv.Value; drv = kv.Key; }
        }
        return (drv, best);
    }
}
