using StutterDiag.Core.Abstractions;
using StutterDiag.Core.Model;
using StutterDiag.Core.Time;
using StutterDiag.Etw;
using StutterDiag.Monitors.Common;

namespace StutterDiag.Monitors.Etw;

/// <summary>
/// Emits <see cref="EventCategory.HardFault"/> events from the kernel hard-fault stream
/// (a page fault that had to be satisfied from disk — a direct stall of the faulting thread),
/// attributed to the process. Faults for the same process inside a short window are coalesced
/// into one event with a count so a paging storm cannot flood the ring buffer.
/// </summary>
public sealed class EtwHardFaultMonitor : MonitorBase, IMonitor
{
    private readonly IEtwKernelStream _stream;
    private readonly int _coalesceMs;

    private readonly object _gate = new();
    private readonly Dictionary<int, Pending> _pending = new(64);
    private System.Threading.Timer? _timer;
    private int _started;

    public EtwHardFaultMonitor(IEtwKernelStream stream, QpcClock clock, int coalesceMs = 100)
        : base("Etw.HardFault", clock)
    {
        _stream = stream;
        _coalesceMs = Math.Max(20, coalesceMs);
    }

    public override Task StartAsync(CancellationToken ct)
    {
        if (Interlocked.Exchange(ref _started, 1) == 1)
            return Task.CompletedTask;

        if (!_stream.Health.IsUsable)
        {
            SetHealth(MonitorHealth.Unavailable("hard-fault ETW unavailable (requires administrator)"));
            return Task.CompletedTask;
        }

        _stream.HardFaultObserved += OnHardFault;
        _stream.HealthChanged += OnStreamHealth;
        var period = TimeSpan.FromMilliseconds(_coalesceMs);
        _timer = new System.Threading.Timer(_ => FlushPending(), null, period, period);

        SetHealth(MonitorHealth.Ok);
        return Task.CompletedTask;
    }

    public override Task StopAsync(CancellationToken ct)
    {
        if (Interlocked.Exchange(ref _started, 0) == 0)
            return Task.CompletedTask;

        _stream.HardFaultObserved -= OnHardFault;
        _stream.HealthChanged -= OnStreamHealth;
        _timer?.Dispose();
        _timer = null;
        FlushPending();
        return Task.CompletedTask;
    }

    public override async ValueTask DisposeAsync()
    {
        await StopAsync(CancellationToken.None).ConfigureAwait(false);
        await base.DisposeAsync().ConfigureAwait(false);
    }

    private void OnStreamHealth(object? sender, MonitorHealth h)
    {
        if (!h.IsUsable) SetHealth(MonitorHealth.Unavailable("hard-fault ETW stopped"));
    }

    private void OnHardFault(object? sender, HardFaultObservation o)
    {
        lock (_gate)
        {
            ref var p = ref System.Runtime.InteropServices.CollectionsMarshal
                .GetValueRefOrAddDefault(_pending, o.ProcessId, out bool existed);
            if (!existed)
            {
                p.FirstQpc = o.TimestampQpc;
                p.Process = o.ProcessName;
            }
            p.LastQpc = o.TimestampQpc;
            p.Count++;
            p.Bytes += o.ByteCount;
            p.TotalMs += o.ElapsedMs;
            if (o.ElapsedMs > p.MaxMs) p.MaxMs = o.ElapsedMs;
            p.LastFile = o.FileName;
        }
    }

    private void FlushPending()
    {
        KeyValuePair<int, Pending>[] batch;
        lock (_gate)
        {
            if (_pending.Count == 0) return;
            batch = new KeyValuePair<int, Pending>[_pending.Count];
            ((ICollection<KeyValuePair<int, Pending>>)_pending).CopyTo(batch, 0);
            _pending.Clear();
        }

        foreach (var (pid, p) in batch)
        {
            double avgMs = p.Count > 0 ? p.TotalMs / p.Count : 0;
            RaiseEvent(NewEvent(EventCategory.HardFault,
                $"{p.Count} hard fault(s) in {p.Process} (pid {pid}); {p.Bytes / 1024.0:F0} KiB, "
                + $"avg {avgMs:F2} ms, max {p.MaxMs:F2} ms",
                p.Count >= 20 || p.MaxMs >= 20 ? EventSeverity.Warning : EventSeverity.Info,
                qpc: p.FirstQpc,
                data: new Dictionary<string, string>
                {
                    ["pid"] = pid.ToString(),
                    ["process"] = p.Process ?? "",
                    ["count"] = p.Count.ToString(),
                    ["bytes"] = p.Bytes.ToString(),
                    ["avgMs"] = avgMs.ToString("F3"),
                    ["maxMs"] = p.MaxMs.ToString("F3"),
                    ["lastFile"] = p.LastFile ?? "",
                    ["spanMs"] = ((p.LastQpc - p.FirstQpc) * 1000.0 / Clock.Frequency).ToString("F2"),
                }));
        }
    }

    private struct Pending
    {
        public long FirstQpc;
        public long LastQpc;
        public int Count;
        public long Bytes;
        public double TotalMs;
        public double MaxMs;
        public string? Process;
        public string? LastFile;
    }
}
