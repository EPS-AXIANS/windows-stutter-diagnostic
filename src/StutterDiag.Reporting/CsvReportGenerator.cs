using System.Globalization;
using System.Text;
using StutterDiag.Core.Abstractions;
using StutterDiag.Core.Model;

namespace StutterDiag.Reporting;

/// <summary>
/// <see cref="IReportGenerator"/> for <see cref="ReportFormat.Csv"/>. Writes a set of RFC 4180
/// CSVs: <c>stutters.csv</c>, <c>correlations.csv</c>, <c>events.csv</c>, <c>drivers.csv</c>,
/// <c>timeline.csv</c>, <c>whea.csv</c>. If <see cref="ReportRequest.OutputPath"/> is a file the
/// primary <c>stutters.csv</c> is written there and the siblings next to it; if a directory,
/// all files go into it. In compare mode each row carries a <c>session_id</c> column.
/// </summary>
public sealed class CsvReportGenerator : IReportGenerator
{
    private static readonly UTF8Encoding Utf8NoBom = new(false);

    private readonly ReportDataLoader _loader;

    public CsvReportGenerator(ReportDataLoader loader)
        => _loader = loader ?? throw new ArgumentNullException(nameof(loader));

    public ReportFormat Format => ReportFormat.Csv;

    public async Task<string> GenerateAsync(ReportRequest request, CancellationToken ct)
    {
        var model = await _loader.LoadAsync(request, ct).ConfigureAwait(false);
        var (dir, primary) = ReportPaths.ResolveCsvSet(request.OutputPath);
        Directory.CreateDirectory(dir);

        await File.WriteAllTextAsync(primary, StuttersCsv(model), Utf8NoBom, ct).ConfigureAwait(false);
        await WriteSiblingAsync(dir, "correlations.csv", CorrelationsCsv(model), ct).ConfigureAwait(false);
        await WriteSiblingAsync(dir, "events.csv", EventsCsv(model), ct).ConfigureAwait(false);
        await WriteSiblingAsync(dir, "drivers.csv", DriversCsv(model), ct).ConfigureAwait(false);
        await WriteSiblingAsync(dir, "timeline.csv", TimelineCsv(model), ct).ConfigureAwait(false);
        await WriteSiblingAsync(dir, "whea.csv", WheaCsv(model), ct).ConfigureAwait(false);

        return primary;
    }

    private static Task WriteSiblingAsync(string dir, string name, string content, CancellationToken ct)
        => File.WriteAllTextAsync(Path.Combine(dir, name), content, Utf8NoBom, ct);

    // ------------------------------------------------------------------ builders (also used by ZIP)

    internal static string StuttersCsv(ReportModel model)
    {
        var w = new CsvWriter();
        w.WriteRow("session_id", "stutter_index", "stutter_id", "timestamp_utc", "timestamp_qpc",
            "duration_ms", "severity", "detector", "confidence", "corroborated_by", "user_marked",
            "tpm_events", "cpu_avg_pct", "cpu_peak_pct", "gpu_avg_pct", "disk_latency_spike_ms",
            "dpc_peak_ms", "dpc_peak_driver", "whea", "kernel_power", "top_processes");

        foreach (var sr in ReportModelQuery.Sessions(model))
            foreach (var s in sr.Stutters)
                w.WriteRow(
                    I(sr.Session.Id), I(s.Index), I(s.Id),
                    D(s.TimestampUtc), I(s.TimestampQpc), N(s.DurationMs),
                    s.Severity.ToString(), s.Detector.ToString(), s.Confidence.ToString(),
                    string.Join("|", s.CorroboratedBy), B(s.UserMarked), I(s.TpmEventCount),
                    N(s.CpuAvgPct), N(s.CpuPeakPct), N(s.GpuAvgPct), N(s.DiskLatencySpikeMs),
                    N(s.DpcPeakMs), S(s.DpcPeakDriver), B(s.Whea), B(s.KernelPower),
                    string.Join(" | ", s.TopProcesses.Select(p => $"{p.Name} ({N(p.CpuPercent)}%)")));

        return w.ToString();
    }

    internal static string CorrelationsCsv(ReportModel model)
    {
        var w = new CsvWriter();
        w.WriteRow("session_id", "stutter_id", "stutter_index", "signal_type", "display_name",
            "proximity_ms", "score", "base_rate", "detail");

        foreach (var sr in ReportModelQuery.Sessions(model))
            foreach (var s in sr.Stutters)
                foreach (var c in s.PossibleCorrelations)
                    w.WriteRow(
                        I(sr.Session.Id), I(s.Id), I(s.Index),
                        c.SignalType, c.DisplayName, N(c.ProximityMs),
                        c.Score.ToString(), N(c.BaseRate), c.Detail);

        return w.ToString();
    }

    internal static string EventsCsv(ReportModel model)
    {
        var w = new CsvWriter();
        w.WriteRow("session_id", "timestamp_utc", "timestamp_qpc", "category", "severity",
            "source", "provider", "event_id", "message");

        foreach (var sr in ReportModelQuery.Sessions(model))
            foreach (var e in sr.Events)
                w.WriteRow(
                    I(sr.Session.Id), D(e.TimestampUtc), I(e.TimestampQpc),
                    e.Category.ToString(), e.Severity.ToString(),
                    e.Source, S(e.Provider),
                    e.EventId?.ToString(CultureInfo.InvariantCulture) ?? "",
                    e.Message);

        return w.ToString();
    }

    internal static string DriversCsv(ReportModel model)
    {
        var w = new CsvWriter();
        w.WriteRow("session_id", "name", "version", "date", "vendor", "device_class", "path");

        foreach (var sr in ReportModelQuery.Sessions(model))
            foreach (var d in sr.Drivers)
                w.WriteRow(
                    I(sr.Session.Id), d.Name, S(d.Version),
                    d.Date?.ToString("o", CultureInfo.InvariantCulture) ?? "",
                    S(d.Vendor), S(d.DeviceClass), S(d.Path));

        return w.ToString();
    }

    internal static string TimelineCsv(ReportModel model)
    {
        var w = new CsvWriter();
        w.WriteRow("session_id", "qpc", "utc", "kind", "label", "duration_ms", "severity");

        foreach (var sr in ReportModelQuery.Sessions(model))
            foreach (var t in sr.Timeline)
                w.WriteRow(
                    I(sr.Session.Id), I(t.Qpc), D(t.Utc),
                    t.Kind, t.Label, N(t.DurationMs), t.Severity);

        return w.ToString();
    }

    internal static string WheaCsv(ReportModel model)
    {
        var w = new CsvWriter();
        w.WriteRow("session_id", "utc", "qpc", "error_source", "severity", "description");

        foreach (var sr in ReportModelQuery.Sessions(model))
            foreach (var wr in sr.Whea)
                w.WriteRow(
                    I(sr.Session.Id), D(wr.Utc), I(wr.Qpc),
                    wr.ErrorSource, wr.Severity, wr.Description);

        return w.ToString();
    }

    // ------------------------------------------------------------------ field formatting

    private static string S(string? v) => v ?? "";
    private static string N(double v) => v.ToString("0.###", CultureInfo.InvariantCulture);
    private static string N(double? v) => v.HasValue ? N(v.Value) : "";
    private static string I(long v) => v.ToString(CultureInfo.InvariantCulture);
    private static string I(int v) => v.ToString(CultureInfo.InvariantCulture);
    private static string D(DateTime v) => v.ToString("o", CultureInfo.InvariantCulture);
    private static string B(bool v) => v ? "true" : "false";
}

/// <summary>Minimal RFC 4180 CSV writer: quotes only when needed, doubles embedded quotes, CRLF rows.</summary>
internal sealed class CsvWriter
{
    private static readonly char[] QuoteTriggers = { ',', '"', '\n', '\r' };
    private readonly StringBuilder _sb = new();

    public void WriteRow(params string?[] fields)
    {
        for (int i = 0; i < fields.Length; i++)
        {
            if (i > 0) _sb.Append(',');
            _sb.Append(Escape(fields[i]));
        }
        _sb.Append("\r\n");
    }

    private static string Escape(string? field)
    {
        field ??= "";
        return field.IndexOfAny(QuoteTriggers) < 0
            ? field
            : "\"" + field.Replace("\"", "\"\"") + "\"";
    }

    public override string ToString() => _sb.ToString();
}
