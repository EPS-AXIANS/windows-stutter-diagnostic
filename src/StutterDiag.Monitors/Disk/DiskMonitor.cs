using System.Runtime.Versioning;
using StutterDiag.Core.Abstractions;
using StutterDiag.Core.Config;
using StutterDiag.Core.Model;
using StutterDiag.Core.Time;
using StutterDiag.Monitors.Common;

namespace StutterDiag.Monitors.Disk;

/// <summary>
/// Physical-disk latency, queue depth, IOPS and throughput time-series from the
/// <c>PhysicalDisk</c> PDH counter set. Per-I/O latency spikes are the ETW engineer's
/// <c>Etw.DiskIo</c> monitor; this one only produces the counter series that the
/// correlation engine baselines.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class DiskMonitor : MetricMonitorBase, IDiskMonitor
{
    private readonly AppConfig _config;

    private PerfCounterSampler? _sampler;
    private MultiInstanceCounter? _readLatency;    // Avg. Disk sec/Read  (seconds)
    private MultiInstanceCounter? _writeLatency;   // Avg. Disk sec/Write (seconds)
    private MultiInstanceCounter? _queue;          // Current Disk Queue Length
    private MultiInstanceCounter? _iops;           // Disk Transfers/sec
    private MultiInstanceCounter? _bytes;          // Disk Bytes/sec
    private MultiInstanceCounter? _idle;           // % Idle Time
    private bool _firstTick = true;

    public DiskMonitor(AppConfig config, QpcClock clock) : base("Disk", clock)
    {
        _config = config;
    }

    public override Task StartAsync(CancellationToken ct)
    {
        if (!OperatingSystem.IsWindows())
        {
            SetHealth(MonitorHealth.Unavailable("disk monitor requires Windows performance counters"));
            return Task.CompletedTask;
        }

        try
        {
            _readLatency = new MultiInstanceCounter("PhysicalDisk", "Avg. Disk sec/Read");
            _writeLatency = new MultiInstanceCounter("PhysicalDisk", "Avg. Disk sec/Write");
            _queue = new MultiInstanceCounter("PhysicalDisk", "Current Disk Queue Length");
            _iops = new MultiInstanceCounter("PhysicalDisk", "Disk Transfers/sec");
            _bytes = new MultiInstanceCounter("PhysicalDisk", "Disk Bytes/sec");
            _idle = new MultiInstanceCounter("PhysicalDisk", "% Idle Time");

            if (!_readLatency.Available && !_queue.Available)
            {
                SetHealth(MonitorHealth.Unavailable(
                    _readLatency.Unavailable ?? "PhysicalDisk counter set is not present"));
                return Task.CompletedTask;
            }

            SetHealth(MonitorHealth.Ok);

            _sampler = new PerfCounterSampler(_config.Sampling.PerfCounterHz);
            _sampler.OnTick(Sample);
            _sampler.Start();
        }
        catch (Exception ex)
        {
            SetHealth(MonitorHealth.Failed(ex.Message));
        }
        return Task.CompletedTask;
    }

    public override async Task StopAsync(CancellationToken ct)
    {
        if (_sampler is not null) await _sampler.DisposeAsync().ConfigureAwait(false);
        _sampler = null;

        _readLatency?.Dispose(); _writeLatency?.Dispose(); _queue?.Dispose();
        _iops?.Dispose(); _bytes?.Dispose(); _idle?.Dispose();
        _readLatency = _writeLatency = _queue = _iops = _bytes = _idle = null;
    }

    private void Sample()
    {
        long qpc = Clock.GetTimestamp();

        if (_firstTick)
        {
            _readLatency?.Read(); _writeLatency?.Read(); _queue?.Read();
            _iops?.Read(); _bytes?.Read(); _idle?.Read();
            _firstTick = false;
            return;
        }

        Emit(_readLatency, qpc, "disk.read.latency.ms", 1000.0);   // seconds -> ms
        Emit(_writeLatency, qpc, "disk.write.latency.ms", 1000.0);
        Emit(_queue, qpc, "disk.queue", 1.0);
        Emit(_iops, qpc, "disk.iops", 1.0);
        Emit(_bytes, qpc, "disk.bytespersec", 1.0);
        Emit(_idle, qpc, "disk.idle.pct", 1.0);
    }

    private void Emit(MultiInstanceCounter? counter, long qpc, string metric, double scale)
    {
        if (counter is null) return;
        foreach (var r in counter.Read())
        {
            string instance = r.Instance.Equals("_Total", StringComparison.OrdinalIgnoreCase)
                ? string.Empty
                : r.Instance; // e.g. "0 C:"
            RaiseSample(qpc, metric, instance, r.Value * scale);
        }
    }

    public override async ValueTask DisposeAsync()
    {
        await StopAsync(CancellationToken.None).ConfigureAwait(false);
        await base.DisposeAsync().ConfigureAwait(false);
    }
}
