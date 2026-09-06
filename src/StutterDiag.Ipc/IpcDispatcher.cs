using System.Text.Json;

namespace StutterDiag.Ipc;

/// <summary>
/// Maps an <see cref="IpcRequest"/> onto an <see cref="IStutterDiagControl"/> implementation.
/// The service wires this into <see cref="NamedPipeServer"/>'s handler delegate.
/// </summary>
public sealed class IpcDispatcher
{
    private readonly IStutterDiagControl _control;

    public IpcDispatcher(IStutterDiagControl control) => _control = control;

    public async Task<IpcResponse> HandleAsync(IpcRequest request, CancellationToken ct)
    {
        try
        {
            object? result = request.Method switch
            {
                IpcMethods.GetStatus => await _control.GetStatusAsync(ct).ConfigureAwait(false),
                IpcMethods.StartMonitoring => await _control.StartMonitoringAsync(Str(request, "mode", "Standard"), ct).ConfigureAwait(false),
                IpcMethods.StopMonitoring => await _control.StopMonitoringAsync(ct).ConfigureAwait(false),
                IpcMethods.GetRecentEvents => await _control.GetRecentEventsAsync(Int(request, "count", 50), ct).ConfigureAwait(false),
                IpcMethods.GetRecentStutters => await _control.GetRecentStuttersAsync(Int(request, "count", 50), ct).ConfigureAwait(false),
                IpcMethods.MarkStutter => await _control.MarkStutterAsync(StrOrNull(request, "note"), ct).ConfigureAwait(false),
                IpcMethods.GetConfig => await _control.GetConfigJsonAsync(ct).ConfigureAwait(false),
                IpcMethods.SetConfig => await _control.SetConfigJsonAsync(Str(request, "configJson", "{}"), ct).ConfigureAwait(false),
                IpcMethods.GetSystemInfo => await _control.GetSystemInfoJsonAsync(ct).ConfigureAwait(false),
                IpcMethods.ListSessions => await _control.ListSessionsAsync(ct).ConfigureAwait(false),
                IpcMethods.GenerateReport => await _control.GenerateReportAsync(
                    IpcJson.Deserialize<ReportRequestDto>(request.Params) ?? new ReportRequestDto(), ct).ConfigureAwait(false),
                IpcMethods.GetTimeline => await _control.GetTimelineAsync(
                    LongVal(request, "sessionId", 0),
                    Str(request, "fromUtcIso", ""),
                    Str(request, "toUtcIso", ""), ct).ConfigureAwait(false),
                IpcMethods.GetStutterWindow => await _control.GetStutterWindowAsync(
                    LongVal(request, "sessionId", 0),
                    LongVal(request, "stutterId", 0), ct).ConfigureAwait(false),
                IpcMethods.GetRecentFindings => await _control.GetRecentFindingsAsync(
                    Int(request, "count", 20), ct).ConfigureAwait(false),
                _ => throw new InvalidOperationException($"unknown method '{request.Method}'")
            };

            return IpcResponse.Success(request.Id, result);
        }
        catch (OperationCanceledException)
        {
            return IpcResponse.Failure(request.Id, "cancelled");
        }
        catch (Exception ex)
        {
            return IpcResponse.Failure(request.Id, ex.Message);
        }
    }

    private static string Str(IpcRequest r, string name, string fallback)
        => TryProp(r, name, out var el) && el.ValueKind == JsonValueKind.String ? el.GetString()! : fallback;

    private static string? StrOrNull(IpcRequest r, string name)
        => TryProp(r, name, out var el) && el.ValueKind == JsonValueKind.String ? el.GetString() : null;

    private static int Int(IpcRequest r, string name, int fallback)
        => TryProp(r, name, out var el) && el.TryGetInt32(out var v) ? v : fallback;

    private static long LongVal(IpcRequest r, string name, long fallback)
        => TryProp(r, name, out var el) && el.TryGetInt64(out var v) ? v : fallback;

    private static bool TryProp(IpcRequest r, string name, out JsonElement value)
    {
        if (r.Params is { ValueKind: JsonValueKind.Object } p && p.TryGetProperty(name, out value)) return true;
        value = default;
        return false;
    }
}
