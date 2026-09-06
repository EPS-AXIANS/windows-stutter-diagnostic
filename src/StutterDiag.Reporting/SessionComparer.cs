using System.Globalization;
using StutterDiag.Core.Model;

namespace StutterDiag.Reporting;

/// <summary>
/// Builds a <see cref="SessionComparison"/> from two loaded <see cref="SessionReport"/>s.
/// Every row is purely descriptive; the <c>Delta</c> column is a plain difference or ratio
/// string. The comparison never attributes a difference to any cause — see <see cref="HeaderNote"/>.
/// </summary>
public sealed class SessionComparer
{
    /// <summary>Header shown above the comparison table. States the descriptive-only contract.</summary>
    public const string HeaderNote =
        "This comparison is descriptive only. It lists how the two sessions differ; it does not "
        + "attribute any difference to the TPM, a driver, or any other component.";

    /// <summary>Produces the A/B comparison. A = <paramref name="a"/>, B = <paramref name="b"/>.</summary>
    public SessionComparison Compare(SessionReport a, SessionReport b)
    {
        if (a is null) throw new ArgumentNullException(nameof(a));
        if (b is null) throw new ArgumentNullException(nameof(b));

        var ca = a.Counts;
        var cb = b.Counts;

        var rows = new List<ComparisonRow>
        {
            new("Comparison basis", HeaderNote, HeaderNote, "descriptive only"),
            new("Monitoring duration",
                ReportDataLoader.FormatDuration(ca.MonitoringDuration),
                ReportDataLoader.FormatDuration(cb.MonitoringDuration),
                DeltaDuration(ca.MonitoringDuration, cb.MonitoringDuration)),
            IntRow("Stutters (total)", ca.StuttersTotal, cb.StuttersTotal),
            NumRow("Stutters per hour", ca.StuttersPerHour, cb.StuttersPerHour, 2, "/h"),
            IntRow("Major or worse stutters",
                ca.MajorStutters + ca.SevereStutters + ca.CriticalStutters,
                cb.MajorStutters + cb.SevereStutters + cb.CriticalStutters),
            NullNumRow("Mean stutter duration", ca.MeanDurationMs, cb.MeanDurationMs, 1, " ms"),
            NullNumRow("p95 stutter duration", ca.P95DurationMs, cb.P95DurationMs, 1, " ms"),
            IntRow("TPM / TBS events", ca.TpmTbsEvents, cb.TpmTbsEvents),
            IntRow("WHEA events", ca.WheaEvents, cb.WheaEvents),
            IntRow("DPC spike count", ca.DpcSpikeCount, cb.DpcSpikeCount),
            IntRow("Distinct driver anomalies", ca.DistinctDriverAnomalies, cb.DistinctDriverAnomalies),
            NullNumRow("Mean CPU %", ca.MeanCpuPct, cb.MeanCpuPct, 1, "%"),
            NullNumRow("Mean GPU %", ca.MeanGpuPct, cb.MeanGpuPct, 1, "%"),
            NullNumRow("Storage latency p95", ca.StorageLatencyP95Ms, cb.StorageLatencyP95Ms, 1, " ms"),
            IntRow("Power-state change count", ca.PowerStateChangeCount, cb.PowerStateChangeCount),
        };

        for (int i = 0; i < 3; i++)
        {
            string va = i < ca.TopCorrelationSignals.Count ? RankText(ca.TopCorrelationSignals[i]) : "—";
            string vb = i < cb.TopCorrelationSignals.Count ? RankText(cb.TopCorrelationSignals[i]) : "—";
            rows.Add(new ComparisonRow($"Top correlation signal #{i + 1}", va, vb, "n/a"));
        }

        return new SessionComparison(a.Session, b.Session, rows);
    }

    private static string RankText(CorrelationRankingRow r)
        => $"{r.DisplayName} ({r.CorrelatedStutters}/{r.TotalStutters}, {r.ScoreLabel})";

    private static ComparisonRow IntRow(string metric, int a, int b)
        => new(metric,
            a.ToString(CultureInfo.InvariantCulture),
            b.ToString(CultureInfo.InvariantCulture),
            DeltaInt(a, b));

    private static ComparisonRow NumRow(string metric, double a, double b, int digits, string unit)
        => new(metric,
            a.ToString("F" + digits, CultureInfo.InvariantCulture) + unit,
            b.ToString("F" + digits, CultureInfo.InvariantCulture) + unit,
            DeltaNum(a, b, digits, unit));

    private static ComparisonRow NullNumRow(string metric, double? a, double? b, int digits, string unit)
        => new(metric,
            a.HasValue ? a.Value.ToString("F" + digits, CultureInfo.InvariantCulture) + unit : "Unavailable",
            b.HasValue ? b.Value.ToString("F" + digits, CultureInfo.InvariantCulture) + unit : "Unavailable",
            a.HasValue && b.HasValue ? DeltaNum(a.Value, b.Value, digits, unit) : "n/a");

    private static string DeltaInt(int a, int b)
    {
        int d = b - a;
        return d == 0 ? "0" : (d > 0 ? "+" : "") + d.ToString(CultureInfo.InvariantCulture);
    }

    private static string DeltaNum(double a, double b, int digits, string unit)
    {
        double d = b - a;
        string s = d.ToString("F" + digits, CultureInfo.InvariantCulture);
        if (d > 0) s = "+" + s;
        return s + unit;
    }

    private static string DeltaDuration(TimeSpan a, TimeSpan b)
    {
        TimeSpan d = b - a;
        string sign = d.Ticks >= 0 ? "+" : "-";
        return sign + ReportDataLoader.FormatDuration(d.Duration());
    }
}
