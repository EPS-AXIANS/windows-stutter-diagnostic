using StutterDiag.Core.Abstractions;
using StutterDiag.Core.Config;
using StutterDiag.Core.Model;
using StutterDiag.Core.Util;

namespace StutterDiag.Core.Correlation;

/// <summary>
/// In-RAM implementation of <see cref="ICorrelationDataSource"/>. The orchestrator feeds
/// every event/sample/DPC stat and process snapshot here; it keeps only the trailing
/// <c>HighRes.RingBufferSeconds</c> so the pre-roll of a stutter window is always available
/// without high-resolution collection running continuously.
/// </summary>
public sealed class RingBufferDataSource : ICorrelationDataSource
{
    private readonly BoundedRingBuffer<MonitorEvent> _events;
    private readonly BoundedRingBuffer<MetricSample> _samples;
    private readonly BoundedRingBuffer<DpcIsrStat> _dpcIsr;
    private readonly BoundedRingBuffer<ProcessSnapshot> _snapshots;
    private readonly long _retainTicks;

    public RingBufferDataSource(HighResOptions options, long qpcFrequency,
        int eventCapacity = 200_000, int sampleCapacity = 200_000,
        int dpcCapacity = 100_000, int snapshotCapacity = 256)
    {
        _events = new BoundedRingBuffer<MonitorEvent>(eventCapacity, e => e.TimestampQpc);
        _samples = new BoundedRingBuffer<MetricSample>(sampleCapacity, s => s.TimestampQpc);
        _dpcIsr = new BoundedRingBuffer<DpcIsrStat>(dpcCapacity, s => s.WindowStartQpc);
        _snapshots = new BoundedRingBuffer<ProcessSnapshot>(snapshotCapacity, s => s.TimestampQpc);
        _retainTicks = (long)(options.RingBufferSeconds * qpcFrequency);
    }

    public void Add(MonitorEvent e) => _events.Add(e);
    public void Add(MetricSample s) => _samples.Add(s);
    public void Add(DpcIsrStat s) => _dpcIsr.Add(s);
    public void Add(ProcessSnapshot s) => _snapshots.Add(s);

    /// <summary>Called on a low-frequency timer with the current QPC to discard stale data.</summary>
    public void Evict(long nowQpc)
    {
        long horizon = nowQpc - _retainTicks;
        _events.EvictOlderThan(horizon);
        _samples.EvictOlderThan(horizon);
        _dpcIsr.EvictOlderThan(horizon);
        _snapshots.EvictOlderThan(horizon);
    }

    public IReadOnlyList<MonitorEvent> GetEvents(long fromQpc, long toQpc) => _events.Query(fromQpc, toQpc);

    public IReadOnlyList<MetricSample> GetSamples(long fromQpc, long toQpc) => _samples.Query(fromQpc, toQpc);

    public IReadOnlyList<DpcIsrStat> GetDpcIsrStats(long fromQpc, long toQpc) => _dpcIsr.Query(fromQpc, toQpc);

    public ProcessSnapshot? GetNearestSnapshot(long qpc)
    {
        ProcessSnapshot? best = null;
        long bestDelta = long.MaxValue;
        foreach (var s in _snapshots.Snapshot())
        {
            long d = Math.Abs(s.TimestampQpc - qpc);
            if (d < bestDelta) { bestDelta = d; best = s; }
        }
        return best;
    }
}
