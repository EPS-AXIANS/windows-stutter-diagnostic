using FluentAssertions;
using StutterDiag.Core.Util;
using Xunit;

namespace StutterDiag.Core.Tests;

public sealed class BoundedRingBufferTests
{
    private static BoundedRingBuffer<long> New(int capacity)
        => new(capacity, x => x);

    [Fact]
    public void At_capacity_the_oldest_item_is_overwritten()
    {
        var buf = New(3);
        buf.Add(1);
        buf.Add(2);
        buf.Add(3);
        buf.Add(4); // evicts 1

        buf.Count.Should().Be(3);
        buf.Snapshot().Should().Equal(2L, 3L, 4L);
    }

    [Fact]
    public void Query_is_inclusive_on_both_bounds()
    {
        var buf = New(10);
        foreach (var t in new long[] { 10, 20, 30, 40 }) buf.Add(t);

        buf.Query(20, 30).Should().Equal(20L, 30L);
        buf.Query(0, 100).Should().Equal(10L, 20L, 30L, 40L);
        buf.Query(25, 25).Should().BeEmpty();
    }

    [Fact]
    public void EvictOlderThan_drops_everything_before_the_horizon()
    {
        var buf = New(10);
        foreach (var t in new long[] { 10, 20, 30 }) buf.Add(t);

        buf.EvictOlderThan(25); // 10 and 20 go, 30 stays (>= horizon)

        buf.Count.Should().Be(1);
        buf.Snapshot().Should().Equal(30L);
    }

    [Fact]
    public void Capacity_must_be_positive()
    {
        var act = () => new BoundedRingBuffer<long>(0, x => x);
        act.Should().Throw<ArgumentOutOfRangeException>();
    }
}
