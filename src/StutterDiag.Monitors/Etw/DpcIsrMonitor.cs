using StutterDiag.Core.Abstractions;
using StutterDiag.Core.Model;
using StutterDiag.Core.Time;
using StutterDiag.Core.Util;
using StutterDiag.Etw;
using StutterDiag.Monitors.Common;

namespace StutterDiag.Monitors.Etw;

/// <summary>
/// Rolls the raw DPC / ISR stream up into per-driver <see cref="DpcIsrStat"/> records over
/// fixed ~1 s windows and keeps them in a ring for the correlation engine. Drivers whose DPC
/// or ISR time in a window is notable also get a discrete <see cref="EventCategory.Dpc"/> /
/// <see cref="EventCategory.Isr"/> event (with <c>driver</c> in <see cref="MonitorEvent.Data"/>,
/// so <c>SignalCatalog</c> can classify it).
/// </summary>
public sealed class DpcIsrMonitor : MonitorBase, IDpcMonitor
{
    private readonly IEtwKernelStream _stream;
    private readonly double _windowSeconds;
    private readonly double _notableMs;

    private readonly object _gate = new();
    private readonly Dictionary<(string Driver, bool Isr), Accum> _current = new(128);
    private readonly BoundedRingBuffer<DpcIsrStat> _ring;
    private long _windowStartQpc;
    private System.Threading.Timer? _timer;
    private int _started;

    public DpcIsrMonitor(
        IEtwKernelStream stream,
        QpcClock clock,
        double windowSeconds = 1.0,
        double notableMs = 1.0,
        int ringCapacity = 8192)
        : base("Etw.Dpc", clock)
    {
        _stream = stream;
        _windowSeconds = windowSeconds;
        _notableMs = notableMs;
        _ring = new BoundedRingBuffer<DpcIsrStat>(ringCapacity, s => s.WindowStartQpc);
    }

    public override Task StartAsync(CancellationToken ct)
    {
        if (Interlocked.Exchange(ref _started, 1) == 1)
            return Task.CompletedTask;

        if (!_stream.Health.IsUsable)
        {
            SetHealth(MonitorHealth.Unavailable("kernel DPC/ISR ETW unavailable (requires administrator)"));
            return Task.CompletedTask;
        }

        _windowStartQpc = Clock.GetTimestamp();
        _stream.DpcIsrObserved += OnDpcIsr;
        _stream.HealthChanged += OnStreamHealth;

        var period = TimeSpan.FromSeconds(_windowSeconds);
        _timer = new System.Threading.Timer(_ => Flush(final: false), null, period, period);

        SetHealth(MonitorHealth.Ok);
        return Task.CompletedTask;
    }

    public override Task StopAsync(CancellationToken ct)
    {
        if (Interlocked.Exchange(ref _started, 0) == 0)
            return Task.CompletedTask;

        _stream.DpcIsrObserved -= OnDpcIsr;
        _stream.HealthChanged -= OnStreamHealth;
        _timer?.Dispose();
        _timer = null;
        Flush(final: true);
        return Task.CompletedTask;
    }

    public override async ValueTask DisposeAsync()
    {
        await StopAsync(CancellationToken.None).ConfigureAwait(false);
        await base.DisposeAsync().ConfigureAwait(false);
    }

    /// <summary>Per-driver DPC/ISR stats whose window overlaps [fromQpc, toQpc].</summary>
    public IReadOnlyList<DpcIsrStat> GetStats(long fromQpc, long toQpc)
    {
        // The ring is keyed on WindowStartQpc; widen the low bound by one window so a stat whose
        // window straddles fromQpc is still returned.
        long lo = fromQpc - Clock.MsToTicks(_windowSeconds * 1000.0);
        return _ring.Query(lo, toQpc);
    }

    private void OnStreamHealth(object? sender, MonitorHealth h)
    {
        if (!h.IsUsable) SetHealth(MonitorHealth.Unavailable("kernel DPC/ISR ETW stopped"));
    }

    private void OnDpcIsr(object? sender, DpcIsrObservation o)
    {
        var key = (o.Driver, o.IsInterrupt);
        lock (_gate)
        {
            ref var a = ref System.Runtime.InteropServices.CollectionsMarshal.GetValueRefOrAddDefault(_current, key, out _);
            a.Total += o.DurationMs;
            a.Count++;
            if (o.DurationMs > a.Max) a.Max = o.DurationMs;
        }
    }

    private void Flush(bool final)
    {
        long end = Clock.GetTimestamp();
        List<DpcIsrStat> stats;
        long start;

        lock (_gate)
        {
            start = _windowStartQpc;
            _windowStartQpc = end;
            if (_current.Count == 0)
                return;

            stats = new List<DpcIsrStat>(_current.Count);
            foreach (var (key, a) in _current)
            {
                stats.Add(new DpcIsrStat(
                    WindowStartQpc: start,
                    WindowEndQpc: end,
                    Driver: key.Driver,
                    Kind: key.Isr ? "ISR" : "DPC",
                    TotalMs: a.Total,
                    Count: a.Count,
                    MaxMs: a.Max));
            }
            _current.Clear();
        }

        foreach (var s in stats)
        {
            _ring.Add(s);
            if (s.TotalMs < _notableMs)
                continue;

            RaiseEvent(NewEvent(
                s.Kind == "ISR" ? EventCategory.Isr : EventCategory.Dpc,
                $"{s.Kind} {s.Driver}: {s.TotalMs:F2} ms over {s.Count} in {_windowSeconds:F0}s (max {s.MaxMs:F2} ms)",
                s.TotalMs >= _notableMs * 10 ? EventSeverity.Warning : EventSeverity.Info,
                qpc: s.WindowStartQpc,
                data: new Dictionary<string, string>
                {
                    ["driver"] = s.Driver,
                    ["kind"] = s.Kind,
                    ["totalMs"] = s.TotalMs.ToString("F3"),
                    ["count"] = s.Count.ToString(),
                    ["maxMs"] = s.MaxMs.ToString("F3"),
                }));
        }
    }

    private struct Accum
    {
        public double Total;
        public long Count;
        public double Max;
    }
}
