using FluentAssertions;
using StutterDiag.Core.Time;
using Xunit;

namespace StutterDiag.Core.Tests;

public sealed class QpcClockTests
{
    [Fact]
    public void GetTimestamp_is_monotonic_non_decreasing_over_a_spin()
    {
        var clock = new QpcClock();
        long prev = clock.GetTimestamp();

        for (int i = 0; i < 200_000; i++)
        {
            long now = clock.GetTimestamp();
            now.Should().BeGreaterThanOrEqualTo(prev);
            prev = now;
        }
    }

    [Fact]
    public void Frequency_is_positive()
        => new QpcClock().Frequency.Should().BeGreaterThan(0);

    [Fact]
    public void QpcToUtc_and_UtcToQpc_round_trip_within_a_few_milliseconds()
    {
        var clock = new QpcClock();
        var t = new DateTime(2026, 9, 6, 12, 0, 0, DateTimeKind.Utc);

        var round = clock.QpcToUtc(clock.UtcToQpc(t));

        (round - t).Duration().Should().BeLessThan(TimeSpan.FromMilliseconds(5));
    }

    [Fact]
    public void MsToTicks_and_TicksToMs_round_trip()
    {
        var clock = new QpcClock();
        clock.TicksToMs(clock.MsToTicks(1_000)).Should().BeApproximately(1_000, 1.0);
    }

    [Fact]
    public void Reanchor_keeps_UtcNow_close_to_wall_clock()
    {
        var clock = new QpcClock();
        clock.Reanchor();

        (clock.UtcNow - DateTime.UtcNow).Duration().Should().BeLessThan(TimeSpan.FromSeconds(1));
    }
}
