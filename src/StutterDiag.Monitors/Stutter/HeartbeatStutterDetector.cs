using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using StutterDiag.Core.Abstractions;
using StutterDiag.Core.Config;
using StutterDiag.Core.Model;
using StutterDiag.Core.Time;
using StutterDiag.Monitors.Common;

namespace StutterDiag.Monitors.Stutter;

/// <summary>
/// Detects scheduler-level stutters by running a small pool of high-priority probe threads
/// that each sleep for a fixed interval and measure how late they actually wake. A wake that
/// overshoots the requested interval by <see cref="HeartbeatOptions.MinReportMs"/> or more is
/// a candidate — the machine was not able to run a ready, time-critical user thread on time,
/// which is exactly what a felt micro-freeze looks like.
/// </summary>
/// <remarks>
/// This is the only detector that works with no elevation, so it is the baseline everywhere.
/// It cannot attribute the stall to a cause; it only proves one happened. Our own GC pauses
/// are excluded via <see cref="GC.CollectionCount"/> deltas so we do not report on ourselves.
/// </remarks>
public sealed class HeartbeatStutterDetector : MetricMonitorBase, IStutterDetector
{
    private readonly HeartbeatOptions _opt;
    private readonly StutterThresholdOptions _thresholds;
    private readonly IProbeClock _probe;

    private CancellationTokenSource? _cts;
    private Thread[] _threads = Array.Empty<Thread>();
    private bool _timerPeriodSet;
    private int _started;

    public HeartbeatStutterDetector(
        HeartbeatOptions options,
        StutterThresholdOptions thresholds,
        QpcClock clock,
        IProbeClock? probeClock = null)
        : base("Heartbeat", clock)
    {
        _opt = options;
        _thresholds = thresholds;
        // Real runs use the QPC-backed system clock + Thread.Sleep. Tests inject a fake so a
        // simulated wake delay is fully deterministic (ARCHITECTURE §11).
        _probe = probeClock ?? new SystemProbeClock();
    }

    public DetectorKind Kind => DetectorKind.Heartbeat;

    /// <summary>The probe measures the same thing at any resolution; nothing extra to switch on.</summary>
    public bool SupportsHighResolutionMode => false;

    public void SetHighResolutionMode(bool enabled) { /* no-op: see SupportsHighResolutionMode */ }

    public event EventHandler<StutterCandidate>? StutterDetected;

    public override Task StartAsync(CancellationToken ct)
    {
        if (Interlocked.Exchange(ref _started, 1) == 1)
            return Task.CompletedTask;

        // Pull the global timer resolution down so Thread.Sleep granularity matches the probe
        // interval. Failure is not fatal — we just run with coarser jitter and say so.
        if (OperatingSystem.IsWindows())
        {
            uint want = (uint)Math.Max(1, _opt.TimerResolutionMs);
            if (Native.timeBeginPeriod(want) == Native.TIMERR_NOERROR)
                _timerPeriodSet = true;
            else
                SetHealth(MonitorHealth.Degraded($"timeBeginPeriod({want}) refused; wake jitter will be coarser"));
        }

        int count = Math.Max(1, _opt.ProbeCount);
        _cts = new CancellationTokenSource();
        var token = _cts.Token;                 // capture: StopAsync may null _cts concurrently
        _threads = new Thread[count];
        for (int i = 0; i < count; i++)
        {
            int probeId = i;
            var t = new Thread(() => ProbeLoop(probeId, token))
            {
                IsBackground = true,
                Name = $"SD-Heartbeat-{probeId}",
                Priority = ThreadPriority.Highest,
            };
            _threads[i] = t;
            t.Start();
        }

        // Health stays Ok (the default) unless timeBeginPeriod above downgraded it to Degraded.
        return Task.CompletedTask;
    }

    public override Task StopAsync(CancellationToken ct)
    {
        if (Interlocked.Exchange(ref _started, 0) == 0)
            return Task.CompletedTask;

        try { _cts?.Cancel(); } catch { /* ignore */ }
        foreach (var t in _threads)
        {
            try { t.Join(TimeSpan.FromMilliseconds(500)); } catch { /* ignore */ }
        }
        _threads = Array.Empty<Thread>();

        if (_timerPeriodSet && OperatingSystem.IsWindows())
        {
            Native.timeEndPeriod((uint)Math.Max(1, _opt.TimerResolutionMs));
            _timerPeriodSet = false;
        }

        _cts?.Dispose();
        _cts = null;
        return Task.CompletedTask;
    }

    public override async ValueTask DisposeAsync()
    {
        await StopAsync(CancellationToken.None).ConfigureAwait(false);
        await base.DisposeAsync().ConfigureAwait(false);
    }

    private void ProbeLoop(int probeId, CancellationToken ct)
    {
        if (OperatingSystem.IsWindows())
        {
            // TIME_CRITICAL so only kernel activity / true starvation can delay our wake.
            Native.SetThreadPriority(Native.GetCurrentThread(), Native.THREAD_PRIORITY_TIME_CRITICAL);
        }

        int interval = Math.Max(1, _opt.ProbeIntervalMs);
        while (!ct.IsCancellationRequested)
        {
            if (Measure(probeId, interval, out var candidate) && candidate is { } c)
                RaiseCandidate(c);
        }
    }

    /// <summary>
    /// One probe iteration: sleep, measure the real elapsed time, emit the sample, and decide
    /// whether it is a candidate. Returns true (with <paramref name="candidate"/> set) only for
    /// a reportable, non-GC wake delay. Exposed for the unit test.
    /// </summary>
    internal bool Measure(int probeId, int intervalMs, out StutterCandidate? candidate)
    {
        candidate = null;

        int gc0 = GC.CollectionCount(0);
        int gc2 = GC.CollectionCount(2);

        long t0 = _probe.GetTimestamp();
        _probe.SleepMs(intervalMs);
        long t1 = _probe.GetTimestamp();

        bool gcHit = GC.CollectionCount(0) != gc0 || GC.CollectionCount(2) != gc2;

        double wakeDelayMs = (t1 - t0) * 1000.0 / _probe.Frequency;   // total time actually asleep
        double excessMs = wakeDelayMs - intervalMs;                   // overshoot beyond the request

        // Every probe emits a sample. ":gc" on the instance marks a reading that overlapped one
        // of our own collections, so the baseline stats can down-weight it.
        RaiseSample(t1, "sched.wakedelay.ms", gcHit ? $"{probeId}:gc" : probeId.ToString(),
            wakeDelayMs);

        if (wakeDelayMs < _opt.MinReportMs)
            return false;

        if (gcHit)
        {
            // Suppress: this is our garbage collector, not a system stutter. Leave a verbose trail.
            RaiseEvent(NewEvent(EventCategory.Other,
                $"Heartbeat wake delay {wakeDelayMs:F1} ms coincided with a local GC; suppressed",
                EventSeverity.Verbose, qpc: t1));
            return false;
        }

        var confidence = excessMs >= _thresholds.SevereMs ? DetectionConfidence.High
            : excessMs >= _thresholds.MajorMs ? DetectionConfidence.Medium
            : DetectionConfidence.Low;

        candidate = new StutterCandidate(
            TimestampQpc: t1,
            EstimatedDurationMs: excessMs,
            Detector: DetectorKind.Heartbeat,
            Confidence: confidence,
            Note: $"probe {probeId}: slept {wakeDelayMs:F1} ms for a {intervalMs} ms request");
        return true;
    }

    private void RaiseCandidate(StutterCandidate c)
    {
        try { StutterDetected?.Invoke(this, c); } catch { /* isolation */ }
    }

    // -------------------------------------------------------------------
    // Probe clock seam — real vs. test.
    // -------------------------------------------------------------------

    /// <summary>Timebase + sleep primitive the probe loop uses. Fakeable for deterministic tests.</summary>
    public interface IProbeClock
    {
        long GetTimestamp();
        long Frequency { get; }
        void SleepMs(int ms);
    }

    private sealed class SystemProbeClock : IProbeClock
    {
        // Stopwatch.GetTimestamp() == QueryPerformanceCounter on Windows (same axis as QpcClock).
        public long GetTimestamp() => Stopwatch.GetTimestamp();
        public long Frequency => Stopwatch.Frequency;
        public void SleepMs(int ms) => Thread.Sleep(ms);
    }

    [SupportedOSPlatform("windows")]
    private static class Native
    {
        public const uint TIMERR_NOERROR = 0;
        public const int THREAD_PRIORITY_TIME_CRITICAL = 15;

        [DllImport("winmm.dll", ExactSpelling = true)]
        public static extern uint timeBeginPeriod(uint uPeriod);

        [DllImport("winmm.dll", ExactSpelling = true)]
        public static extern uint timeEndPeriod(uint uPeriod);

        [DllImport("kernel32.dll")]
        public static extern IntPtr GetCurrentThread();

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool SetThreadPriority(IntPtr hThread, int nPriority);
    }
}
