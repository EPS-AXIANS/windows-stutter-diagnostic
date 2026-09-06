using System.IO.Pipes;
using System.Text;
using System.Text.Json;

namespace StutterDiag.Ipc;

/// <summary>
/// Minimal newline-delimited-JSON server over a named pipe. Transport only: it hands each
/// decoded <see cref="IpcRequest"/> to <c>handler</c> and writes back the
/// <see cref="IpcResponse"/>. Multiple concurrent client connections are supported.
/// </summary>
/// <remarks>
/// The <paramref name="streamFactory"/> lets the host supply a pipe configured with an ACL
/// (so a non-elevated GUI can talk to a service running as LocalSystem). When null, a
/// default local-only pipe is created.
/// </remarks>
public sealed class NamedPipeServer : IAsyncDisposable
{
    private readonly string _pipeName;
    private readonly Func<IpcRequest, CancellationToken, Task<IpcResponse>> _handler;
    private readonly Func<string, NamedPipeServerStream>? _streamFactory;
    private readonly int _maxConcurrentConnections;
    private readonly CancellationTokenSource _cts = new();
    private Task? _acceptLoop;

    public NamedPipeServer(
        string pipeName,
        Func<IpcRequest, CancellationToken, Task<IpcResponse>> handler,
        Func<string, NamedPipeServerStream>? streamFactory = null,
        int maxConcurrentConnections = 8)
    {
        _pipeName = pipeName;
        _handler = handler;
        _streamFactory = streamFactory;
        _maxConcurrentConnections = maxConcurrentConnections;
    }

    public void Start() => _acceptLoop = Task.Run(() => AcceptLoopAsync(_cts.Token));

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        var slots = new SemaphoreSlim(_maxConcurrentConnections);
        while (!ct.IsCancellationRequested)
        {
            await slots.WaitAsync(ct).ConfigureAwait(false);
            NamedPipeServerStream server;
            try
            {
                server = _streamFactory?.Invoke(_pipeName) ?? CreateDefault(_pipeName);
            }
            catch (Exception)
            {
                slots.Release();
                await Task.Delay(500, ct).ConfigureAwait(false);
                continue;
            }

            _ = Task.Run(async () =>
            {
                try
                {
                    await server.WaitForConnectionAsync(ct).ConfigureAwait(false);
                    await ServeConnectionAsync(server, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException) { }
                catch (Exception) { /* connection-level failure: drop it */ }
                finally
                {
                    try { server.Dispose(); } catch { /* ignore */ }
                    slots.Release();
                }
            }, ct);
        }
    }

    private async Task ServeConnectionAsync(NamedPipeServerStream server, CancellationToken ct)
    {
        using var reader = new StreamReader(server, Encoding.UTF8, false, 1 << 16, leaveOpen: true);
        await using var writer = new StreamWriter(server, new UTF8Encoding(false), 1 << 16, leaveOpen: true) { AutoFlush = true };

        while (!ct.IsCancellationRequested && server.IsConnected)
        {
            string? line = await reader.ReadLineAsync(ct).ConfigureAwait(false);
            if (line is null) break;
            if (line.Length == 0) continue;

            IpcResponse response;
            try
            {
                var request = JsonSerializer.Deserialize<IpcRequest>(line, IpcJson.Options)
                              ?? throw new JsonException("empty request");
                response = await _handler(request, ct).ConfigureAwait(false);
            }
            catch (JsonException ex)
            {
                response = IpcResponse.Failure("", $"malformed request: {ex.Message}");
            }
            catch (Exception ex)
            {
                response = IpcResponse.Failure("", ex.Message);
            }

            await writer.WriteLineAsync(JsonSerializer.Serialize(response, IpcJson.Options)).ConfigureAwait(false);
        }
    }

    // Default pipe: same-user (portable) scenario. When the service runs as LocalSystem it
    // MUST pass a streamFactory that applies an ACL granting the interactive user, so a
    // non-elevated GUI can connect (see StutterDiag.Service.Ipc.PipeStreamFactory).
    private static NamedPipeServerStream CreateDefault(string pipeName) => new(
        pipeName,
        PipeDirection.InOut,
        NamedPipeServerStream.MaxAllowedServerInstances,
        PipeTransmissionMode.Byte,
        PipeOptions.Asynchronous);

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        if (_acceptLoop is not null)
        {
            try { await _acceptLoop.ConfigureAwait(false); } catch { /* ignore */ }
        }
        _cts.Dispose();
    }
}
