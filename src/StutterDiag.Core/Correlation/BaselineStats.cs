using StutterDiag.Core.Config;

namespace StutterDiag.Core.Correlation;

/// <summary>
/// Per-metric adaptive baseline: a time-bounded rolling window of values, summarised as
/// median + MAD (median absolute deviation — robust to outliers). Nothing here is tuned
/// for a particular PC model; the baseline is learned per machine, per run.
/// </summary>
public sealed class BaselineStats
{
    private readonly object _gate = new();
    private readonly Dictionary<string, Deque> _series = new(StringComparer.Ordinal);
    private readonly long _windowTicks;
    private readonly double _madK;

    public BaselineStats(CorrelationOptions options, long qpcFrequency)
    {
        _windowTicks = (long)(options.BaselineWindowMinutes * 60 * qpcFrequency);
        _madK = options.MadK;
    }

    public void Observe(string key, double value, long qpc)
    {
        lock (_gate)
        {
            if (!_series.TryGetValue(key, out var d))
                _series[key] = d = new Deque();
            d.Add(qpc, value);
            d.EvictOlderThan(qpc - _windowTicks);
        }
    }

    public bool TryGetBaseline(string key, out double median, out double mad, out int n)
    {
        lock (_gate)
        {
            if (_series.TryGetValue(key, out var d) && d.Count >= 8)
            {
                (median, mad) = d.MedianAndMad();
                n = d.Count;
                return true;
            }
        }
        median = mad = 0;
        n = 0;
        return false;
    }

    /// <summary>True if <paramref name="value"/> is more than <c>MadK</c> MADs above the median.</summary>
    public bool IsHigh(string key, double value)
        => TryGetBaseline(key, out var m, out var mad, out _) && value > m + _madK * Math.Max(mad, 1e-9);

    /// <summary>True if <paramref name="value"/> is more than <c>MadK</c> MADs below the median.</summary>
    public bool IsLow(string key, double value)
        => TryGetBaseline(key, out var m, out var mad, out _) && value < m - _madK * Math.Max(mad, 1e-9);

    public bool IsAnomaly(string key, double value) => IsHigh(key, value) || IsLow(key, value);

    private sealed class Deque
    {
        private readonly LinkedList<(long Qpc, double Value)> _list = new();

        public int Count => _list.Count;

        public void Add(long qpc, double value) => _list.AddLast((qpc, value));

        public void EvictOlderThan(long horizon)
        {
            while (_list.First is { } f && f.Value.Qpc < horizon)
                _list.RemoveFirst();
        }

        public (double Median, double Mad) MedianAndMad()
        {
            var values = new double[_list.Count];
            int i = 0;
            foreach (var (_, v) in _list) values[i++] = v;
            Array.Sort(values);
            double median = Median(values);

            var dev = new double[values.Length];
            for (int j = 0; j < values.Length; j++) dev[j] = Math.Abs(values[j] - median);
            Array.Sort(dev);
            // 1.4826 scales MAD to be a consistent estimator of the standard deviation for normal data.
            double mad = Median(dev) * 1.4826;
            return (median, mad);
        }

        private static double Median(double[] sorted)
        {
            int n = sorted.Length;
            if (n == 0) return 0;
            return n % 2 == 1 ? sorted[n / 2] : (sorted[n / 2 - 1] + sorted[n / 2]) / 2.0;
        }
    }
}
