using System.Runtime.Versioning;
using StutterDiag.Core.Abstractions;
using StutterDiag.Core.Config;
using StutterDiag.Core.Model;
using StutterDiag.Core.Time;
using StutterDiag.Monitors.Common;

namespace StutterDiag.Monitors.Memory;

/// <summary>
/// System memory pressure time-series from the single-instance <c>Memory</c> PDH counter set:
/// available bytes, commit, hard-fault rate (<c>Page Reads/sec</c>), total page-fault rate and
/// pool usage. Per-process page-fault deltas live in <see cref="StutterDiag.Core.Abstractions.IProcessMonitor"/>.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class MemoryMonitor : MetricMonitorBase, IMemoryMonitor
{
    private readonly AppConfig _config;

    private PerfCounterSampler? _sampler;
    private MultiInstanceCounter? _availableMb;     // Available MBytes
    private MultiInstanceCounter? _committedBytes;  // Committed Bytes
    private MultiInstanceCounter? _commitLimit;     // Commit Limit
    private MultiInstanceCounter? _pageReads;       // Page Reads/sec  (hard faults)
    private MultiInstanceCounter? _pageFaults;      // Page Faults/sec (all faults)
    private MultiInstanceCounter? _poolPaged;       // Pool Paged Bytes
    private MultiInstanceCounter? _poolNonpaged;    // Pool Nonpaged Bytes
    private bool _firstTick = true;

    public MemoryMonitor(AppConfig config, QpcClock clock) : base("Memory", clock)
    {
        _config = config;
    }

    public override Task StartAsync(CancellationToken ct)
    {
        if (!OperatingSystem.IsWindows())
        {
            SetHealth(MonitorHealth.Unavailable("memory monitor requires Windows performance counters"));
            return Task.CompletedTask;
        }

        try
        {
            _availableMb = new MultiInstanceCounter("Memory", "Available MBytes");
            _committedBytes = new MultiInstanceCounter("Memory", "Committed Bytes");
            _commitLimit = new MultiInstanceCounter("Memory", "Commit Limit");
            _pageReads = new MultiInstanceCounter("Memory", "Page Reads/sec");
            _pageFaults = new MultiInstanceCounter("Memory", "Page Faults/sec");
            _poolPaged = new MultiInstanceCounter("Memory", "Pool Paged Bytes");
            _poolNonpaged = new MultiInstanceCounter("Memory", "Pool Nonpaged Bytes");

            if (!_availableMb.Available)
            {
                SetHealth(MonitorHealth.Unavailable(_availableMb.Unavailable ?? "Memory counter set is not present"));
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

        _availableMb?.Dispose(); _committedBytes?.Dispose(); _commitLimit?.Dispose();
        _pageReads?.Dispose(); _pageFaults?.Dispose(); _poolPaged?.Dispose(); _poolNonpaged?.Dispose();
        _availableMb = _committedBytes = _commitLimit = _pageReads = _pageFaults = _poolPaged = _poolNonpaged = null;
    }

    private void Sample()
    {
        long qpc = Clock.GetTimestamp();

        if (_firstTick)
        {
            _availableMb?.Read(); _committedBytes?.Read(); _commitLimit?.Read();
            _pageReads?.Read(); _pageFaults?.Read(); _poolPaged?.Read(); _poolNonpaged?.Read();
            _firstTick = false;
            return;
        }

        EmitFirst(_availableMb, qpc, "mem.available.mb", 1.0);
        EmitFirst(_committedBytes, qpc, "mem.committed.mb", 1.0 / (1024.0 * 1024.0));
        EmitFirst(_commitLimit, qpc, "mem.commitlimit.mb", 1.0 / (1024.0 * 1024.0));
        EmitFirst(_pageReads, qpc, "mem.hardfaults.persec", 1.0);
        EmitFirst(_pageFaults, qpc, "mem.pagefaults.persec", 1.0);
        EmitFirst(_poolPaged, qpc, "mem.poolpaged.mb", 1.0 / (1024.0 * 1024.0));
        EmitFirst(_poolNonpaged, qpc, "mem.poolnonpaged.mb", 1.0 / (1024.0 * 1024.0));
    }

    private void EmitFirst(MultiInstanceCounter? counter, long qpc, string metric, double scale)
    {
        if (counter is null) return;
        var readings = counter.Read();
        if (readings.Count == 0) return;
        RaiseSample(qpc, metric, string.Empty, readings[0].Value * scale);
    }

    public override async ValueTask DisposeAsync()
    {
        await StopAsync(CancellationToken.None).ConfigureAwait(false);
        await base.DisposeAsync().ConfigureAwait(false);
    }
}
