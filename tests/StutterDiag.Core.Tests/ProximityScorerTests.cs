using FluentAssertions;
using StutterDiag.Core.Config;
using StutterDiag.Core.Correlation;
using StutterDiag.Core.Model;
using Xunit;

namespace StutterDiag.Core.Tests;

public sealed class ProximityScorerTests
{
    // Defaults: HighProximityMs = 25, MediumProximityMs = 250, window = (5 + 5) s = 10_000 ms
    private static readonly ProximityScorer Scorer = new(new CorrelationOptions());

    [Theory]
    [InlineData(0, CorrelationScore.High)]
    [InlineData(25, CorrelationScore.High)]         // inclusive upper bound
    [InlineData(-25, CorrelationScore.High)]        // absolute value is used
    [InlineData(25.001, CorrelationScore.Medium)]
    [InlineData(250, CorrelationScore.Medium)]      // inclusive upper bound
    [InlineData(250.001, CorrelationScore.Low)]
    [InlineData(10_000, CorrelationScore.Low)]      // inclusive edge of the window
    [InlineData(10_000.001, CorrelationScore.NoEvidence)]
    public void Score_maps_distance_to_the_table(double proximityMs, CorrelationScore expected)
        => Scorer.Score(proximityMs).Should().Be(expected);

    [Fact]
    public void An_anomalous_metric_bumps_a_LOW_proximity_up_to_MEDIUM()
    {
        Scorer.Score(500, metricIsAnomalous: false).Should().Be(CorrelationScore.Low);
        Scorer.Score(500, metricIsAnomalous: true).Should().Be(CorrelationScore.Medium);
    }

    [Fact]
    public void An_anomalous_flag_never_downgrades_a_HIGH_proximity()
        => Scorer.Score(5, metricIsAnomalous: true).Should().Be(CorrelationScore.High);

    [Fact]
    public void An_anomalous_metric_forces_at_least_MEDIUM_even_past_the_window_edge()
        => Scorer.Score(50_000, metricIsAnomalous: true).Should().Be(CorrelationScore.Medium);
}
