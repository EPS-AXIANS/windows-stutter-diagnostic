using FluentAssertions;
using StutterDiag.Core.Config;
using StutterDiag.Core.Correlation;
using StutterDiag.Core.Model;
using StutterDiag.Core.Time;
using Xunit;

namespace StutterDiag.Core.Tests;

public sealed class StutterAggregatorTests
{
    private static readonly QpcClock Clock = new();
    private static readonly StutterThresholdOptions Thresholds = new(); // 50 / 100 / 250 / 1000

    private static (StutterAggregator agg, List<Stutter> finalized) New()
    {
        var agg = new StutterAggregator(Clock, Thresholds);
        var list = new List<Stutter>();
        agg.StutterAggregated += (_, s) => list.Add(s);
        return (agg, list);
    }

    private static StutterCandidate Candidate(long qpc, double durMs, DetectorKind kind,
        DetectionConfidence conf = DetectionConfidence.Low)
        => new(qpc, durMs, kind, conf);

    [Fact]
    public void Two_candidates_within_the_merge_window_collapse_into_one()
    {
        var (agg, finalized) = New();
        long t = 5_000_000;

        agg.Add(Candidate(t, 60, DetectorKind.Heartbeat));
        agg.Add(Candidate(t + Clock.MsToTicks(10), 120, DetectorKind.Frametime));
        finalized.Should().BeEmpty("the cluster is still open");

        agg.Flush(t + Clock.MsToTicks(500));

        finalized.Should().ContainSingle();
        var s = finalized[0];
        s.DurationMs.Should().Be(120, "the longest candidate wins");
        s.CorroboratedBy.Should().BeEquivalentTo(new[] { DetectorKind.Heartbeat, DetectorKind.Frametime });
        s.Detector.Should().Be(DetectorKind.Frametime);
        s.Severity.Should().Be(StutterSeverity.Major); // 120 ms -> [100, 250)
        s.Confidence.Should().Be(DetectionConfidence.Medium); // two detectors bump Low -> Medium
    }

    [Fact]
    public void Candidates_further_apart_than_the_merge_window_do_not_merge()
    {
        var (agg, finalized) = New();
        long t = 9_000_000;

        agg.Add(Candidate(t, 55, DetectorKind.Heartbeat));
        agg.Add(Candidate(t + Clock.MsToTicks(100), 70, DetectorKind.Frametime)); // 100 ms > 30 ms window

        finalized.Should().ContainSingle("the first cluster is finalized when the far candidate arrives");
        finalized[0].DurationMs.Should().Be(55);
        finalized[0].CorroboratedBy.Should().BeEquivalentTo(new[] { DetectorKind.Heartbeat });

        agg.Flush(t + Clock.MsToTicks(1_000));
        finalized.Should().HaveCount(2);
        finalized[1].DurationMs.Should().Be(70);
    }

    [Theory]
    [InlineData(49, StutterSeverity.Micro)]
    [InlineData(100, StutterSeverity.Major)]
    [InlineData(249, StutterSeverity.Major)]
    [InlineData(250, StutterSeverity.Severe)]
    [InlineData(999, StutterSeverity.Severe)]
    [InlineData(1000, StutterSeverity.Critical)]
    public void Severity_is_classified_at_each_threshold(double durationMs, StutterSeverity expected)
        => new StutterAggregator(Clock, Thresholds).Classify(durationMs).Should().Be(expected);

    [Fact]
    public void A_user_marked_candidate_finalizes_with_high_confidence()
    {
        var (agg, finalized) = New();
        long t = 3_000_000;

        agg.Add(Candidate(t, 30, DetectorKind.UserMarked));
        agg.Flush(t + Clock.MsToTicks(200));

        finalized.Should().ContainSingle();
        finalized[0].UserMarked.Should().BeTrue();
        finalized[0].Confidence.Should().Be(DetectionConfidence.High);
    }

    [Fact]
    public void Flush_only_finalizes_once_the_merge_window_has_elapsed()
    {
        var (agg, finalized) = New();
        long t = 1_000_000;

        agg.Add(Candidate(t, 80, DetectorKind.Heartbeat));

        agg.Flush(t + Clock.MsToTicks(20)); // still inside the 30 ms window
        finalized.Should().BeEmpty();

        agg.Flush(t + Clock.MsToTicks(40)); // window elapsed
        finalized.Should().ContainSingle();
        finalized[0].DurationMs.Should().Be(80);
    }
}
