using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using StutterDiag.Core.Abstractions;
using StutterDiag.Core.Config;

namespace StutterDiag.Service;

/// <summary>
/// The service's <see cref="BackgroundService"/>. On start it initialises the event store,
/// runs the orchestrator's one-shot startup work, and — when
/// <c>Service.AutoStartMonitoring</c> is set — begins a monitoring session. On stop it ends the
/// session, flushes and disposes everything in order.
/// </summary>
public sealed class Worker : BackgroundService
{
    private readonly MonitorOrchestrator _orchestrator;
    private readonly IEventStore _store;
    private readonly AppConfig _config;
    private readonly ILogger<Worker> _log;

    public Worker(MonitorOrchestrator orchestrator, IEventStore store, AppConfig config, ILogger<Worker> log)
    {
        _orchestrator = orchestrator;
        _store = store;
        _config = config;
        _log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _log.LogInformation("StutterDiag service starting; database {Db}", _config.Storage.DatabasePath);

        try
        {
            await _store.InitializeAsync(stoppingToken).ConfigureAwait(false);
            await _orchestrator.InitializeAsync(stoppingToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // Without this the exception faults the host silently (BackgroundService defaults to
            // StopHost) and the operator sees only "start error 3". The most common cause is the
            // service account lacking write access to the data directory; say so, actionably,
            // before failing. Re-thrown because a collector with no store cannot do its job.
            _log.LogCritical(ex,
                "Startup failed initialising storage at {Path}. If this is an access error, ensure the " +
                "service account has Modify on that folder (reinstalling grants it).",
                _config.Storage.DatabasePath);
            throw;
        }

        if (_config.Service.AutoStartMonitoring)
        {
            try
            {
                await _orchestrator.StartAsync("Standard", stoppingToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "auto-start monitoring failed; the service stays up and can be started over IPC");
            }
        }
        else
        {
            _log.LogInformation("AutoStartMonitoring is off; waiting for an IPC start request");
        }

        try
        {
            // Idle until the host signals shutdown. All real work is event/timer driven.
            await Task.Delay(Timeout.Infinite, stoppingToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // expected on shutdown
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        _log.LogInformation("StutterDiag service stopping");
        try { await _orchestrator.StopAsync(cancellationToken).ConfigureAwait(false); }
        catch (Exception ex) { _log.LogWarning(ex, "orchestrator stop faulted"); }

        try { await _store.FlushAsync(cancellationToken).ConfigureAwait(false); }
        catch (Exception ex) { _log.LogWarning(ex, "store flush on stop faulted"); }

        try { await _store.DisposeAsync().ConfigureAwait(false); }
        catch (Exception ex) { _log.LogWarning(ex, "store dispose faulted"); }

        await base.StopAsync(cancellationToken).ConfigureAwait(false);
    }
}
