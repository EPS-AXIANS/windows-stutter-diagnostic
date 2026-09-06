using StutterDiag.Core.Config;
using StutterDiag.Core.Model;

namespace StutterDiag.Core.Correlation;

/// <summary>
/// Maps a temporal distance (ms between a signal and the stutter) to a
/// <see cref="CorrelationScore"/>. This is proximity only — it is never a causal weight.
/// </summary>
public sealed class ProximityScorer
{
    private readonly double _highMs;
    private readonly double _mediumMs;
    private readonly double _windowMs;

    public ProximityScorer(CorrelationOptions o)
    {
        _highMs = o.HighProximityMs;
        _mediumMs = o.MediumProximityMs;
        _windowMs = (o.PreRollSeconds + o.PostRollSeconds) * 1000.0;
    }

    /// <param name="proximityMs">Absolute ms between the signal and the stutter start (0 = simultaneous / overlapping).</param>
    /// <param name="metricIsAnomalous">True if the signal is a metric that also breached its adaptive baseline in the window.</param>
    public CorrelationScore Score(double proximityMs, bool metricIsAnomalous = false)
    {
        double p = Math.Abs(proximityMs);
        if (p <= _highMs) return CorrelationScore.High;
        if (p <= _mediumMs || metricIsAnomalous) return CorrelationScore.Medium;
        if (p <= _windowMs) return CorrelationScore.Low;
        return CorrelationScore.NoEvidence;
    }
}
