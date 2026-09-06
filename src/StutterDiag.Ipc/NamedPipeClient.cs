using System.IO.Pipes;
using System.Text;
using System.Text.Json;

namespace StutterDiag.Ipc;

/// <summary>
/// Newline-delimited-JSON client for <see cref="NamedPipeServer"/>. One request/response at
/// a time per instance (calls are serialised by an internal lock). Reconnects transparently
/// if the pipe drops between calls.
/// </summary>
public sealed class NamedPipeClient : IAsyncDisposable
{
    private readonly string _pipeName;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private NamedPipeClientStream? _stream;
    private StreamReader? _reader;
    private StreamWriter? _writer;

    public NamedPipeClient(string pipeName) => _pipeName = pipeName;

    public bool IsConnected => _stream?.IsConnected == true;

    public async Task<IpcResponse> SendAsync(IpcRequest request, TimeSpan? connectTimeout = null, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            for (int attempt = 0; attempt < 2; attempt++)
            {
                try
                {
                    await EnsureConnectedAsync(connectTimeout ?? TimeSpan.FromSeconds(3), ct).ConfigureAwait(false);
                    await _writer!.WriteLineAsync(JsonSerializer.Serialize(request, IpcJson.Options)).ConfigureAwait(false);
                    string? line = await _reader!.ReadLineAsync(ct).ConfigureAwait(false);
                    if (line is null) throw new IOException("pipe closed by server");
                    return JsonSerializer.Deserialize<IpcResponse>(line, IpcJson.Options)
                           ?? IpcResponse.Failure(request.Id, "empty response");
                }
                catch (Exception ex) when (ex is IOException or InvalidOperationException or ObjectDisposedException)
                {
                    Reset();
                    if (attempt == 1) return IpcResponse.Failure(request.Id, $"transport error: {ex.Message}");
                }
            }
            return IpcResponse.Failure(request.Id, "unreachable");
        }
        finally { _gate.Release(); }
    }

    private async Task EnsureConnectedAsync(TimeSpan timeout, CancellationToken ct)
    {
        if (_stream is { IsConnected: true }) return;
        Reset();

        var stream = new NamedPipeClientStream(".", _pipeName, PipeDirection.InOut,
            PipeOptions.Asynchronous);
        await stream.ConnectAsync((int)timeout.TotalMilliseconds, ct).ConfigureAwait(false);

        _stream = stream;
        _reader = new StreamReader(stream, Encoding.UTF8, false, 1 << 16, leaveOpen: true);
        _writer = new StreamWriter(stream, new UTF8Encoding(false), 1 << 16, leaveOpen: true) { AutoFlush = true };
    }

    private void Reset()
    {
        try { _reader?.Dispose(); } catch { /* ignore */ }
        try { _writer?.Dispose(); } catch { /* ignore */ }
        try { _stream?.Dispose(); } catch { /* ignore */ }
        _reader = null; _writer = null; _stream = null;
    }

    public async ValueTask DisposeAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try { Reset(); }
        finally { _gate.Release(); _gate.Dispose(); }
    }
}
