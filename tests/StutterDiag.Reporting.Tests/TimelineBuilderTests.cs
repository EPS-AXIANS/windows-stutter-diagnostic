using FluentAssertions;
using StutterDiag.Core.Model;
using StutterDiag.Core.Time;
using StutterDiag.Reporting;
using Xunit;

namespace StutterDiag.Reporting.Tests;

public sealed class TimelineBuilderTests
{
    private static readonly QpcClock Clock = new();
    private const long Center = 1_000_000_000;

    private static TimelineBuilder NewBuilder() => new(Clock, dpcSpikeMs: 2.0, diskLatencySpikeMs: 10.0);

    private static (Stutter[] stutters, MonitorEvent[] events, MetricSample[] samples, DpcIsrStat[] dpc) Scenario()
    {
        var stutters = new[]
        {
            new Stutter
            {
                Id = 1, TimestampQpc = Center, TimestampUtc = Clock.QpcToUtc(Center),
                DurationMs = 90, Severity = StutterSeverity.Major, Detector = DetectorKind.Heartbeat,
            },
        };
        var events = new[]
        {
            new MonitorEvent { TimestampQpc = Center + Clock.MsToTicks(50), TimestampUtc = Clock.QpcToUtc(Center + Clock.MsToTicks(50)), Category = EventCategory.Whea, Message = "whea" },
            new MonitorEvent { TimestampQpc = Center + Clock.MsToTicks(5_000), TimestampUtc = Clock.QpcToUtc(Center + Clock.MsToTicks(5_000)), Category = EventCategory.KernelPower, Message = "kp" },
        };
        var dpc = new[]
        {
            new DpcIsrStat(Center + Clock.MsToTicks(20), Center + Clock.MsToTicks(30), "nvlddmkm.sys", "DPC", 6.0, 10, 3.0),
        };
        return (stutters, events, Array.Empty<MetricSample>(), dpc);
    }

    [Fact]
    public void Build_returns_entries_sorted_by_qpc()
    {
        var (s, e, m, d) = Scenario();

        var timeline = NewBuilder().Build(s, e, m, d);

        timeline.Should().NotBeEmpty();
        timeline.Select(x => x.Qpc).Should().BeInAscendingOrder();
        timeline.Should().Contain(x => x.Kind == TimelineBuilder.KindStutter);
        timeline.Should().Contain(x => x.Kind == TimelineBuilder.KindDpcSpike);
    }

    [Fact]
    public void Zoom_narrows_to_the_requested_window_and_keeps_the_stutter_entry()
    {
        var (s, e, m, d) = Scenario();
        var builder = NewBuilder();
        var full = builder.Build(s, e, m, d);

        var zoomed = builder.Zoom(full, Center, plusMinusMs: 100);

        zoomed.Should().Contain(x => x.Kind == TimelineBuilder.KindStutter);
        zoomed.Should().NotContain(x => x.Kind == TimelineBuilder.KindKernelPower, "the Kernel-Power event is 5 s away");

        long half = Clock.MsToTicks(100);
        zoomed.Should().OnlyContain(x => x.Qpc >= Center - half && x.Qpc <= Center + half);
    }

    [Fact]
    public void Zoom_by_stutter_id_centres_on_that_stutter()
    {
        var (s, e, m, d) = Scenario();
        var builder = NewBuilder();
        var full = builder.Build(s, e, m, d);

        var zoomed = builder.Zoom(full, s, stutterId: 1, plusMinusMs: 100);

        zoomed.Should().Contain(x => x.Kind == TimelineBuilder.KindStutter);
        zoomed.Should().NotContain(x => x.Kind == TimelineBuilder.KindKernelPower);
    }

    [Fact]
    public void Zoom_by_an_unknown_stutter_id_returns_empty()
    {
        var (s, e, m, d) = Scenario();
        var builder = NewBuilder();
        var full = builder.Build(s, e, m, d);

        builder.Zoom(full, s, stutterId: 999, plusMinusMs: 100).Should().BeEmpty();
    }
}
