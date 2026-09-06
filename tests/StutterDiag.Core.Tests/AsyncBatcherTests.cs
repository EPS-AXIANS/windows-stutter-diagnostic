using System.Collections.Concurrent;
using FluentAssertions;
using StutterDiag.Core.Util;
using Xunit;

namespace StutterDiag.Core.Tests;

public sealed class AsyncBatcherTests
{
    private sealed class Sink
    {
        public readonly ConcurrentQueue<int> BatchSizes = new();
        public readonly ConcurrentBag<int> Items = new();
        public int Count => Items.Count;

        public Task Flush(IReadOnlyList<int> batch, CancellationToken ct)
        {
            BatchSizes.Enqueue(batch.Count);
            foreach (var i in batch) Items.Add(i);
            return Task.CompletedTask;
        }
    }

    private static async Task WaitUntil(Func<bool> condition, int timeoutMs = 5_000)
    {
        var start = Environment.TickCount64;
        while (!condition() && Environment.TickCount64 - start < timeoutMs)
            await Task.Delay(25);
    }

    [Fact]
    public async Task Buffered_items_are_delivered_on_an_explicit_flush()
    {
        var sink = new Sink();
        await using var batcher = new AsyncBatcher<int>(sink.Flush, batchSize: 512, interval: TimeSpan.FromSeconds(30));

        for (int i = 0; i < 3; i++) batcher.Enqueue(i);
        await batcher.FlushAsync();

        sink.Items.Should().BeEquivalentTo(new[] { 0, 1, 2 });
    }

    [Fact]
    public async Task A_large_input_is_delivered_in_batches_no_larger_than_batchSize()
    {
        var sink = new Sink();
        await using (var batcher = new AsyncBatcher<int>(
            sink.Flush, capacity: 100_000, batchSize: 100, interval: TimeSpan.FromMilliseconds(50)))
        {
            for (int i = 0; i < 1_000; i++) batcher.Enqueue(i);
            await WaitUntil(() => sink.Count >= 1_000);
        }

        sink.Count.Should().Be(1_000);
        sink.Items.Distinct().Should().HaveCount(1_000);
        sink.BatchSizes.Should().OnlyContain(n => n <= 100);
        sink.BatchSizes.Count.Should().BeGreaterThan(1);
    }

    [Fact]
    public async Task Dispose_drains_the_remaining_buffered_items()
    {
        var sink = new Sink();
        var batcher = new AsyncBatcher<int>(sink.Flush, batchSize: 512, interval: TimeSpan.FromSeconds(30));

        for (int i = 0; i < 7; i++) batcher.Enqueue(i);
        await batcher.DisposeAsync();

        sink.Items.Should().BeEquivalentTo(new[] { 0, 1, 2, 3, 4, 5, 6 });
    }

    [Fact]
    public async Task Enqueue_after_dispose_increments_DroppedCount()
    {
        var sink = new Sink();
        var batcher = new AsyncBatcher<int>(sink.Flush);
        await batcher.DisposeAsync();

        batcher.Enqueue(1);
        batcher.Enqueue(2);

        batcher.DroppedCount.Should().Be(2);
    }

    [Fact]
    public async Task Capacity_overflow_is_counted_via_DroppedCount()
    {
        // A flush callback that blocks until released, so the reader can't drain the channel
        // and a burst larger than capacity forces DropOldest evictions.
        var gate = new TaskCompletionSource();
        Task Flush(IReadOnlyList<int> _, CancellationToken __) => gate.Task;

        await using var batcher = new AsyncBatcher<int>(Flush, capacity: 8, batchSize: 4,
            interval: TimeSpan.FromMilliseconds(20));

        for (int i = 0; i < 200; i++) batcher.Enqueue(i);
        await WaitUntil(() => batcher.DroppedCount > 0);

        batcher.DroppedCount.Should().BeGreaterThan(0);
        gate.SetResult();
    }
}
