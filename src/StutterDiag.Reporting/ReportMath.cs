namespace StutterDiag.Reporting;

/// <summary>Small numeric helpers for the report aggregates. Deterministic, allocation-light.</summary>
internal static class ReportMath
{
    /// <summary>
    /// Linear-interpolated percentile (<paramref name="p"/> in 0..1) of <paramref name="values"/>.
    /// Returns <c>null</c> for an empty input so the report can render "Unavailable".
    /// </summary>
    public static double? Percentile(IReadOnlyList<double> values, double p)
    {
        if (values is null || values.Count == 0) return null;
        if (values.Count == 1) return values[0];

        var sorted = values.ToArray();
        Array.Sort(sorted);

        double clamped = p < 0 ? 0 : p > 1 ? 1 : p;
        double rank = clamped * (sorted.Length - 1);
        int lo = (int)Math.Floor(rank);
        int hi = (int)Math.Ceiling(rank);
        if (lo == hi) return sorted[lo];
        return sorted[lo] + (sorted[hi] - sorted[lo]) * (rank - lo);
    }

    /// <summary>Arithmetic mean, or <c>null</c> for an empty input.</summary>
    public static double? Mean(IReadOnlyList<double> values)
        => values is null || values.Count == 0 ? null : values.Average();
}
