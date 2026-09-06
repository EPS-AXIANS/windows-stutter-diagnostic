using StutterDiag.Core.Model;

namespace StutterDiag.Core.Abstractions;

/// <summary>
/// Read model the <c>CorrelationEngine</c> uses to build the window around a stutter.
/// The live implementation is an in-memory ring buffer (<c>RingBufferDataSource</c>);
/// report regeneration uses an implementation backed by <see cref="IEventStore"/>.
/// All ranges are inclusive and expressed on the QPC axis.
/// </summary>
public interface ICorrelationDataSource
{
    IReadOnlyList<MonitorEvent> GetEvents(long fromQpc, long toQpc);

    IReadOnlyList<MetricSample> GetSamples(long fromQpc, long toQpc);

    IReadOnlyList<DpcIsrStat> GetDpcIsrStats(long fromQpc, long toQpc);

    ProcessSnapshot? GetNearestSnapshot(long qpc);
}
