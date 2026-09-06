using Microsoft.Diagnostics.Tracing;
using StutterDiag.Core.Abstractions;
using StutterDiag.Core.Config;
using StutterDiag.Core.Model;
using StutterDiag.Core.Time;
using StutterDiag.Etw;
using StutterDiag.Monitors.Common;

namespace StutterDiag.Monitors.Frametime;

/// <summary>
/// Approximate per-process frame time from present activity, PresentMon-style: it watches
/// <c>Microsoft-Windows-DxgKrnl</c> present/flip completions and <c>Microsoft-Windows-Dwm-Core</c>
/// composition presents and measures the interval between successive presents by the same
/// process. Emits <c>"frame.time.ms"</c> samples and raises a <see cref="DetectorKind.Frametime"/>
/// candidate when an interval spikes well above that process's recent median.
/// </summary>
/// <remarks>
/// This is the PresentMon <i>technique</i> — pure ETW observation, NOT a Present hook or an
/// injected overlay. Limits:
/// <list type="bullet">
///   <item>needs an application actually presenting frames; an idle desktop yields nothing;</item>
///   <item>realtime ETW consumption requires administrator;</item>
///   <item>without DxgKrnl or DWM-Core enabled it reports <see cref="HealthStatus.Unavailable"/>;</item>
///   <item>composed (windowed) vs. independent-flip (exclusive fullscreen) present paths differ,
///         so absolute values are indicative, not a substitute for a real frame-time overlay.</item>
/// </list>
/// NOTE(build): the DxgKrnl / DWM present event names and the fields used for process
/// attribution must be verified against the installed manifests and PresentMon's
/// PresentMonTraceConsumer — they change across Windows builds.
/// </remarks>
public sealed class FrametimeMonitor : MetricMonitorBase, IStutterDetector
{
    private const string DxgKrnl = "Microsoft-Windows-DxgKrnl";
    private const string DwmCore = "Microsoft-Windows-Dwm-Core";
    private const int HistoryLen = 32;

    private readonly EtwUserSession _user;
    private readonly StutterThresholdOptions _thresholds;
    private readonly EtwTimebase _timebase;

    private readonly Dictionary<int, PerProcess> _byProcess = new(32);
    private int _started;

    public FrametimeMonitor(
        EtwUserSession userSession,
        QpcClock clock,
        StutterThresholdOptions thresholds,
        EtwTimebase? timebase = null)
        : base("Frametime", clock)
    {
        _user = userSession;
        _thresholds = thresholds;
        _timebase = timebase ?? new EtwTimebase(clock);
    }

    public DetectorKind Kind => DetectorKind.Frametime;
    public bool SupportsHighResolutionMode => false;
    public void SetHighResolutionMode(bool enabled) { /* independent of the kernel high-res window */ }

    public event EventHandler<StutterCandidate>? StutterDetected;

    public override Task StartAsync(CancellationToken ct)
    {
        if (Interlocked.Exchange(ref _started, 1) == 1)
            return Task.CompletedTask;

        bool ok = _user.IsAvailable && (_user.IsEnabled(DxgKrnl) || _user.IsEnabled(DwmCore));
        if (!ok)
        {
            SetHealth(MonitorHealth.Unavailable("frametime needs DxgKrnl/DWM ETW"));
            return Task.CompletedTask;
        }

        _user.Session!.Source.Dynamic.All += OnDynamic;
        SetHealth(MonitorHealth.Ok);
        return Task.CompletedTask;
    }

    public override Task StopAsync(CancellationToken ct)
    {
        if (Interlocked.Exchange(ref _started, 0) == 0)
            return Task.CompletedTask;
        if (_user.Session is { } s)
            s.Source.Dynamic.All -= OnDynamic;
        _byProcess.Clear();
        return Task.CompletedTask;
    }

    public override async ValueTask DisposeAsync()
    {
        await StopAsync(CancellationToken.None).ConfigureAwait(false);
        await base.DisposeAsync().ConfigureAwait(false);
    }

    private void OnDynamic(TraceEvent data)
    {
        var provider = data.ProviderName;
        if (provider != DxgKrnl && provider != DwmCore)
            return;

        var name = data.EventName ?? string.Empty;
        // A "present completed" signal: DxgKrnl present/flip stop, or a DWM composed present.
        bool present =
            (provider == DxgKrnl && name.IndexOf("Present", StringComparison.OrdinalIgnoreCase) >= 0
                                 && (name.EndsWith("Stop", StringComparison.OrdinalIgnoreCase)
                                     || name.EndsWith("Info", StringComparison.OrdinalIgnoreCase)))
            || (provider == DxgKrnl && name.IndexOf("MMIOFlip", StringComparison.OrdinalIgnoreCase) >= 0)
            || (provider == DwmCore && name.IndexOf("Present", StringComparison.OrdinalIgnoreCase) >= 0);
        if (!present)
            return;

        int pid = data.ProcessID;
        if (pid <= 0)
            return;

        long qpc = _timebase.ToQpc(data);

        if (!_byProcess.TryGetValue(pid, out var pp))
        {
            pp = new PerProcess { Name = data.ProcessName ?? pid.ToString() };
            _byProcess[pid] = pp;
        }

        if (pp.LastPresentQpc != 0)
        {
            double intervalMs = (qpc - pp.LastPresentQpc) * 1000.0 / Clock.Frequency;
            pp.LastPresentQpc = qpc;

            if (intervalMs is > 0 and < 10_000)   // ignore the first sample after a long idle gap
            {
                RaiseSample(qpc, "frame.time.ms", pp.Name, intervalMs);

                double median = pp.Median();
                pp.Push(intervalMs);

                double floor = Math.Max(_thresholds.MicroStutterMs, median * 2.0);
                if (median > 0 && intervalMs >= floor)
                {
                    double lost = intervalMs - median;
                    var confidence =
                        intervalMs >= _thresholds.SevereMs ? DetectionConfidence.High
                        : intervalMs >= _thresholds.MajorMs || intervalMs >= median * 4 ? DetectionConfidence.Medium
                        : DetectionConfidence.Low;

                    try
                    {
                        StutterDetected?.Invoke(this, new StutterCandidate(
                            TimestampQpc: qpc,
                            EstimatedDurationMs: lost,
                            Detector: DetectorKind.Frametime,
                            Confidence: confidence,
                            Note: $"{pp.Name}: frame {intervalMs:F1} ms vs median {median:F1} ms"));
                    }
                    catch { /* isolation */ }
                }
            }
        }
        else
        {
            pp.LastPresentQpc = qpc;
        }
    }

    private sealed class PerProcess
    {
        public string Name = "";
        public long LastPresentQpc;
        private readonly double[] _hist = new double[HistoryLen];
        private int _n;
        private int _len;

        public void Push(double ms)
        {
            _hist[_n] = ms;
            _n = (_n + 1) % HistoryLen;
            if (_len < HistoryLen) _len++;
        }

        // Small N; a copy-and-sort median is cheap and only runs once per present.
        public double Median()
        {
            if (_len == 0) return 0;
            Span<double> tmp = stackalloc double[_len];
            for (int i = 0; i < _len; i++) tmp[i] = _hist[i];
            tmp.Sort();
            return _len % 2 == 1 ? tmp[_len / 2] : (tmp[_len / 2 - 1] + tmp[_len / 2]) / 2.0;
        }
    }
}
