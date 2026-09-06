using StutterDiag.Core.Abstractions;
using StutterDiag.Core.Config;
using StutterDiag.Core.Model;
using StutterDiag.Core.Time;

namespace StutterDiag.Core.Correlation;

/// <summary>
/// Periodically inspects a random <c>BaseRateWindowSeconds</c>-wide window that is <b>not</b>
/// near any stutter, and records which signal types appear in it. Over a run this yields
/// P(signal | random window), the denominator that turns "co-occurred with 9/47 stutters"
/// into a meaningful lift ratio.
/// </summary>
public sealed class BaseRateSampler : IBaseRateProvider
{
    private readonly QpcClock _clock;
    private readonly ICorrelationDataSource _source;
    private readonly long _windowTicks;
    private readonly object _gate = new();
    private readonly Dictionary<string, int> _hits = new(StringComparer.Ordinal);
    private int _windows;

    public BaseRateSampler(QpcClock clock, ICorrelationDataSource source, CorrelationOptions options)
    {
        _clock = clock;
        _source = source;
        _windowTicks = clock.MsToTicks(options.BaseRateWindowSeconds * 1000.0);
    }

    /// <summary>Sample the window ending <paramref name="offsetTicksAgo"/> before <c>now</c>.</summary>
    public void Sample(long nowQpc, long offsetTicksAgo)
    {
        long to = nowQpc - offsetTicksAgo;
        long fromWindow = to - _windowTicks;

        var present = new HashSet<string>(StringComparer.Ordinal);
        foreach (var e in _source.GetEvents(fromWindow, to))
        {
            var s = SignalCatalog.Classify(e);
            if (s is not null) present.Add(s);
        }
        foreach (var stat in _source.GetDpcIsrStats(fromWindow, to))
        {
            if (stat.MaxMs >= 1.0)
                present.Add(stat.Kind == "ISR" ? SignalCatalog.Isr(stat.Driver) : SignalCatalog.Dpc(stat.Driver));
        }

        lock (_gate)
        {
            _windows++;
            foreach (var s in present)
                _hits[s] = _hits.GetValueOrDefault(s) + 1;
        }
    }

    public double GetBaseRate(string signalType)
    {
        lock (_gate)
        {
            if (_windows == 0) return 0.0;
            return _hits.GetValueOrDefault(signalType) / (double)_windows;
        }
    }

    public int SampledWindows { get { lock (_gate) return _windows; } }
}
