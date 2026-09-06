using StutterDiag.Core.Abstractions;
using StutterDiag.Core.Config;
using StutterDiag.Core.Model;
using StutterDiag.Core.Time;

namespace StutterDiag.Reporting;

/// <summary>
/// Resolves the right <see cref="IReportGenerator"/> for a <see cref="ReportFormat"/> and offers
/// a one-call helper the Service / CLI use to generate a report end to end.
/// </summary>
public sealed class ReportGeneratorFactory
{
    private readonly ReportDataLoader _loader;
    private readonly string? _logDirectory;

    public ReportGeneratorFactory(IEventStore store, QpcClock clock, AppConfig? config = null)
    {
        if (store is null) throw new ArgumentNullException(nameof(store));
        if (clock is null) throw new ArgumentNullException(nameof(clock));

        var cfg = config ?? AppConfigDefaults.Create();
        _loader = new ReportDataLoader(store, clock, cfg);
        _logDirectory = cfg.Storage.LogDirectory;
    }

    /// <summary>Returns the generator for <paramref name="format"/>.</summary>
    public IReportGenerator Resolve(ReportFormat format) => format switch
    {
        ReportFormat.Html => new HtmlReportGenerator(_loader),
        ReportFormat.Json => new JsonReportGenerator(_loader),
        ReportFormat.Csv => new CsvReportGenerator(_loader),
        ReportFormat.Zip => new ZipReportPackager(_loader, _logDirectory),
        _ => throw new ArgumentOutOfRangeException(nameof(format), format, "Unsupported report format."),
    };

    /// <summary>Generates a report for <paramref name="req"/> and returns the path written.</summary>
    public static Task<string> GenerateAsync(IEventStore store, ReportRequest req, QpcClock clock, CancellationToken ct)
    {
        if (req is null) throw new ArgumentNullException(nameof(req));
        return new ReportGeneratorFactory(store, clock).Resolve(req.Format).GenerateAsync(req, ct);
    }
}
