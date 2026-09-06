using System.Threading.Channels;

namespace StutterDiag.Core.Util;

/// <summary>
/// Buffers high-frequency items and flushes them to <paramref name="flush"/> in batches:
/// when <c>batchSize</c> is reached, every <c>interval</c>, or on an explicit
/// <see cref="FlushAsync"/> (which completes within one <c>interval</c>). The item channel
/// is bounded and drops the oldest entry when full (incrementing <see cref="DroppedCount"/>)
/// so a monitor's hot path never blocks.
/// </summary>
public sealed class AsyncBatcher<T> : IAsyncDisposable
{
    private readonly Channel<T> _items;
    private readonly Func<IReadOnlyList<T>, CancellationToken, Task> _flush;
    private readonly int _batchSize;
    private readonly TimeSpan _interval;
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _worker;
    private readonly List<TaskCompletionSource<bool>> _flushWaiters = new();
    private readonly object _waiterGate = new();
    private long _dropped;

    public AsyncBatcher(
        Func<IReadOnlyList<T>, CancellationToken, Task> flush,
        int capacity = 16384,
        int batchSize = 512,
        TimeSpan? interval = null)
    {
        _flush = flush;
        _batchSize = batchSize;
        _interval = interval ?? TimeSpan.FromSeconds(2);
        // itemDropped fires for every entry discarded by DropOldest while the writer is open —
        // that is the silent back-pressure loss we must surface via DroppedCount.
        _items = Channel.CreateBounded<T>(
            new BoundedChannelOptions(capacity)
            {
                FullMode = BoundedChannelFullMode.DropOldest,
                SingleReader = true,
                SingleWriter = false
            },
            itemDropped: _ => Interlocked.Increment(ref _dropped));
        _worker = Task.Run(RunAsync);
    }

    public long DroppedCount => Interlocked.Read(ref _dropped);

    public void Enqueue(T item)
    {
        // TryWrite returns false only once the writer is completed (i.e. after DisposeAsync);
        // capacity overflow is DropOldest and is counted by the itemDropped callback above.
        if (!_items.Writer.TryWrite(item))
            Interlocked.Increment(ref _dropped);
    }

    /// <summary>Request a flush of everything buffered so far and await its completion.</summary>
    public Task FlushAsync()
    {
        if (_cts.IsCancellationRequested) return Task.CompletedTask;
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_waiterGate) _flushWaiters.Add(tcs);
        return tcs.Task;
    }

    private void ReleaseWaiters(Exception? error = null)
    {
        TaskCompletionSource<bool>[] waiters;
        lock (_waiterGate)
        {
            if (_flushWaiters.Count == 0) return;
            waiters = _flushWaiters.ToArray();
            _flushWaiters.Clear();
        }
        foreach (var w in waiters)
        {
            if (error is null) w.TrySetResult(true);
            else w.TrySetException(error);
        }
    }

    private async Task RunAsync()
    {
        var buffer = new List<T>(_batchSize);
        var reader = _items.Reader;

        while (!_cts.IsCancellationRequested)
        {
            bool flushRequested;
            lock (_waiterGate) flushRequested = _flushWaiters.Count > 0;

            if (!flushRequested)
            {
                using var timerCts = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
                timerCts.CancelAfter(_interval);
                try
                {
                    await reader.WaitToReadAsync(timerCts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) { /* interval elapsed or shutting down */ }
            }

            while (buffer.Count < _batchSize && reader.TryRead(out var item))
                buffer.Add(item);

            try
            {
                if (buffer.Count > 0)
                {
                    await _flush(buffer, _cts.Token).ConfigureAwait(false);
                    buffer.Clear();
                }
                ReleaseWaiters();
            }
            catch (OperationCanceledException)
            {
                ReleaseWaiters();
                break;
            }
            catch (Exception ex)
            {
                buffer.Clear();
                ReleaseWaiters(ex);
            }
        }

        while (reader.TryRead(out var item)) buffer.Add(item);
        if (buffer.Count > 0)
        {
            try { await _flush(buffer, CancellationToken.None).ConfigureAwait(false); }
            catch { /* best effort on shutdown */ }
        }
        ReleaseWaiters();
    }

    public async ValueTask DisposeAsync()
    {
        _items.Writer.TryComplete();
        _cts.Cancel();
        try { await _worker.ConfigureAwait(false); }
        catch { /* ignore */ }
        ReleaseWaiters();
        _cts.Dispose();
    }
}
