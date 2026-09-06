using CommunityToolkit.Mvvm.ComponentModel;
using StutterDiag.Ipc;

namespace StutterDiag.Gui.Services;

/// <summary>
/// Thin wrapper around <see cref="IpcClientProxy"/>. Every backend call funnels through
/// one of the <c>Try…</c> helpers, which turn an <see cref="IpcException"/> (or any transport
/// failure) into a friendly "service unavailable" state instead of throwing. The underlying
/// <see cref="NamedPipeClient"/> already reconnects transparently, so recovery is automatic
/// on the next successful call.
/// </summary>
public sealed partial class ServiceConnection : ObservableObject, IAsyncDisposable
{
    public const string ServiceDownMessage =
        "Service not running — is the StutterDiag Windows service installed and started?";

    private readonly IpcClientProxy _proxy;

    /// <param name="pipeName">
    /// The IPC pipe name, resolved by the caller from the persisted config
    /// (<c>AppConfig.Service.IpcPipeName</c>); the built-in fallback is
    /// <c>"StutterDiag.Service"</c>.
    /// </param>
    public ServiceConnection(string pipeName)
    {
        PipeName = string.IsNullOrWhiteSpace(pipeName) ? "StutterDiag.Service" : pipeName;
        _proxy = new IpcClientProxy(PipeName);
    }

    public string PipeName { get; }

    /// <summary>True after the last call round-tripped successfully. Starts false (unknown).</summary>
    [ObservableProperty]
    private bool _isServiceAvailable;

    /// <summary>Human-readable connection state for the app-wide banner.</summary>
    [ObservableProperty]
    private string _serviceStateText = "Connecting to StutterDiag service…";

    /// <summary>UTC of the last successful call (for a subtle "last updated" hint).</summary>
    [ObservableProperty]
    private DateTime? _lastContactUtc;

    // ---- Typed pass-throughs (all null / empty tolerant) ----------------------------------

    public Task<StatusDto?> GetStatusAsync(CancellationToken ct = default) =>
        TryOrNullAsync(p => p.GetStatusAsync(ct));

    public Task<StatusDto?> StartMonitoringAsync(string mode, CancellationToken ct = default) =>
        TryOrNullAsync(p => p.StartMonitoringAsync(mode, ct));

    public Task<StatusDto?> StopMonitoringAsync(CancellationToken ct = default) =>
        TryOrNullAsync(p => p.StopMonitoringAsync(ct));

    public Task<IReadOnlyList<RecentEventDto>> GetRecentEventsAsync(int count, CancellationToken ct = default) =>
        TryOrEmptyAsync(p => p.GetRecentEventsAsync(count, ct), Array.Empty<RecentEventDto>());

    public Task<IReadOnlyList<RecentStutterDto>> GetRecentStuttersAsync(int count, CancellationToken ct = default) =>
        TryOrEmptyAsync(p => p.GetRecentStuttersAsync(count, ct), Array.Empty<RecentStutterDto>());

    public Task<MarkResultDto?> MarkStutterAsync(string? note, CancellationToken ct = default) =>
        TryOrNullAsync(p => p.MarkStutterAsync(note, ct));

    public Task<string?> GetConfigJsonAsync(CancellationToken ct = default) =>
        TryOrNullAsync(p => p.GetConfigJsonAsync(ct));

    public Task<IReadOnlyList<string>> SetConfigJsonAsync(string configJson, CancellationToken ct = default) =>
        TryOrEmptyAsync(p => p.SetConfigJsonAsync(configJson, ct), Array.Empty<string>());

    public Task<string?> GetSystemInfoJsonAsync(CancellationToken ct = default) =>
        TryOrNullAsync(p => p.GetSystemInfoJsonAsync(ct));

    public Task<IReadOnlyList<SessionDto>> ListSessionsAsync(CancellationToken ct = default) =>
        TryOrEmptyAsync(p => p.ListSessionsAsync(ct), Array.Empty<SessionDto>());

    public Task<string?> GenerateReportAsync(ReportRequestDto request, CancellationToken ct = default) =>
        TryOrNullAsync(p => p.GenerateReportAsync(request, ct));

    public Task<IReadOnlyList<TimelineEntryDto>> GetTimelineAsync(
        long sessionId, string fromUtcIso, string toUtcIso, CancellationToken ct = default) =>
        TryOrEmptyAsync(p => p.GetTimelineAsync(sessionId, fromUtcIso, toUtcIso, ct), Array.Empty<TimelineEntryDto>());

    public async Task<StutterWindowDto?> GetStutterWindowAsync(long sessionId, long stutterId, CancellationToken ct = default)
    {
        try
        {
            var result = await _proxy.GetStutterWindowAsync(sessionId, stutterId, ct).ConfigureAwait(true);
            MarkUp();
            return result;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { MarkDown(ex.Message); return null; }
    }

    public Task<IReadOnlyList<RecentFindingDto>> GetRecentFindingsAsync(int count, CancellationToken ct = default) =>
        TryOrEmptyAsync(p => p.GetRecentFindingsAsync(count, ct), Array.Empty<RecentFindingDto>());

    // ---- Core error funnel ---------------------------------------------------------------

    private async Task<T?> TryOrNullAsync<T>(Func<IpcClientProxy, Task<T>> call) where T : class
    {
        try
        {
            var result = await call(_proxy).ConfigureAwait(true);
            MarkUp();
            return result;
        }
        catch (OperationCanceledException) { throw; }
        catch (IpcException ex) { MarkDown(ex.Message); return null; }
        catch (Exception ex) { MarkDown(ex.Message); return null; }
    }

    private async Task<T> TryOrEmptyAsync<T>(Func<IpcClientProxy, Task<T>> call, T fallback)
    {
        try
        {
            var result = await call(_proxy).ConfigureAwait(true);
            MarkUp();
            return result;
        }
        catch (OperationCanceledException) { throw; }
        catch (IpcException ex) { MarkDown(ex.Message); return fallback; }
        catch (Exception ex) { MarkDown(ex.Message); return fallback; }
    }

    private void MarkUp()
    {
        LastContactUtc = DateTime.UtcNow;
        if (!IsServiceAvailable)
        {
            IsServiceAvailable = true;
            ServiceStateText = "Connected to StutterDiag service.";
            GuiLog.Info("IPC connection established.");
        }
    }

    private void MarkDown(string detail)
    {
        if (IsServiceAvailable || ServiceStateText != ServiceDownMessage)
        {
            IsServiceAvailable = false;
            ServiceStateText = ServiceDownMessage;
            GuiLog.Warn($"IPC unavailable: {detail}");
        }
    }

    public ValueTask DisposeAsync() => _proxy.DisposeAsync();
}
