using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using StutterDiag.Core.Config;
using StutterDiag.Ipc;

namespace StutterDiag.Service.Ipc;

/// <summary>
/// Hosts the named-pipe IPC server for the service's lifetime. It wires
/// <see cref="IpcDispatcher"/> over <see cref="StutterDiagControl"/> and gives
/// <see cref="NamedPipeServer"/> the ACL-aware <see cref="PipeStreamFactory"/> so a
/// non-elevated GUI can connect while the service runs as a service account.
/// </summary>
public sealed class IpcHost : IHostedService, IAsyncDisposable
{
    private readonly NamedPipeServer _server;
    private readonly ILogger<IpcHost> _log;
    private readonly string _pipeName;
    private int _disposed;

    public IpcHost(IStutterDiagControl control, AppConfig config, ILogger<IpcHost> log)
    {
        _log = log;
        _pipeName = string.IsNullOrWhiteSpace(config.Service.IpcPipeName)
            ? "StutterDiag.Service"
            : config.Service.IpcPipeName;

        var dispatcher = new IpcDispatcher(control);
        _server = new NamedPipeServer(_pipeName, dispatcher.HandleAsync, PipeStreamFactory.Create);
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _server.Start();
        _log.LogInformation(@"IPC listening on \\.\pipe\{Pipe}", _pipeName);
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken) => await DisposeAsync().ConfigureAwait(false);

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1) return;
        try { await _server.DisposeAsync().ConfigureAwait(false); }
        catch (Exception ex) { _log.LogDebug(ex, "IPC server dispose"); }
    }
}
