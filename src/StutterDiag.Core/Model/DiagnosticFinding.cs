namespace StutterDiag.Core.Model;

/// <summary>
/// One line of the automatic diagnostic. Strictly split into observation / hypothesis /
/// not-proven so the report can never be read as a causal claim. Produced by
/// <c>DiagnosticHypothesisEngine</c> by aggregating <see cref="StutterCorrelation"/>s
/// across a whole session.
/// </summary>
public sealed record DiagnosticFinding(
    CorrelationScore Score,
    string SignalType,
    string Observation,
    string Hypothesis,
    string NotProven,
    int CorrelatedStutters,
    int TotalStutters,
    double BaseRatePercent)
{
    /// <summary>Fraction of this session's stutters this signal co-occurred with.</summary>
    public double CorrelatedFraction =>
        TotalStutters == 0 ? 0 : (double)CorrelatedStutters / TotalStutters;

    /// <summary>
    /// How much more often the signal appears in stutter windows than in random windows.
    /// &gt; 1 means over-represented near stutters; ~1 means no temporal association.
    /// </summary>
    public double Lift =>
        BaseRatePercent <= 0 ? double.PositiveInfinity : CorrelatedFraction * 100.0 / BaseRatePercent;
}
