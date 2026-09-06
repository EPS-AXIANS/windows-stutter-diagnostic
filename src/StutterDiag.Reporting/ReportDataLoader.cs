using System.Globalization;
using StutterDiag.Core.Abstractions;
using StutterDiag.Core.Config;
using StutterDiag.Core.Correlation;
using StutterDiag.Core.Diagnostics;
using StutterDiag.Core.Model;
using StutterDiag.Core.Time;

namespace StutterDiag.Reporting;

/// <summary>
/// Reads a session (or a session pair) back out of an <see cref="IEventStore"/> and assembles
/// the <see cref="ReportModel"/> record tree that every generator renders. All windowing is on
/// the QPC axis. Nothing here draws a causal conclusion: per-stutter correlations are re-derived
/// with <see cref="CorrelationEngine"/> (stored correlations win when present) and the session
/// diagnostic comes straight from <see cref="DiagnosticHypothesisEngine"/>.
/// </summary>
public sealed class ReportDataLoader
{
    // The store's read queries are inclusive range filters; this pair selects "the whole session".
    private const long QpcMin = long.MinValue;
    private const long QpcMax = long.MaxValue;

    // Thresholds used only for descriptive counting in the report (not detection).
    internal const double DpcSpikeMs = 2.0;
    internal const double DiskLatencySpikeMs = 10.0;

    private readonly IEventStore _store;
    private readonly QpcClock _clock;
    private readonly CorrelationOptions _correlation;

    /// <summary>Overrides the report timestamp so snapshot tests get deterministic output.</summary>
    public DateTime? GeneratedUtcOverride { get; set; }

    public ReportDataLoader(IEventStore store, QpcClock clock, AppConfig config)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        var cfg = config ?? AppConfigDefaults.Create();
        _correlation = cfg.Correlation;
    }

    /// <summary>Loads one report model for the request (one session, or two when comparing).</summary>
    public async Task<ReportModel> LoadAsync(ReportRequest request, CancellationToken ct)
    {
        if (request is null) throw new ArgumentNullException(nameof(request));
        if (request.SessionIds is null || request.SessionIds.Count == 0)
            throw new ArgumentException("ReportRequest.SessionIds must contain at least one id.", nameof(request));

        var redaction = request.Redaction ?? new RedactionOptions();
        bool compare = request.CompareMode && request.SessionIds.Count >= 2;
        // request.Redaction has a non-null default; the coalesce above is belt-and-braces.

        var primary = await LoadSessionAsync(request.SessionIds[0], redaction, ct).ConfigureAwait(false);

        SessionReport? secondary = null;
        SessionComparison? comparison = null;
        if (compare)
        {
            secondary = await LoadSessionAsync(request.SessionIds[1], redaction, ct).ConfigureAwait(false);
            comparison = new SessionComparer().Compare(primary, secondary);
        }

        var meta = new ReportMeta(
            GeneratedUtc: GeneratedUtcOverride ?? _clock.UtcNow,
            ToolVersion: typeof(ReportDataLoader).Assembly.GetName().Version?.ToString() ?? "0.0.0",
            Format: request.Format,
            CompareMode: compare,
            QpcFrequency: _clock.Frequency,
            Redaction: redaction,
            RedactionNotes: RedactionNotes.For(redaction));

        return new ReportModel { Meta = meta, Primary = primary, Secondary = secondary, Comparison = comparison };
    }

    /// <summary>Loads and shapes a single session into a <see cref="SessionReport"/>.</summary>
    public async Task<SessionReport> LoadSessionAsync(long sessionId, RedactionOptions? redaction, CancellationToken ct)
    {
        redaction ??= new RedactionOptions();

        var session = await _store.GetSessionAsync(sessionId, ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"Session {sessionId} was not found in the event store.");

        var rawEvents = await _store.GetEventsAsync(sessionId, QpcMin, QpcMax, ct).ConfigureAwait(false);
        var samples = await _store.GetSamplesAsync(sessionId, QpcMin, QpcMax, ct).ConfigureAwait(false);
        var dpcIsr = await _store.GetDpcIsrStatsAsync(sessionId, QpcMin, QpcMax, ct).ConfigureAwait(false);
        var stuttersRaw = await _store.GetStuttersAsync(sessionId, ct).ConfigureAwait(false);
        var snapshotsRaw = await _store.GetProcessSnapshotsAsync(sessionId, QpcMin, QpcMax, ct).ConfigureAwait(false);
        var system = await _store.GetSystemInfoAsync(sessionId, ct).ConfigureAwait(false);
        var driversStored = await _store.GetDriversAsync(sessionId, ct).ConfigureAwait(false);

        var stutters = stuttersRaw.OrderBy(s => s.TimestampQpc).ThenBy(s => s.Id).ToList();

        var correlationsByStutter = new Dictionary<long, IReadOnlyList<StutterCorrelation>>();
        var allCorrelations = new List<StutterCorrelation>();
        foreach (var s in stutters)
        {
            var cs = await _store.GetCorrelationsAsync(s.Id, ct).ConfigureAwait(false);
            correlationsByStutter[s.Id] = cs;
            allCorrelations.AddRange(cs);
        }

        // ---- redaction (applied once, before anything else reads the data) ----
        var events = ApplyEventRedaction(rawEvents, redaction);
        var snapshots = ApplySnapshotRedaction(snapshotsRaw, redaction);

        // ---- per-stutter windows ----
        var dataSource = new InMemoryCorrelationDataSource(events, samples, dpcIsr, snapshots);
        var engine = new CorrelationEngine(_clock, _correlation);
        var baseRates = new StoredBaseRateProvider(allCorrelations);

        var details = new List<StutterDetail>(stutters.Count);
        int index = 0;
        foreach (var s in stutters)
        {
            index++;
            var window = engine.Analyze(s, dataSource, baseline: null, baseRates: baseRates);
            var stored = correlationsByStutter.TryGetValue(s.Id, out var sc) ? sc : Array.Empty<StutterCorrelation>();
            var effective = stored.Count > 0 ? stored : window.Correlations;
            details.Add(BuildDetail(index, s, window, effective));
        }

        int totalStutters = stutters.Count;

        var findings = new DiagnosticHypothesisEngine().Analyze(
            stutters, allCorrelations, baseRates,
            alwaysReport: new[] { SignalCatalog.TpmTbs, SignalCatalog.Whea });

        var ranking = BuildRanking(allCorrelations, totalStutters);
        var counts = BuildCounts(session, stutters, events, samples, dpcIsr, ranking);
        var summary = BuildSummary(counts);

        var timeline = new TimelineBuilder(_clock, DpcSpikeMs, DiskLatencySpikeMs)
            .Build(stutters, events, samples, dpcIsr);

        var whea = events
            .Where(e => e.Category == EventCategory.Whea)
            .OrderBy(e => e.TimestampQpc)
            .Select(e => new WheaRow(
                e.TimestampUtc, e.TimestampQpc,
                DataOr(e, "errorSource", "Unknown"),
                DataOr(e, "severity", SeverityText(e.Severity)),
                e.Message,
                redaction.IncludeRawEventXml ? e.RawXml : null))
            .ToList();

        var health = BuildHealth(events);

        var drivers = driversStored.Count > 0
            ? driversStored
            : (system?.Drivers ?? (IReadOnlyList<DriverInfo>)Array.Empty<DriverInfo>());
        var systemView = system is null ? null : BuildSystemView(system, drivers.Count);

        return new SessionReport
        {
            Session = session,
            Summary = summary,
            Counts = counts,
            CorrelationRanking = ranking,
            Stutters = details,
            Findings = findings,
            Timeline = timeline,
            Whea = whea,
            Events = events,
            Drivers = drivers,
            Health = health,
            System = system,
            SystemView = systemView,
        };
    }

    // ---------------------------------------------------------------- redaction

    private static IReadOnlyList<MonitorEvent> ApplyEventRedaction(IReadOnlyList<MonitorEvent> events, RedactionOptions r)
    {
        IEnumerable<MonitorEvent> q = events;
        if (!r.IncludeFullEventLog)
            q = q.Where(e => e.Category != EventCategory.EventLog);

        bool scrub = !r.IncludeRawEventXml || !r.IncludeUserNames || !r.IncludeCommandLines;
        if (!scrub)
            return ReferenceEquals(q, events) ? events : q.ToList();

        var outp = new List<MonitorEvent>();
        foreach (var e in q)
            outp.Add(ScrubEvent(e, r));
        return outp;
    }

    private static MonitorEvent ScrubEvent(MonitorEvent e, RedactionOptions r)
    {
        string? xml = r.IncludeRawEventXml ? e.RawXml : null;
        IReadOnlyDictionary<string, string>? data = e.Data;

        if (e.Data is not null && (!r.IncludeUserNames || !r.IncludeCommandLines))
        {
            var d = new Dictionary<string, string>(e.Data.Count, StringComparer.Ordinal);
            foreach (var kv in e.Data)
            {
                string k = kv.Key.ToLowerInvariant();
                if (!r.IncludeUserNames && (k.Contains("user") || k.Contains("account") || k.Contains("owner") || k == "sid"))
                    d[kv.Key] = "(user redacted)";
                else if (!r.IncludeCommandLines && (k.Contains("commandline") || k.Contains("cmdline") || k.Contains("command_line")))
                    d[kv.Key] = "(command line redacted)";
                else
                    d[kv.Key] = kv.Value;
            }
            data = d;
        }

        if (ReferenceEquals(xml, e.RawXml) && ReferenceEquals(data, e.Data))
            return e;
        return e with { RawXml = xml, Data = data };
    }

    private static IReadOnlyList<ProcessSnapshot> ApplySnapshotRedaction(IReadOnlyList<ProcessSnapshot> snaps, RedactionOptions r)
    {
        if (r.IncludeProcessNames) return snaps;
        var outp = new List<ProcessSnapshot>(snaps.Count);
        foreach (var s in snaps)
        {
            var rows = s.Rows.Select(row => row with { Name = "(process name redacted)" }).ToList();
            outp.Add(s with { Rows = rows });
        }
        return outp;
    }

    // ---------------------------------------------------------------- per stutter

    private static StutterDetail BuildDetail(
        int index, Stutter s, CorrelationWindow window, IReadOnlyList<StutterCorrelation> correlations)
    {
        var winEvents = window.Events;
        var winSamples = window.Samples;

        int tpm = winEvents.Count(e => e.Category is EventCategory.Tpm or EventCategory.Tbs);
        bool whea = winEvents.Any(e => e.Category == EventCategory.Whea);
        bool kp = winEvents.Any(e => e.Category == EventCategory.KernelPower);

        double? diskSpike = null;
        foreach (var m in winSamples)
            if (m.Metric is "disk.read.latency.ms" or "disk.write.latency.ms")
                diskSpike = diskSpike is null ? m.Value : Math.Max(diskSpike.Value, m.Value);

        double? dpcPeak = null;
        string? dpcDriver = null;
        foreach (var d in window.DpcIsr)
        {
            if (d.Kind != "DPC") continue;
            if (dpcPeak is null || d.MaxMs > dpcPeak.Value) { dpcPeak = d.MaxMs; dpcDriver = d.Driver; }
        }

        var top = (window.Snapshot?.Rows ?? (IReadOnlyList<ProcessSnapshotRow>)Array.Empty<ProcessSnapshotRow>())
            .OrderByDescending(x => x.CpuPercent)
            .ThenBy(x => x.Pid)
            .Take(5)
            .Select(x => new TopProcess(x.Name, x.Pid, x.CpuPercent, x.WorkingSetBytes))
            .ToList();

        var possible = correlations
            .Where(c => c.Score != CorrelationScore.NoEvidence)
            .OrderByDescending(c => c.Score)
            .ThenBy(c => Math.Abs(c.ProximityMs))
            .ThenBy(c => c.SignalType, StringComparer.Ordinal)
            .Select(c => new PossibleCorrelation(
                Label: ScoreLabel(c.Score),
                ScoreClass: ScoreClass(c.Score),
                SignalType: c.SignalType,
                DisplayName: SignalCatalog.DisplayName(c.SignalType),
                Score: c.Score,
                ProximityMs: c.ProximityMs,
                BaseRate: c.BaseRate,
                Detail: c.Detail))
            .ToList();

        return new StutterDetail
        {
            Id = s.Id,
            Index = index,
            TimestampQpc = s.TimestampQpc,
            TimestampUtc = s.TimestampUtc,
            DurationMs = s.DurationMs,
            Severity = s.Severity,
            Detector = s.Detector,
            Confidence = s.Confidence,
            CorroboratedBy = s.CorroboratedBy,
            UserMarked = s.UserMarked,
            TpmEventCount = tpm,
            CpuAvgPct = Avg(winSamples, "cpu.total.pct"),
            CpuPeakPct = Max(winSamples, "cpu.total.pct"),
            GpuAvgPct = Avg(winSamples, "gpu.engine.pct"),
            DiskLatencySpikeMs = diskSpike,
            DpcPeakMs = dpcPeak,
            DpcPeakDriver = dpcDriver,
            Whea = whea,
            KernelPower = kp,
            TopProcesses = top,
            PossibleCorrelations = possible,
            WindowFromQpc = window.FromQpc,
            WindowToQpc = window.ToQpc,
        };
    }

    // ---------------------------------------------------------------- aggregates

    private static IReadOnlyList<CorrelationRankingRow> BuildRanking(IReadOnlyList<StutterCorrelation> all, int totalStutters)
    {
        return all
            .GroupBy(c => c.SignalType, StringComparer.Ordinal)
            .Select(g =>
            {
                int correlated = g.Select(c => c.StutterId).Distinct().Count();
                var score = g.Max(c => c.Score);
                double basePct = g.Max(c => c.BaseRate) * 100.0;
                double fraction = totalStutters == 0 ? 0 : (double)correlated / totalStutters;
                double lift = basePct <= 0 ? double.PositiveInfinity : fraction * 100.0 / basePct;
                return (Signal: g.Key, Correlated: correlated, Score: score, BasePct: basePct, Lift: lift);
            })
            .OrderByDescending(x => x.Score)
            .ThenByDescending(x => x.Correlated)
            .ThenBy(x => x.Signal, StringComparer.Ordinal)
            .Select((x, i) => new CorrelationRankingRow(
                Rank: i + 1,
                SignalType: x.Signal,
                DisplayName: SignalCatalog.DisplayName(x.Signal),
                CorrelatedStutters: x.Correlated,
                TotalStutters: totalStutters,
                Score: x.Score,
                ScoreLabel: ScoreLabel(x.Score),
                ScoreClass: ScoreClass(x.Score),
                BaseRatePercent: x.BasePct,
                Lift: x.Lift))
            .ToList();
    }

    private static AggregateCounts BuildCounts(
        MonitoringSession session,
        IReadOnlyList<Stutter> stutters,
        IReadOnlyList<MonitorEvent> events,
        IReadOnlyList<MetricSample> samples,
        IReadOnlyList<DpcIsrStat> dpcIsr,
        IReadOnlyList<CorrelationRankingRow> ranking)
    {
        TimeSpan duration = SessionDuration(session, events, stutters);
        double hours = duration.TotalHours;
        int total = stutters.Count;

        var durations = stutters.Select(s => s.DurationMs).ToList();
        var diskLat = samples
            .Where(s => s.Metric is "disk.read.latency.ms" or "disk.write.latency.ms")
            .Select(s => s.Value)
            .ToList();

        return new AggregateCounts(
            MonitoringDuration: duration,
            StuttersTotal: total,
            StuttersPerHour: hours > 0 ? total / hours : 0,
            MicroStutters: stutters.Count(s => s.Severity == StutterSeverity.Micro),
            MajorStutters: stutters.Count(s => s.Severity == StutterSeverity.Major),
            SevereStutters: stutters.Count(s => s.Severity == StutterSeverity.Severe),
            CriticalStutters: stutters.Count(s => s.Severity == StutterSeverity.Critical),
            MeanDurationMs: durations.Count > 0 ? durations.Average() : (double?)null,
            P95DurationMs: ReportMath.Percentile(durations, 0.95),
            TpmTbsEvents: events.Count(e => e.Category is EventCategory.Tpm or EventCategory.Tbs),
            WheaEvents: events.Count(e => e.Category == EventCategory.Whea),
            DpcSpikeCount: dpcIsr.Count(d => d.Kind == "DPC" && d.MaxMs >= DpcSpikeMs),
            DistinctDriverAnomalies: dpcIsr
                .Where(d => d.MaxMs >= DpcSpikeMs)
                .Select(d => d.Driver)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Count(),
            MeanCpuPct: Avg(samples, "cpu.total.pct"),
            MeanGpuPct: Avg(samples, "gpu.engine.pct"),
            StorageLatencyP95Ms: ReportMath.Percentile(diskLat, 0.95),
            PowerStateChangeCount: events.Count(e => e.Category is EventCategory.PowerState or EventCategory.PState),
            TopCorrelationSignals: ranking.Take(3).ToList());
    }

    private static SummaryBlock BuildSummary(AggregateCounts c) => new(
        MonitoringDuration: FormatDuration(c.MonitoringDuration),
        StuttersDetected: c.StuttersTotal,
        MajorStutters: c.MajorStutters + c.SevereStutters + c.CriticalStutters,
        TpmRelatedEvents: c.TpmTbsEvents,
        WheaEvents: c.WheaEvents,
        DpcSpikes: c.DpcSpikeCount,
        DriverAnomalies: c.DistinctDriverAnomalies);

    private static TimeSpan SessionDuration(MonitoringSession s, IReadOnlyList<MonitorEvent> events, IReadOnlyList<Stutter> stutters)
    {
        if (s.EndedUtc is { } ended && ended > s.StartedUtc)
            return ended - s.StartedUtc;

        DateTime max = s.StartedUtc;
        foreach (var e in events) if (e.TimestampUtc > max) max = e.TimestampUtc;
        foreach (var st in stutters) if (st.TimestampUtc > max) max = st.TimestampUtc;
        return max > s.StartedUtc ? max - s.StartedUtc : TimeSpan.Zero;
    }

    private static HealthSummary BuildHealth(IReadOnlyList<MonitorEvent> events)
    {
        var monitors = events
            .Where(e => e.Category == EventCategory.Health)
            .GroupBy(e => e.Source, StringComparer.Ordinal)
            .Select(g =>
            {
                var last = g.OrderByDescending(e => e.TimestampQpc).First();
                string status =
                    last.Data is not null && last.Data.TryGetValue("status", out var st) && !string.IsNullOrWhiteSpace(st) ? st
                    : last.Severity >= EventSeverity.Error ? "Failed"
                    : last.Severity == EventSeverity.Warning ? "Degraded"
                    : "Ok";
                return new MonitorHealthRow(g.Key, status, string.IsNullOrWhiteSpace(last.Message) ? null : last.Message);
            })
            .OrderBy(m => m.Monitor, StringComparer.Ordinal)
            .ToList();

        long lost = 0;
        bool sawCounter = false;
        foreach (var e in events)
        {
            if (e.Data is null) continue;
            foreach (var key in LostKeys)
                if (e.Data.TryGetValue(key, out var v)
                    && long.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n))
                {
                    lost += Math.Max(0, n);
                    sawCounter = true;
                }
        }

        string note = sawCounter
            ? "Reconstructed from Kernel-EventTracing counters found in the event stream."
            : "No lost-event counters were recorded; 0 is a lower bound, not a guarantee.";
        return new HealthSummary(monitors, (int)Math.Min(lost, int.MaxValue), note);
    }

    private static readonly string[] LostKeys =
        { "lostEvents", "eventsLost", "lost", "RTLostEvents", "BuffersLost" };

    private static SystemView BuildSystemView(SystemInfo s, int driverCount)
    {
        var facts = new List<NamedValue>();
        void AddAll(string prefix, IReadOnlyDictionary<string, string> d)
        {
            foreach (var kv in d.OrderBy(k => k.Key, StringComparer.Ordinal))
                facts.Add(new NamedValue(prefix.Length == 0 ? kv.Key : $"{prefix}: {kv.Key}", kv.Value));
        }

        AddAll("Windows", s.Windows);
        AddAll("CPU", s.Cpu);
        AddAll("Memory", s.Memory);
        AddAll("Motherboard", s.Motherboard);
        AddAll("BIOS", s.Bios);
        AddAll("Security", s.Security);
        for (int i = 0; i < s.Gpus.Count; i++)
            AddAll($"GPU{i}", s.Gpus[i]);

        var tpm = new List<NamedValue>
        {
            new("Present", s.Tpm.Present.ToString()),
            new("Inferred type", s.Tpm.InferredType.ToString()),
            new("Inference basis", s.Tpm.InferenceBasis),
            new("Spec version", s.Tpm.SpecVersion ?? SystemInfo.Unavailable),
            new("Manufacturer", s.Tpm.ManufacturerName ?? s.Tpm.ManufacturerId ?? SystemInfo.Unavailable),
            new("Interface", s.Tpm.InterfaceType ?? SystemInfo.Unavailable),
            new("Enabled", s.Tpm.IsEnabled?.ToString() ?? SystemInfo.Unavailable),
            new("Activated", s.Tpm.IsActivated?.ToString() ?? SystemInfo.Unavailable),
            new("Owned", s.Tpm.IsOwned?.ToString() ?? SystemInfo.Unavailable),
            new("Source", s.Tpm.Source),
        };

        return new SystemView(facts, tpm, driverCount);
    }

    // ---------------------------------------------------------------- small helpers

    internal static string FormatDuration(TimeSpan t)
    {
        if (t <= TimeSpan.Zero) return "0m";
        if (t.TotalDays >= 1) return $"{(int)t.TotalDays}d {t.Hours}h {t.Minutes}m";
        if (t.TotalHours >= 1) return $"{(int)t.TotalHours}h {t.Minutes}m";
        if (t.TotalMinutes >= 1) return $"{(int)t.TotalMinutes}m {t.Seconds}s";
        return $"{t.Seconds}s";
    }

    internal static string ScoreLabel(CorrelationScore s) => s switch
    {
        CorrelationScore.High => "[HIGH]",
        CorrelationScore.Medium => "[MEDIUM]",
        CorrelationScore.Low => "[LOW]",
        _ => "[NONE]",
    };

    internal static string ScoreClass(CorrelationScore s) => s switch
    {
        CorrelationScore.High => "high",
        CorrelationScore.Medium => "medium",
        _ => "low",
    };

    private static string DataOr(MonitorEvent e, string key, string fallback)
        => e.Data is not null && e.Data.TryGetValue(key, out var v) && !string.IsNullOrWhiteSpace(v) ? v : fallback;

    private static string SeverityText(EventSeverity s) => s switch
    {
        EventSeverity.Critical => "Fatal",
        EventSeverity.Error => "Recoverable",
        EventSeverity.Warning => "Corrected",
        _ => "Informational",
    };

    private static double? Avg(IReadOnlyList<MetricSample> samples, string metric)
    {
        double sum = 0;
        int n = 0;
        foreach (var s in samples)
            if (string.Equals(s.Metric, metric, StringComparison.Ordinal)) { sum += s.Value; n++; }
        return n == 0 ? (double?)null : sum / n;
    }

    private static double? Max(IReadOnlyList<MetricSample> samples, string metric)
    {
        double? m = null;
        foreach (var s in samples)
            if (string.Equals(s.Metric, metric, StringComparison.Ordinal))
                m = m is null ? s.Value : Math.Max(m.Value, s.Value);
        return m;
    }
}

// ------------------------------------------------------------------------------------------------
// ReportModel record tree — the single shape every generator (HTML / JSON / CSV / ZIP) renders.
// ------------------------------------------------------------------------------------------------

/// <summary>Root of the rendered report. One primary session, plus an optional comparison session.</summary>
public sealed record ReportModel
{
    public required ReportMeta Meta { get; init; }
    public required SessionReport Primary { get; init; }
    public SessionReport? Secondary { get; init; }
    public SessionComparison? Comparison { get; init; }

    /// <summary>The sentence every generated artefact must carry verbatim.</summary>
    public const string CausationDisclaimer = "Correlation does not prove causation.";
}

/// <summary>Report-level metadata: when it was made, from what, and how the data was redacted.</summary>
public sealed record ReportMeta(
    DateTime GeneratedUtc,
    string ToolVersion,
    ReportFormat Format,
    bool CompareMode,
    long QpcFrequency,
    RedactionOptions Redaction,
    IReadOnlyList<string> RedactionNotes);

/// <summary>Everything the report shows for one monitoring session.</summary>
public sealed record SessionReport
{
    public required MonitoringSession Session { get; init; }
    public required SummaryBlock Summary { get; init; }
    public required AggregateCounts Counts { get; init; }
    public required IReadOnlyList<CorrelationRankingRow> CorrelationRanking { get; init; }
    public required IReadOnlyList<StutterDetail> Stutters { get; init; }
    public required IReadOnlyList<DiagnosticFinding> Findings { get; init; }
    public required IReadOnlyList<TimelineEntry> Timeline { get; init; }
    public required IReadOnlyList<WheaRow> Whea { get; init; }
    public required IReadOnlyList<MonitorEvent> Events { get; init; }
    public required IReadOnlyList<DriverInfo> Drivers { get; init; }
    public required HealthSummary Health { get; init; }
    public required SystemInfo? System { get; init; }
    public required SystemView? SystemView { get; init; }
}

/// <summary>The headline counters shown in the report's summary block.</summary>
public sealed record SummaryBlock(
    string MonitoringDuration,
    int StuttersDetected,
    int MajorStutters,
    int TpmRelatedEvents,
    int WheaEvents,
    int DpcSpikes,
    int DriverAnomalies);

/// <summary>Numeric session aggregates, also the raw material for the A/B comparison.</summary>
public sealed record AggregateCounts(
    TimeSpan MonitoringDuration,
    int StuttersTotal,
    double StuttersPerHour,
    int MicroStutters,
    int MajorStutters,
    int SevereStutters,
    int CriticalStutters,
    double? MeanDurationMs,
    double? P95DurationMs,
    int TpmTbsEvents,
    int WheaEvents,
    int DpcSpikeCount,
    int DistinctDriverAnomalies,
    double? MeanCpuPct,
    double? MeanGpuPct,
    double? StorageLatencyP95Ms,
    int PowerStateChangeCount,
    IReadOnlyList<CorrelationRankingRow> TopCorrelationSignals);

/// <summary>One line of the session correlation ranking (co-occurrence only, never causation).</summary>
public sealed record CorrelationRankingRow(
    int Rank,
    string SignalType,
    string DisplayName,
    int CorrelatedStutters,
    int TotalStutters,
    CorrelationScore Score,
    string ScoreLabel,
    string ScoreClass,
    double BaseRatePercent,
    double Lift)
{
    /// <summary>Canonical "N. &lt;signal&gt; — Correlated with X/Y stutters" line.</summary>
    public string Line => $"{Rank}. {DisplayName} — Correlated with {CorrelatedStutters}/{TotalStutters} stutters";
}

/// <summary>Everything the per-stutter detail card shows.</summary>
public sealed record StutterDetail
{
    public required long Id { get; init; }
    public required int Index { get; init; }
    public required long TimestampQpc { get; init; }
    public required DateTime TimestampUtc { get; init; }
    public required double DurationMs { get; init; }
    public required StutterSeverity Severity { get; init; }
    public required DetectorKind Detector { get; init; }
    public required DetectionConfidence Confidence { get; init; }
    public required IReadOnlyList<DetectorKind> CorroboratedBy { get; init; }
    public required bool UserMarked { get; init; }
    public required int TpmEventCount { get; init; }
    public required double? CpuAvgPct { get; init; }
    public required double? CpuPeakPct { get; init; }
    public required double? GpuAvgPct { get; init; }
    public required double? DiskLatencySpikeMs { get; init; }
    public required double? DpcPeakMs { get; init; }
    public required string? DpcPeakDriver { get; init; }
    public required bool Whea { get; init; }
    public required bool KernelPower { get; init; }
    public required IReadOnlyList<TopProcess> TopProcesses { get; init; }
    public required IReadOnlyList<PossibleCorrelation> PossibleCorrelations { get; init; }
    public required long WindowFromQpc { get; init; }
    public required long WindowToQpc { get; init; }
}

/// <summary>A process row shown under a stutter (name may be a redaction placeholder).</summary>
public sealed record TopProcess(string Name, int Pid, double CpuPercent, long WorkingSetBytes);

/// <summary>One entry in a stutter's "Possible correlations" list, tagged [HIGH]/[MEDIUM]/[LOW].</summary>
public sealed record PossibleCorrelation(
    string Label,
    string ScoreClass,
    string SignalType,
    string DisplayName,
    CorrelationScore Score,
    double ProximityMs,
    double BaseRate,
    string Detail);

/// <summary>A WHEA record flattened for the report table / CSV.</summary>
public sealed record WheaRow(
    DateTime Utc,
    long Qpc,
    string ErrorSource,
    string Severity,
    string Description,
    string? RawXml);

/// <summary>Last known status of one monitor (reconstructed from <see cref="EventCategory.Health"/> events).</summary>
public sealed record MonitorHealthRow(string Monitor, string Status, string? Note);

/// <summary>Monitor health plus a best-effort lost-event estimate.</summary>
public sealed record HealthSummary(
    IReadOnlyList<MonitorHealthRow> Monitors,
    int LostEventEstimate,
    string? Note);

/// <summary>A flattened name/value fact for the System section.</summary>
public sealed record NamedValue(string Name, string Value);

/// <summary>System information flattened into ordered rows the template can render without dictionaries.</summary>
public sealed record SystemView(
    IReadOnlyList<NamedValue> Facts,
    IReadOnlyList<NamedValue> Tpm,
    int DriverCount);

// ------------------------------------------------------------------------------------------------
// Internal helpers shared by the loader and the generators.
// ------------------------------------------------------------------------------------------------

/// <summary>Enumerates the sessions in a model (primary, then the comparison session if present).</summary>
internal static class ReportModelQuery
{
    public static IEnumerable<SessionReport> Sessions(ReportModel model)
    {
        yield return model.Primary;
        if (model.Secondary is not null)
            yield return model.Secondary;
    }
}

/// <summary>Human-readable notes describing what a <see cref="RedactionOptions"/> stripped.</summary>
internal static class RedactionNotes
{
    public static IReadOnlyList<string> For(RedactionOptions r)
    {
        var n = new List<string>();
        if (!r.IncludeProcessNames) n.Add("process names replaced with a placeholder");
        if (!r.IncludeUserNames) n.Add("user / account names removed from event data");
        if (!r.IncludeCommandLines) n.Add("process command lines removed from event data");
        if (!r.IncludeRawEventXml) n.Add("raw event XML stripped");
        if (!r.IncludeFullEventLog) n.Add("Windows Event Log entries omitted");
        return n;
    }
}

/// <summary>
/// In-memory <see cref="ICorrelationDataSource"/> over one session's data so the synchronous
/// <see cref="CorrelationEngine"/> can rebuild each stutter window at report time.
/// </summary>
internal sealed class InMemoryCorrelationDataSource : ICorrelationDataSource
{
    private readonly List<MonitorEvent> _events;
    private readonly List<MetricSample> _samples;
    private readonly List<DpcIsrStat> _dpc;
    private readonly List<ProcessSnapshot> _snaps;

    public InMemoryCorrelationDataSource(
        IReadOnlyList<MonitorEvent> events,
        IReadOnlyList<MetricSample> samples,
        IReadOnlyList<DpcIsrStat> dpc,
        IReadOnlyList<ProcessSnapshot> snapshots)
    {
        _events = events.OrderBy(e => e.TimestampQpc).ToList();
        _samples = samples.OrderBy(s => s.TimestampQpc).ToList();
        _dpc = dpc.OrderBy(d => d.WindowStartQpc).ToList();
        _snaps = snapshots.OrderBy(s => s.TimestampQpc).ToList();
    }

    public IReadOnlyList<MonitorEvent> GetEvents(long fromQpc, long toQpc)
        => _events.Where(e => e.TimestampQpc >= fromQpc && e.TimestampQpc <= toQpc).ToList();

    public IReadOnlyList<MetricSample> GetSamples(long fromQpc, long toQpc)
        => _samples.Where(s => s.TimestampQpc >= fromQpc && s.TimestampQpc <= toQpc).ToList();

    public IReadOnlyList<DpcIsrStat> GetDpcIsrStats(long fromQpc, long toQpc)
        => _dpc.Where(d => d.WindowStartQpc <= toQpc && d.WindowEndQpc >= fromQpc).ToList();

    public ProcessSnapshot? GetNearestSnapshot(long qpc)
    {
        ProcessSnapshot? best = null;
        long bestDelta = long.MaxValue;
        foreach (var s in _snaps)
        {
            long d = Math.Abs(s.TimestampQpc - qpc);
            if (d < bestDelta) { bestDelta = d; best = s; }
        }
        return best;
    }
}

/// <summary>
/// <see cref="IBaseRateProvider"/> that replays the base rates stored on each
/// <see cref="StutterCorrelation"/> (computed live by <c>BaseRateSampler</c> during the run).
/// </summary>
internal sealed class StoredBaseRateProvider : IBaseRateProvider
{
    private readonly Dictionary<string, double> _rates;

    public StoredBaseRateProvider(IEnumerable<StutterCorrelation> correlations)
        => _rates = correlations
            .GroupBy(c => c.SignalType, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Max(c => c.BaseRate), StringComparer.Ordinal);

    public double GetBaseRate(string signalType) => _rates.TryGetValue(signalType, out var r) ? r : 0.0;
}
