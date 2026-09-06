namespace StutterDiag.Ipc;

/// <summary>
/// The full control surface exposed by the Windows Service over a local named pipe and
/// consumed by the GUI and CLI. Transport is newline-delimited JSON request/response
/// (see <see cref="NamedPipeServer"/> / <see cref="NamedPipeClient"/>). The service keeps
/// running with the pipe closed; clients are optional.
/// </summary>
public interface IStutterDiagControl
{
    Task<StatusDto> GetStatusAsync(CancellationToken ct = default);

    Task<StatusDto> StartMonitoringAsync(string mode, CancellationToken ct = default);

    Task<StatusDto> StopMonitoringAsync(CancellationToken ct = default);

    Task<IReadOnlyList<RecentEventDto>> GetRecentEventsAsync(int count, CancellationToken ct = default);

    Task<IReadOnlyList<RecentStutterDto>> GetRecentStuttersAsync(int count, CancellationToken ct = default);

    /// <summary>Record a user-marked stutter at "now" and widen high-res capture around it.</summary>
    Task<MarkResultDto> MarkStutterAsync(string? note, CancellationToken ct = default);

    /// <summary>Current effective configuration as JSON (the <c>AppConfig</c> shape).</summary>
    Task<string> GetConfigJsonAsync(CancellationToken ct = default);

    /// <summary>Apply configuration; returns validation warnings (empty = clean).</summary>
    Task<IReadOnlyList<string>> SetConfigJsonAsync(string configJson, CancellationToken ct = default);

    /// <summary>Machine description JSON (the <c>SystemInfo</c> shape).</summary>
    Task<string> GetSystemInfoJsonAsync(CancellationToken ct = default);

    Task<IReadOnlyList<SessionDto>> ListSessionsAsync(CancellationToken ct = default);

    Task<string> GenerateReportAsync(ReportRequestDto request, CancellationToken ct = default);

    Task<IReadOnlyList<TimelineEntryDto>> GetTimelineAsync(long sessionId, string fromUtcIso, string toUtcIso, CancellationToken ct = default);

    /// <summary>The full detail window around one stutter (correlations, events, processes, key metrics).</summary>
    Task<StutterWindowDto?> GetStutterWindowAsync(long sessionId, long stutterId, CancellationToken ct = default);

    /// <summary>
    /// The automatic diagnostic for the current (or most recent) session, as OBSERVATION /
    /// HYPOTHESIS / NOT PROVEN rows. Correlation only — never causation.
    /// </summary>
    Task<IReadOnlyList<RecentFindingDto>> GetRecentFindingsAsync(int count, CancellationToken ct = default);
}

public static class IpcMethods
{
    public const string GetStatus = "GetStatus";
    public const string StartMonitoring = "StartMonitoring";
    public const string StopMonitoring = "StopMonitoring";
    public const string GetRecentEvents = "GetRecentEvents";
    public const string GetRecentStutters = "GetRecentStutters";
    public const string MarkStutter = "MarkStutter";
    public const string GetConfig = "GetConfig";
    public const string SetConfig = "SetConfig";
    public const string GetSystemInfo = "GetSystemInfo";
    public const string ListSessions = "ListSessions";
    public const string GenerateReport = "GenerateReport";
    public const string GetTimeline = "GetTimeline";
    public const string GetStutterWindow = "GetStutterWindow";
    public const string GetRecentFindings = "GetRecentFindings";
}
