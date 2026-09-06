using System.Text.Json;

namespace StutterDiag.Ipc;

/// <summary>
/// <see cref="IStutterDiagControl"/> implemented over a <see cref="NamedPipeClient"/>.
/// Used by the GUI and CLI. If the service is not running, calls fail fast with an
/// <see cref="IpcException"/> the caller can surface as "service unavailable".
/// </summary>
public sealed class IpcClientProxy : IStutterDiagControl, IAsyncDisposable
{
    private readonly NamedPipeClient _client;

    public IpcClientProxy(string pipeName) => _client = new NamedPipeClient(pipeName);

    public Task<StatusDto> GetStatusAsync(CancellationToken ct = default)
        => CallAsync<StatusDto>(IpcMethods.GetStatus, null, ct);

    public Task<StatusDto> StartMonitoringAsync(string mode, CancellationToken ct = default)
        => CallAsync<StatusDto>(IpcMethods.StartMonitoring, new { mode }, ct);

    public Task<StatusDto> StopMonitoringAsync(CancellationToken ct = default)
        => CallAsync<StatusDto>(IpcMethods.StopMonitoring, null, ct);

    public Task<IReadOnlyList<RecentEventDto>> GetRecentEventsAsync(int count, CancellationToken ct = default)
        => CallAsync<IReadOnlyList<RecentEventDto>>(IpcMethods.GetRecentEvents, new { count }, ct);

    public Task<IReadOnlyList<RecentStutterDto>> GetRecentStuttersAsync(int count, CancellationToken ct = default)
        => CallAsync<IReadOnlyList<RecentStutterDto>>(IpcMethods.GetRecentStutters, new { count }, ct);

    public Task<MarkResultDto> MarkStutterAsync(string? note, CancellationToken ct = default)
        => CallAsync<MarkResultDto>(IpcMethods.MarkStutter, new { note }, ct);

    public Task<string> GetConfigJsonAsync(CancellationToken ct = default)
        => CallAsync<string>(IpcMethods.GetConfig, null, ct);

    public Task<IReadOnlyList<string>> SetConfigJsonAsync(string configJson, CancellationToken ct = default)
        => CallAsync<IReadOnlyList<string>>(IpcMethods.SetConfig, new { configJson }, ct);

    public Task<string> GetSystemInfoJsonAsync(CancellationToken ct = default)
        => CallAsync<string>(IpcMethods.GetSystemInfo, null, ct);

    public Task<IReadOnlyList<SessionDto>> ListSessionsAsync(CancellationToken ct = default)
        => CallAsync<IReadOnlyList<SessionDto>>(IpcMethods.ListSessions, null, ct);

    public Task<string> GenerateReportAsync(ReportRequestDto request, CancellationToken ct = default)
        => CallAsync<string>(IpcMethods.GenerateReport, request, ct);

    public Task<IReadOnlyList<TimelineEntryDto>> GetTimelineAsync(long sessionId, string fromUtcIso, string toUtcIso, CancellationToken ct = default)
        => CallAsync<IReadOnlyList<TimelineEntryDto>>(IpcMethods.GetTimeline, new { sessionId, fromUtcIso, toUtcIso }, ct);

    public Task<StutterWindowDto?> GetStutterWindowAsync(long sessionId, long stutterId, CancellationToken ct = default)
        => CallOrNullAsync<StutterWindowDto>(IpcMethods.GetStutterWindow, new { sessionId, stutterId }, ct);

    public Task<IReadOnlyList<RecentFindingDto>> GetRecentFindingsAsync(int count, CancellationToken ct = default)
        => CallAsync<IReadOnlyList<RecentFindingDto>>(IpcMethods.GetRecentFindings, new { count }, ct);

    private async Task<T> CallAsync<T>(string method, object? parameters, CancellationToken ct)
    {
        var req = new IpcRequest
        {
            Method = method,
            Params = parameters is null ? null : JsonSerializer.SerializeToElement(parameters, IpcJson.Options)
        };
        var res = await _client.SendAsync(req, ct: ct).ConfigureAwait(false);
        if (!res.Ok) throw new IpcException(res.Error ?? "unknown IPC error");

        if (typeof(T) == typeof(string) && res.Result is { ValueKind: JsonValueKind.String } s)
            return (T)(object)s.GetString()!;

        return IpcJson.Deserialize<T>(res.Result)
               ?? throw new IpcException($"could not deserialize {method} result as {typeof(T).Name}");
    }

    private async Task<T?> CallOrNullAsync<T>(string method, object? parameters, CancellationToken ct) where T : class
    {
        var req = new IpcRequest
        {
            Method = method,
            Params = parameters is null ? null : JsonSerializer.SerializeToElement(parameters, IpcJson.Options)
        };
        var res = await _client.SendAsync(req, ct: ct).ConfigureAwait(false);
        if (!res.Ok) throw new IpcException(res.Error ?? "unknown IPC error");
        if (res.Result is null or { ValueKind: JsonValueKind.Null or JsonValueKind.Undefined }) return null;
        return IpcJson.Deserialize<T>(res.Result);
    }

    public ValueTask DisposeAsync() => _client.DisposeAsync();
}

/// <summary>Raised when an IPC call fails (bad request, service-side error, or transport failure).</summary>
public sealed class IpcException : Exception
{
    public IpcException(string message) : base(message) { }
}
