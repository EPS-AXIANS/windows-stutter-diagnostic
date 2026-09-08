using StutterDiag.Core.Model;

namespace StutterDiag.Core.Abstractions;

/// <summary>
/// Local persistence for everything collected. The production implementation is
/// <c>SqliteEventStore</c> (WAL, writes batched on a background task). High-frequency
/// appends return <see cref="ValueTask"/> and must not block the caller.
/// </summary>
public interface IEventStore : IAsyncDisposable
{
    Task InitializeAsync(CancellationToken ct);

    Task<long> StartSessionAsync(MonitoringSession session, CancellationToken ct);

    Task EndSessionAsync(long sessionId, DateTime endedUtc, CancellationToken ct);

    /// <summary>
    /// Close any session still marked open (<c>ended_utc IS NULL</c>) by stamping its end at its
    /// own start instant. These are runs a previous process left behind by crashing or being
    /// killed before <see cref="EndSessionAsync"/> ran. Call once at startup, before any new
    /// session is opened; returns the number closed. Without it such sessions are never pruned
    /// (retention only deletes finished sessions), so the database grows without bound.
    /// </summary>
    Task<int> CloseOpenSessionsAsync(CancellationToken ct);

    // ---- write side (hot path: batched) ----
    ValueTask AppendEventAsync(MonitorEvent e);

    ValueTask AppendSampleAsync(MetricSample s);

    ValueTask AppendDpcIsrStatAsync(DpcIsrStat s);

    Task AddStutterAsync(Stutter s, CancellationToken ct);

    /// <summary>Insert a stutter and return its new row id (to attach correlations to).</summary>
    Task<long> AddStutterReturningIdAsync(Stutter s, CancellationToken ct);

    Task AddCorrelationsAsync(long stutterId, IEnumerable<StutterCorrelation> correlations, CancellationToken ct);

    Task AddProcessSnapshotAsync(ProcessSnapshot snapshot, CancellationToken ct);

    Task SaveSystemInfoAsync(long sessionId, SystemInfo info, CancellationToken ct);

    Task SaveDriversAsync(long sessionId, IEnumerable<DriverInfo> drivers, CancellationToken ct);

    Task RecordHealthAsync(long sessionId, string monitor, MonitorHealth health, CancellationToken ct);

    Task FlushAsync(CancellationToken ct);

    // ---- read side (report regeneration / GUI history) ----
    Task<IReadOnlyList<MonitoringSession>> GetSessionsAsync(CancellationToken ct);

    Task<MonitoringSession?> GetSessionAsync(long sessionId, CancellationToken ct);

    Task<IReadOnlyList<MonitorEvent>> GetEventsAsync(long sessionId, long fromQpc, long toQpc, CancellationToken ct);

    Task<IReadOnlyList<MetricSample>> GetSamplesAsync(long sessionId, long fromQpc, long toQpc, CancellationToken ct);

    Task<IReadOnlyList<Stutter>> GetStuttersAsync(long sessionId, CancellationToken ct);

    /// <summary>Aggregate counts for one session, computed with GROUP BY (no row loading).</summary>
    Task<SessionCounts> GetSessionCountsAsync(long sessionId, CancellationToken ct);

    Task<IReadOnlyList<StutterCorrelation>> GetCorrelationsAsync(long stutterId, CancellationToken ct);

    Task<IReadOnlyList<DpcIsrStat>> GetDpcIsrStatsAsync(long sessionId, long fromQpc, long toQpc, CancellationToken ct);

    Task<IReadOnlyList<ProcessSnapshot>> GetProcessSnapshotsAsync(long sessionId, long fromQpc, long toQpc, CancellationToken ct);

    Task<SystemInfo?> GetSystemInfoAsync(long sessionId, CancellationToken ct);

    Task<IReadOnlyList<DriverInfo>> GetDriversAsync(long sessionId, CancellationToken ct);

    /// <summary>Total on-disk size in bytes (for the retention manager).</summary>
    Task<long> GetDatabaseSizeBytesAsync(CancellationToken ct);

    /// <summary>Delete sessions (and their rows) older than <paramref name="olderThanUtc"/>.</summary>
    Task<int> PruneSessionsAsync(DateTime olderThanUtc, CancellationToken ct);
}
