using FluentAssertions;
using NSubstitute;
using StutterDiag.Core.Abstractions;
using StutterDiag.Core.Config;
using StutterDiag.Core.Correlation;
using StutterDiag.Core.Model;
using StutterDiag.Core.Time;
using Xunit;

namespace StutterDiag.Core.Tests;

public sealed class CorrelationEngineTests
{
    private sealed class FakeDataSource : ICorrelationDataSource
    {
        public readonly List<MonitorEvent> Events = new();
        public readonly List<MetricSample> Samples = new();
        public readonly List<DpcIsrStat> Dpc = new();
        public ProcessSnapshot? Snapshot;

        public IReadOnlyList<MonitorEvent> GetEvents(long fromQpc, long toQpc)
            => Events.Where(e => e.TimestampQpc >= fromQpc && e.TimestampQpc <= toQpc).ToList();

        public IReadOnlyList<MetricSample> GetSamples(long fromQpc, long toQpc)
            => Samples.Where(s => s.TimestampQpc >= fromQpc && s.TimestampQpc <= toQpc).ToList();

        public IReadOnlyList<DpcIsrStat> GetDpcIsrStats(long fromQpc, long toQpc)
            => Dpc.Where(d => d.WindowStartQpc >= fromQpc && d.WindowStartQpc <= toQpc).ToList();

        public ProcessSnapshot? GetNearestSnapshot(long qpc) => Snapshot;
    }

    private static readonly QpcClock Clock = new();
    private const long StutterQpc = 1_000_000_000;

    private static CorrelationEngine NewEngine(CorrelationOptions? opt = null)
        => new(Clock, opt ?? new CorrelationOptions());

    private static Stutter NewStutter() => new() { Id = 1, TimestampQpc = StutterQpc, DurationMs = 0 };

    private static MonitorEvent WheaEventAt(long qpc) => new()
    {
        TimestampQpc = qpc,
        TimestampUtc = DateTime.UnixEpoch,
        Category = EventCategory.Whea,
        Source = "test",
        Message = "synthetic WHEA record",
    };

    [Theory]
    [InlineData(10, CorrelationScore.High)]    // <= HighProximityMs (25)
    [InlineData(100, CorrelationScore.Medium)] // <= MediumProximityMs (250)
    [InlineData(1500, CorrelationScore.Low)]   // inside the +/-5 s window
    public void Proximity_maps_to_the_expected_score(double offsetMs, CorrelationScore expected)
    {
        var src = new FakeDataSource();
        src.Events.Add(WheaEventAt(StutterQpc + Clock.MsToTicks(offsetMs)));

        var window = NewEngine().Analyze(NewStutter(), src);

        var whea = window.Correlations.Single(c => c.SignalType == SignalCatalog.Whea);
        whea.Score.Should().Be(expected);
    }

    [Fact]
    public void A_signal_outside_the_preRoll_postRoll_window_yields_no_evidence()
    {
        var src = new FakeDataSource();
        src.Events.Add(WheaEventAt(StutterQpc + Clock.MsToTicks(20_000))); // 20 s away, well outside +/-5 s

        var window = NewEngine().Analyze(NewStutter(), src);

        window.Correlations.Should().NotContain(c => c.SignalType == SignalCatalog.Whea);
    }

    [Fact]
    public void An_event_exactly_at_the_preRoll_edge_is_still_scored_low()
    {
        var opt = new CorrelationOptions(); // PreRollSeconds = 5
        var src = new FakeDataSource();
        src.Events.Add(WheaEventAt(StutterQpc - Clock.MsToTicks(opt.PreRollSeconds * 1000.0)));

        var window = NewEngine(opt).Analyze(NewStutter(), src);

        window.Correlations.Single(c => c.SignalType == SignalCatalog.Whea)
            .Score.Should().Be(CorrelationScore.Low);
    }

    [Fact]
    public void A_metric_breaching_the_injected_baseline_yields_at_least_medium_even_when_far_in_time()
    {
        var opt = new CorrelationOptions(); // MediumProximityMs = 250
        var baseline = new BaselineStats(opt, Clock.Frequency);
        for (int i = 0; i < 12; i++)
            baseline.Observe("disk.read.latency.ms", 5.0, StutterQpc - Clock.MsToTicks(1000) + i);

        var src = new FakeDataSource();
        // 300 ms away -> proximity alone would be LOW; the baseline breach must lift it to >= MEDIUM.
        src.Samples.Add(new MetricSample(StutterQpc + Clock.MsToTicks(300), "disk.read.latency.ms", "", 500.0));

        var window = NewEngine(opt).Analyze(NewStutter(), src, baseline: baseline);

        var disk = window.Correlations.Single(c => c.SignalType == SignalCatalog.DiskLatency);
        disk.Score.Should().BeOneOf(CorrelationScore.Medium, CorrelationScore.High);
        ((int)disk.Score).Should().BeGreaterThanOrEqualTo((int)CorrelationScore.Medium);
    }

    [Fact]
    public void Base_rate_from_the_provider_flows_onto_the_correlation()
    {
        var baseRates = Substitute.For<IBaseRateProvider>();
        baseRates.GetBaseRate(Arg.Any<string>()).Returns(0.0);
        baseRates.GetBaseRate(SignalCatalog.Whea).Returns(0.42);

        var src = new FakeDataSource();
        src.Events.Add(WheaEventAt(StutterQpc + Clock.MsToTicks(5)));

        var window = NewEngine().Analyze(NewStutter(), src, baseline: null, baseRates: baseRates);

        window.Correlations.Single(c => c.SignalType == SignalCatalog.Whea)
            .BaseRate.Should().Be(0.42);
    }

    [Fact]
    public void The_window_bounds_are_the_stutter_plus_minus_the_configured_rolls()
    {
        var opt = new CorrelationOptions { PreRollSeconds = 5, PostRollSeconds = 5 };
        var window = NewEngine(opt).Analyze(NewStutter(), new FakeDataSource());

        window.FromQpc.Should().Be(StutterQpc - Clock.MsToTicks(5000));
        window.ToQpc.Should().Be(StutterQpc + Clock.MsToTicks(5000));
    }

    // The nearest process snapshot is carried through the window untouched: it feeds the
    // report's "Top processes" line. Every other test leaves FakeDataSource.Snapshot null,
    // so without this one the non-null branch is never exercised.
    [Fact]
    public void The_nearest_process_snapshot_is_carried_into_the_window()
    {
        var snapshot = new ProcessSnapshot(
            StutterQpc, DateTime.UnixEpoch, SnapshotTrigger.AutoStutter,
            new[]
            {
                new ProcessSnapshotRow(4321, "game.exe", 42.0, 1_048_576, 524_288, 10, 20, 8, 40, 3, 1.5, 2.5, false, true),
            });
        var src = new FakeDataSource { Snapshot = snapshot };

        var window = NewEngine().Analyze(NewStutter(), src);

        window.Snapshot.Should().BeSameAs(snapshot);
        window.Snapshot!.Rows.Should().ContainSingle().Which.Name.Should().Be("game.exe");
    }

    [Fact]
    public void A_missing_process_snapshot_leaves_the_window_snapshot_null()
    {
        var window = NewEngine().Analyze(NewStutter(), new FakeDataSource());

        window.Snapshot.Should().BeNull();
    }
}
