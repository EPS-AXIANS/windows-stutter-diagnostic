using StutterDiag.Core.Model;
using StutterDiag.Core.Time;

namespace StutterDiag.Reporting;

/// <summary>One point on the report timeline. Ordered and windowed on the QPC axis only.</summary>
/// <param name="Qpc">QueryPerformanceCounter tick (authoritative for ordering).</param>
/// <param name="Utc">Wall-clock UTC, for axis labels only.</param>
/// <param name="Kind">
/// One of: <c>Normal</c>, <c>Event</c>, <c>DpcSpike</c>, <c>DiskLatencySpike</c>, <c>Stutter</c>,
/// <c>UserMark</c>, <c>Whea</c>, <c>KernelPower</c>.
/// </param>
/// <param name="Label">Short human description.</param>
/// <param name="DurationMs">Duration where meaningful (stutters, spikes); otherwise null.</param>
/// <param name="Severity">Severity/level token for colouring.</param>
public sealed record TimelineEntry(
    long Qpc,
    DateTime Utc,
    string Kind,
    string Label,
    double? DurationMs,
    string Severity);

/// <summary>
/// Builds an ordered timeline for a whole session, or a zoom window around one stutter.
/// It only places markers for things that were recorded; it never interpolates or infers.
/// </summary>
public sealed class TimelineBuilder
{
    public const string KindNormal = "Normal";
    public const string KindEvent = "Event";
    public const string KindDpcSpike = "DpcSpike";
    public const string KindDiskLatencySpike = "DiskLatencySpike";
    public const string KindStutter = "Stutter";
    public const string KindUserMark = "UserMark";
    public const string KindWhea = "Whea";
    public const string KindKernelPower = "KernelPower";

    private readonly QpcClock _clock;
    private readonly double _dpcSpikeMs;
    private readonly double _diskSpikeMs;
    private readonly double _normalMarkerSeconds;

    public TimelineBuilder(
        QpcClock clock,
        double dpcSpikeMs = 2.0,
        double diskLatencySpikeMs = 10.0,
        double normalMarkerSeconds = 30.0)
    {
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _dpcSpikeMs = dpcSpikeMs;
        _diskSpikeMs = diskLatencySpikeMs;
        _normalMarkerSeconds = normalMarkerSeconds <= 0 ? 30.0 : normalMarkerSeconds;
    }

    /// <summary>Builds the full-session timeline, ordered by QPC.</summary>
    public IReadOnlyList<TimelineEntry> Build(
        IReadOnlyList<Stutter> stutters,
        IReadOnlyList<MonitorEvent> events,
        IReadOnlyList<MetricSample> samples,
        IReadOnlyList<DpcIsrStat> dpcIsr)
    {
        var list = new List<TimelineEntry>();

        foreach (var s in stutters ?? Array.Empty<Stutter>())
        {
            string kind = s.UserMarked ? KindUserMark : KindStutter;
            list.Add(new TimelineEntry(
                s.TimestampQpc, s.TimestampUtc, kind,
                $"{s.Severity} stutter {s.DurationMs:F1} ms ({s.Detector})",
                s.DurationMs, s.Severity.ToString()));
        }

        foreach (var e in events ?? Array.Empty<MonitorEvent>())
        {
            string? kind = e.Category switch
            {
                EventCategory.Stutter => null,       // covered by the stutter list
                EventCategory.UserMark => KindUserMark,
                EventCategory.Whea => KindWhea,
                EventCategory.KernelPower => KindKernelPower,
                _ => KindEvent,
            };
            if (kind is null) continue;

            list.Add(new TimelineEntry(
                e.TimestampQpc, e.TimestampUtc, kind,
                $"{e.Category}: {Trim(e.Message, 120)}",
                null, e.Severity.ToString()));
        }

        foreach (var d in dpcIsr ?? Array.Empty<DpcIsrStat>())
        {
            if (d.Kind != "DPC" || d.MaxMs < _dpcSpikeMs) continue;
            list.Add(new TimelineEntry(
                d.WindowStartQpc, _clock.QpcToUtc(d.WindowStartQpc), KindDpcSpike,
                $"DPC peak {d.MaxMs:F1} ms in {d.Driver} ({d.Count} calls)",
                d.MaxMs, "Warning"));
        }

        foreach (var m in samples ?? Array.Empty<MetricSample>())
        {
            if (m.Metric is not ("disk.read.latency.ms" or "disk.write.latency.ms")) continue;
            if (m.Value < _diskSpikeMs) continue;
            list.Add(new TimelineEntry(
                m.TimestampQpc, _clock.QpcToUtc(m.TimestampQpc), KindDiskLatencySpike,
                $"{m.Metric} {m.Value:F1} ms",
                m.Value, "Warning"));
        }

        AddNormalMarkers(list);

        list.Sort(CompareEntries);
        return list;
    }

    /// <summary>Filters an already-built timeline to ±<paramref name="plusMinusMs"/> around a QPC.</summary>
    public IReadOnlyList<TimelineEntry> Zoom(IReadOnlyList<TimelineEntry> full, long centerQpc, double plusMinusMs)
    {
        long half = _clock.MsToTicks(Math.Abs(plusMinusMs));
        long from = centerQpc - half;
        long to = centerQpc + half;

        var outp = new List<TimelineEntry>();
        foreach (var e in full)
            if (e.Qpc >= from && e.Qpc <= to)
                outp.Add(e);
        outp.Sort(CompareEntries);
        return outp;
    }

    /// <summary>Zoom view centred on a specific stutter (<c>Zoom(stutterId, ±ms)</c>).</summary>
    public IReadOnlyList<TimelineEntry> Zoom(
        IReadOnlyList<TimelineEntry> full,
        IReadOnlyList<Stutter> stutters,
        long stutterId,
        double plusMinusMs)
    {
        var s = stutters?.FirstOrDefault(x => x.Id == stutterId);
        return s is null ? Array.Empty<TimelineEntry>() : Zoom(full, s.TimestampQpc, plusMinusMs);
    }

    private void AddNormalMarkers(List<TimelineEntry> list)
    {
        if (list.Count == 0) return;

        long min = long.MaxValue, max = long.MinValue;
        foreach (var e in list) { if (e.Qpc < min) min = e.Qpc; if (e.Qpc > max) max = e.Qpc; }
        if (max <= min) return;

        long step = _clock.MsToTicks(_normalMarkerSeconds * 1000.0);
        if (step <= 0) return;

        for (long t = min; t <= max; t += step)
            list.Add(new TimelineEntry(t, _clock.QpcToUtc(t), KindNormal, "reference", null, "Info"));
    }

    private static int CompareEntries(TimelineEntry a, TimelineEntry b)
    {
        int c = a.Qpc.CompareTo(b.Qpc);
        if (c != 0) return c;
        c = string.CompareOrdinal(a.Kind, b.Kind);
        if (c != 0) return c;
        return string.CompareOrdinal(a.Label, b.Label);
    }

    private static string Trim(string s, int max)
        => string.IsNullOrEmpty(s) ? "" : s.Length <= max ? s : s[..max] + "…";
}
