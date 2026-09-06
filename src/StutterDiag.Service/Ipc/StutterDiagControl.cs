using System.Globalization;
using StutterDiag.Core.Correlation;
using StutterDiag.Core.Diagnostics;
using StutterDiag.Core.Model;
using StutterDiag.Core.Time;
using StutterDiag.Ipc;
using StutterDiag.Reporting;

namespace StutterDiag.Service.Ipc;

/// <summary>
/// The service-side <see cref="IStutterDiagControl"/>. Every method is a thin adapter: it
/// forwards to <see cref="MonitorOrchestrator"/> for live state / control, to the orchestrator's
/// <see cref="MonitorOrchestrator.Store"/> for history, and to
/// <see cref="ReportGeneratorFactory"/> for exports. DTO ↔ model mapping lives here so the
/// orchestrator never depends on the IPC contract.
/// </summary>
public sealed class StutterDiagControl : IStutterDiagControl
{
    private readonly MonitorOrchestrator _orchestrator;
    private readonly QpcClock _clock;

    public StutterDiagControl(MonitorOrchestrator orchestrator, QpcClock clock)
    {
        _orchestrator = orchestrator;
        _clock = clock;
    }

    public Task<StatusDto> GetStatusAsync(CancellationToken ct = default)
        => Task.FromResult(ToDto(_orchestrator.GetStatus()));

    public async Task<StatusDto> StartMonitoringAsync(string mode, CancellationToken ct = default)
    {
        await _orchestrator.StartAsync(mode, ct).ConfigureAwait(false);
        return ToDto(_orchestrator.GetStatus());
    }

    public async Task<StatusDto> StopMonitoringAsync(CancellationToken ct = default)
    {
        await _orchestrator.StopAsync(ct).ConfigureAwait(false);
        return ToDto(_orchestrator.GetStatus());
    }

    public Task<IReadOnlyList<RecentEventDto>> GetRecentEventsAsync(int count, CancellationToken ct = default)
    {
        IReadOnlyList<RecentEventDto> list = _orchestrator.GetRecentEvents(count)
            .Select(e => new RecentEventDto(
                Iso(e.TimestampUtc), e.Category.ToString(), e.Severity.ToString(), e.Source, e.Message))
            .ToList();
        return Task.FromResult(list);
    }

    public Task<IReadOnlyList<RecentStutterDto>> GetRecentStuttersAsync(int count, CancellationToken ct = default)
    {
        IReadOnlyList<RecentStutterDto> list = _orchestrator.GetRecentStutters(count)
            .Select(s => new RecentStutterDto(
                s.Id, Iso(s.TimestampUtc), s.DurationMs, s.Severity.ToString(), s.Detector.ToString(),
                s.UserMarked, s.CorrelationCount, s.TopCorrelation))
            .ToList();
        return Task.FromResult(list);
    }

    public Task<MarkResultDto> MarkStutterAsync(string? note, CancellationToken ct = default)
    {
        var outcome = _orchestrator.MarkUserStutter(note);
        return Task.FromResult(new MarkResultDto(outcome.Accepted, Iso(outcome.TimestampUtc), outcome.Message));
    }

    public Task<string> GetConfigJsonAsync(CancellationToken ct = default)
        => Task.FromResult(_orchestrator.GetConfigJson());

    public Task<IReadOnlyList<string>> SetConfigJsonAsync(string configJson, CancellationToken ct = default)
        => Task.FromResult(_orchestrator.ApplyConfigJson(configJson));

    public Task<string> GetSystemInfoJsonAsync(CancellationToken ct = default)
    {
        var info = _orchestrator.CollectSystemInfo();
        return Task.FromResult(System.Text.Json.JsonSerializer.Serialize(info, IpcJson.Options));
    }

    public async Task<IReadOnlyList<SessionDto>> ListSessionsAsync(CancellationToken ct = default)
    {
        var sessions = await _orchestrator.Store.GetSessionsAsync(ct).ConfigureAwait(false);
        var result = new List<SessionDto>(sessions.Count);
        foreach (var s in sessions)
        {
            SessionCounts c;
            try { c = await _orchestrator.Store.GetSessionCountsAsync(s.Id, ct).ConfigureAwait(false); }
            catch { c = SessionCounts.Empty(s.Id); }

            result.Add(new SessionDto(
                s.Id, Iso(s.StartedUtc), s.EndedUtc is { } end ? Iso(end) : null,
                s.Label, s.Mode, s.TpmTypeInferred.ToString(),
                c.Stutters, c.MajorStutters, c.TpmEvents, c.WheaEvents, c.DpcSpikes));
        }
        return result;
    }

    public Task<string> GenerateReportAsync(ReportRequestDto request, CancellationToken ct = default)
    {
        var format = Enum.TryParse<ReportFormat>(request.Format, ignoreCase: true, out var f) ? f : ReportFormat.Html;

        var req = new ReportRequest
        {
            OutputPath = request.OutputPath,
            SessionIds = request.SessionIds ?? Array.Empty<long>(),
            Format = format,
            CompareMode = request.CompareMode,
            Redaction = new RedactionOptions
            {
                IncludeProcessNames = request.IncludeProcessNames,
                IncludeUserNames = request.IncludeUserNames,
                IncludeCommandLines = request.IncludeCommandLines,
                IncludeRawEventXml = request.IncludeRawEventXml,
                IncludeFullEventLog = request.IncludeFullEventLog,
            },
        };

        // ReportGeneratorFactory.GenerateAsync(IEventStore, ReportRequest, QpcClock, CancellationToken) -> output path.
        return ReportGeneratorFactory.GenerateAsync(_orchestrator.Store, req, _clock, ct);
    }

    public async Task<IReadOnlyList<TimelineEntryDto>> GetTimelineAsync(
        long sessionId, string fromUtcIso, string toUtcIso, CancellationToken ct = default)
    {
        DateTime fromUtc = ParseUtc(fromUtcIso, DateTime.UtcNow.AddHours(-1));
        DateTime toUtc = ParseUtc(toUtcIso, DateTime.UtcNow);
        long fromQpc = _clock.UtcToQpc(fromUtc);
        long toQpc = _clock.UtcToQpc(toUtc);

        var events = await _orchestrator.Store.GetEventsAsync(sessionId, fromQpc, toQpc, ct).ConfigureAwait(false);
        var stutters = await _orchestrator.Store.GetStuttersAsync(sessionId, ct).ConfigureAwait(false);

        var rows = new List<(long Qpc, TimelineEntryDto Dto)>(events.Count + stutters.Count);

        foreach (var e in events)
            rows.Add((e.TimestampQpc, new TimelineEntryDto(Iso(e.TimestampUtc), e.Category.ToString(), e.Message, null)));

        foreach (var s in stutters)
        {
            if (s.TimestampQpc < fromQpc || s.TimestampQpc > toQpc) continue;
            rows.Add((s.TimestampQpc, new TimelineEntryDto(
                Iso(s.TimestampUtc),
                s.UserMarked ? "UserMark" : "Stutter",
                $"{s.Severity} · {s.Detector} · {s.DurationMs:F0} ms",
                s.DurationMs,
                s.Id)));
        }

        return rows.OrderBy(r => r.Qpc).Select(r => r.Dto).ToList();
    }

    public async Task<StutterWindowDto?> GetStutterWindowAsync(long sessionId, long stutterId, CancellationToken ct = default)
    {
        var store = _orchestrator.Store;
        var stutter = (await store.GetStuttersAsync(sessionId, ct).ConfigureAwait(false))
            .FirstOrDefault(s => s.Id == stutterId);
        if (stutter is null) return null;

        var corr = _orchestrator.Config.Correlation;
        long preTicks = _clock.MsToTicks(corr.PreRollSeconds * 1000.0);
        long postTicks = _clock.MsToTicks(corr.PostRollSeconds * 1000.0);
        long from = stutter.TimestampQpc - preTicks;
        long to = stutter.TimestampQpc + postTicks;

        var events = await store.GetEventsAsync(sessionId, from, to, ct).ConfigureAwait(false);
        var samples = await store.GetSamplesAsync(sessionId, from, to, ct).ConfigureAwait(false);
        var dpc = await store.GetDpcIsrStatsAsync(sessionId, from, to, ct).ConfigureAwait(false);
        var snapshots = await store.GetProcessSnapshotsAsync(sessionId, from, to, ct).ConfigureAwait(false);
        var correlations = await store.GetCorrelationsAsync(stutterId, ct).ConfigureAwait(false);

        double? Avg(string metric)
        {
            var v = samples.Where(s => s.Metric == metric).Select(s => s.Value).ToList();
            return v.Count == 0 ? null : v.Average();
        }
        double? Peak(string metric)
        {
            var v = samples.Where(s => s.Metric == metric).Select(s => s.Value).ToList();
            return v.Count == 0 ? null : v.Max();
        }
        double? MaxOf(params string[] metrics)
        {
            var v = samples.Where(s => metrics.Contains(s.Metric)).Select(s => s.Value).ToList();
            return v.Count == 0 ? null : v.Max();
        }

        var topDpc = dpc.OrderByDescending(d => d.MaxMs).FirstOrDefault();
        var snap = snapshots
            .OrderBy(s => Math.Abs(s.TimestampQpc - stutter.TimestampQpc))
            .FirstOrDefault();

        return new StutterWindowDto
        {
            StutterId = stutter.Id,
            TimestampUtcIso = Iso(stutter.TimestampUtc),
            DurationMs = stutter.DurationMs,
            Severity = stutter.Severity.ToString(),
            Detector = stutter.Detector.ToString(),
            UserMarked = stutter.UserMarked,
            WindowFromMs = -corr.PreRollSeconds * 1000.0,
            WindowToMs = corr.PostRollSeconds * 1000.0,
            Correlations = correlations
                .Select(c => new StutterCorrelationDto(
                    c.SignalType, SignalCatalog.DisplayName(c.SignalType), c.Score.ToString(),
                    c.ProximityMs, c.BaseRate * 100.0, c.Detail))
                .ToList(),
            Events = events
                .OrderBy(e => e.TimestampQpc)
                .Select(e => new StutterWindowEventDto(
                    Iso(e.TimestampUtc), e.Category.ToString(), e.Severity.ToString(), e.Source, e.Message))
                .ToList(),
            TopProcesses = (snap?.Rows ?? Array.Empty<ProcessSnapshotRow>())
                .OrderByDescending(r => r.CpuPercent)
                .Take(8)
                .Select(r => new StutterWindowProcessDto(r.Name, r.Pid, r.CpuPercent, r.WorkingSetBytes))
                .ToList(),
            CpuAvgPct = Avg("cpu.total.pct"),
            CpuPeakPct = Peak("cpu.total.pct"),
            GpuAvgPct = Avg("gpu.engine.pct"),
            DiskLatencySpikeMs = MaxOf("disk.read.latency.ms", "disk.write.latency.ms"),
            DpcPeakMs = topDpc?.MaxMs,
            DpcPeakDriver = topDpc?.Driver,
            WheaInWindow = events.Any(e => e.Category is EventCategory.Whea or EventCategory.HardwareError),
            KernelPowerInWindow = events.Any(e => e.Category == EventCategory.KernelPower),
        };
    }

    public async Task<IReadOnlyList<RecentFindingDto>> GetRecentFindingsAsync(int count, CancellationToken ct = default)
    {
        var store = _orchestrator.Store;

        long sessionId = _orchestrator.CurrentSessionId;
        if (sessionId == 0)
        {
            var sessions = await store.GetSessionsAsync(ct).ConfigureAwait(false);
            if (sessions.Count == 0) return Array.Empty<RecentFindingDto>();
            sessionId = sessions[0].Id;   // GetSessionsAsync returns newest first
        }

        var stutters = await store.GetStuttersAsync(sessionId, ct).ConfigureAwait(false);
        if (stutters.Count == 0) return Array.Empty<RecentFindingDto>();

        var all = new List<StutterCorrelation>();
        foreach (var s in stutters)
            all.AddRange(await store.GetCorrelationsAsync(s.Id, ct).ConfigureAwait(false));

        var baseRates = new AveragedStoredBaseRateProvider(all);
        var findings = new DiagnosticHypothesisEngine().Analyze(
            stutters, all, baseRates,
            alwaysReport: new[] { SignalCatalog.TpmTbs, SignalCatalog.Whea });

        return findings
            .Take(Math.Clamp(count, 1, 100))
            .Select(f => new RecentFindingDto(
                f.Score.ToString(), f.SignalType, SignalCatalog.DisplayName(f.SignalType),
                f.Observation, f.Hypothesis, f.NotProven,
                f.CorrelatedStutters, f.TotalStutters, f.BaseRatePercent))
            .ToList();
    }

    /// <summary>Base rate per signal type = mean of the base rates stored on this session's correlations.</summary>
    private sealed class AveragedStoredBaseRateProvider : IBaseRateProvider
    {
        private readonly Dictionary<string, double> _bySignal;

        public AveragedStoredBaseRateProvider(IEnumerable<StutterCorrelation> correlations)
            => _bySignal = correlations
                .GroupBy(c => c.SignalType, StringComparer.Ordinal)
                .ToDictionary(g => g.Key, g => g.Average(c => c.BaseRate), StringComparer.Ordinal);

        public double GetBaseRate(string signalType)
            => _bySignal.TryGetValue(signalType, out var v) ? v : 0.0;
    }

    // ------------------------------------------------------------------

    private static StatusDto ToDto(StatusSnapshot s) => new()
    {
        Monitoring = s.Monitoring,
        SessionId = s.SessionId,
        StartedUtcIso = s.StartedUtc is { } started ? Iso(started) : null,
        MonitoringSeconds = s.MonitoringSeconds,
        Mode = s.Mode,
        StuttersDetected = s.StuttersDetected,
        MajorStutters = s.MajorStutters,
        TpmEvents = s.TpmEvents,
        WheaEvents = s.WheaEvents,
        DpcSpikes = s.DpcSpikes,
        LastEventUtcIso = s.LastEventUtc is { } le ? Iso(le) : null,
        LastEventText = s.LastEventText,
        IsElevated = s.IsElevated,
        Monitors = s.Monitors.Select(m => new MonitorHealthDto(m.Name, m.Status.ToString(), m.Note)).ToList(),
        Warnings = s.Warnings,
    };

    private static string Iso(DateTime dt)
        => DateTime.SpecifyKind(dt, dt.Kind == DateTimeKind.Unspecified ? DateTimeKind.Utc : dt.Kind)
            .ToUniversalTime()
            .ToString("O", CultureInfo.InvariantCulture);

    private static DateTime ParseUtc(string iso, DateTime fallback)
        => DateTime.TryParse(iso, CultureInfo.InvariantCulture,
                DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var v)
            ? v
            : fallback;
}
