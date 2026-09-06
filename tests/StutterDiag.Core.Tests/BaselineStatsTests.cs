using FluentAssertions;
using StutterDiag.Core.Config;
using StutterDiag.Core.Correlation;
using Xunit;

namespace StutterDiag.Core.Tests;

public sealed class BaselineStatsTests
{
    private const long Freq = 1_000_000;
    private static readonly CorrelationOptions Opt = new(); // BaselineWindowMinutes = 15, MadK = 4.0

    private static BaselineStats New() => new(Opt, Freq);

    [Fact]
    public void Median_and_mad_match_a_known_series()
    {
        var b = New();
        // values 1..9, taken close together so nothing is evicted
        for (int i = 1; i <= 9; i++) b.Observe("m", i, qpc: 1_000 + i);

        b.TryGetBaseline("m", out var median, out var mad, out var n).Should().BeTrue();
        n.Should().Be(9);
        median.Should().Be(5.0);
        // deviations sorted: 0,1,1,2,2,3,3,4,4 -> median 2 -> * 1.4826
        mad.Should().BeApproximately(2.0 * 1.4826, 1e-9);
    }

    [Fact]
    public void IsHigh_and_IsLow_fire_around_median_plus_minus_madK_times_mad()
    {
        var b = New();
        for (int i = 1; i <= 9; i++) b.Observe("m", i, qpc: 1_000 + i);
        // median 5, mad ~2.9652, threshold offset = 4 * 2.9652 ~= 11.8608

        b.IsHigh("m", 17).Should().BeTrue();
        b.IsHigh("m", 16).Should().BeFalse();

        b.IsLow("m", -7).Should().BeTrue();
        b.IsLow("m", -6).Should().BeFalse();

        b.IsAnomaly("m", 17).Should().BeTrue();
        b.IsAnomaly("m", 5).Should().BeFalse();
    }

    [Fact]
    public void Fewer_than_eight_samples_means_no_baseline()
    {
        var b = New();
        for (int i = 0; i < 7; i++) b.Observe("m", 10, qpc: 100 + i);

        b.TryGetBaseline("m", out _, out _, out _).Should().BeFalse();
        b.IsHigh("m", 10_000).Should().BeFalse();
        b.IsLow("m", -10_000).Should().BeFalse();
    }

    [Fact]
    public void Samples_older_than_the_window_are_evicted()
    {
        var b = New();
        long window = (long)(Opt.BaselineWindowMinutes * 60 * Freq);

        // old cluster around qpc 0
        for (int i = 0; i < 10; i++) b.Observe("m", 1_000.0, qpc: i);

        // fresh cluster far in the future -> observing these evicts the old ones
        long baseQpc = window * 10;
        for (int i = 0; i < 8; i++) b.Observe("m", 3.0, qpc: baseQpc + i);

        b.TryGetBaseline("m", out var median, out _, out var n).Should().BeTrue();
        n.Should().Be(8);
        median.Should().Be(3.0); // old value 1000 no longer contributes
    }
}
