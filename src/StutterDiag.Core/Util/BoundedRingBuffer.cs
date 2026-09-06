namespace StutterDiag.Core.Util;

/// <summary>
/// Fixed-capacity, time-ordered ring buffer. One writer (the orchestrator's consumer task),
/// occasional readers (only when a stutter window is built). Items are assumed to be added
/// in roughly non-decreasing timestamp order; <see cref="Query"/> does a linear scan, which
/// is fine for the sizes involved (seconds of data).
/// </summary>
public sealed class BoundedRingBuffer<T>
{
    private readonly object _gate = new();
    private readonly T[] _items;
    private readonly Func<T, long> _timestampSelector;
    private int _head;      // index of oldest
    private int _count;

    public BoundedRingBuffer(int capacity, Func<T, long> timestampSelector)
    {
        if (capacity < 1) throw new ArgumentOutOfRangeException(nameof(capacity));
        _items = new T[capacity];
        _timestampSelector = timestampSelector;
    }

    public int Capacity => _items.Length;

    public int Count
    {
        get { lock (_gate) return _count; }
    }

    public void Add(T item)
    {
        lock (_gate)
        {
            int tail = (_head + _count) % _items.Length;
            if (_count == _items.Length)
            {
                _items[tail] = item;
                _head = (_head + 1) % _items.Length;   // overwrite oldest
            }
            else
            {
                _items[tail] = item;
                _count++;
            }
        }
    }

    /// <summary>Drop everything with a timestamp before <paramref name="horizonQpc"/>.</summary>
    public void EvictOlderThan(long horizonQpc)
    {
        lock (_gate)
        {
            while (_count > 0)
            {
                var oldest = _items[_head];
                if (_timestampSelector(oldest) >= horizonQpc) break;
                _items[_head] = default!;
                _head = (_head + 1) % _items.Length;
                _count--;
            }
        }
    }

    /// <summary>All items whose timestamp is within [fromQpc, toQpc], oldest first.</summary>
    public IReadOnlyList<T> Query(long fromQpc, long toQpc)
    {
        lock (_gate)
        {
            var result = new List<T>();
            for (int i = 0; i < _count; i++)
            {
                var item = _items[(_head + i) % _items.Length];
                long ts = _timestampSelector(item);
                if (ts >= fromQpc && ts <= toQpc) result.Add(item);
            }
            return result;
        }
    }

    public IReadOnlyList<T> Snapshot()
    {
        lock (_gate)
        {
            var result = new List<T>(_count);
            for (int i = 0; i < _count; i++)
                result.Add(_items[(_head + i) % _items.Length]);
            return result;
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            Array.Clear(_items);
            _head = 0;
            _count = 0;
        }
    }
}
