namespace StutterDiag.Core.Correlation;

/// <summary>
/// Supplies, for a given signal type, the fraction (0..1) of random non-stutter windows in
/// which that signal also appears. The correlation engine attaches this to every
/// <c>StutterCorrelation</c> so the report can show a lift ratio rather than a bare count.
/// </summary>
public interface IBaseRateProvider
{
    double GetBaseRate(string signalType);
}

/// <summary>Base-rate provider that always returns 0 (used when no sampling has run yet).</summary>
public sealed class NullBaseRateProvider : IBaseRateProvider
{
    public static readonly NullBaseRateProvider Instance = new();
    public double GetBaseRate(string signalType) => 0.0;
}
