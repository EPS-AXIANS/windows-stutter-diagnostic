using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Serilog;
using StutterDiag.Core.Abstractions;
using StutterDiag.Core.Config;
using StutterDiag.Core.Storage;
using StutterDiag.Core.Time;
using StutterDiag.Ipc;
using StutterDiag.Service.Ipc;

namespace StutterDiag.Service;

/// <summary>
/// Entry point. With no verb (or under the SCM) it runs the generic host: Serilog from config,
/// <c>AppConfig</c> bound from <c>appsettings.json</c>, the collector orchestrator, the IPC pipe
/// server and the background worker. With a verb (<c>install|uninstall|start|stop|status</c>) it
/// runs <see cref="ServiceControl"/> and exits.
/// </summary>
internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        if (args.Length > 0 && ServiceControl.IsVerb(args[0]))
            return ServiceControl.Run(args);

        var builder = Host.CreateApplicationBuilder(args);

        builder.Configuration
            .SetBasePath(AppContext.BaseDirectory)
            .AddJsonFile("appsettings.json", optional: false, reloadOnChange: false)
            .AddEnvironmentVariables(prefix: "STUTTERDIAG_")
            .AddCommandLine(args);

        // Serilog.Settings.Configuration does not expand %ENV% tokens; do it for file sink paths.
        ExpandSerilogPaths(builder.Configuration);

        Log.Logger = new LoggerConfiguration()
            .ReadFrom.Configuration(builder.Configuration)
            .CreateLogger();

        try
        {
            var (appConfig, warnings) = ConfigLoader.FromConfiguration(builder.Configuration);
            foreach (var w in warnings) Log.Warning("Config: {Warning}", w);

            builder.Services.AddSerilog();

            builder.Services.AddSingleton(appConfig);
            builder.Services.AddSingleton(new StartupWarnings(warnings));
            builder.Services.AddSingleton<QpcClock>();
            builder.Services.AddSingleton<IEventStore>(sp =>
                new SqliteEventStore(appConfig.Storage.DatabasePath, sp.GetRequiredService<QpcClock>()));
            builder.Services.AddSingleton<MonitorOrchestrator>();
            builder.Services.AddSingleton<IStutterDiagControl>(sp =>
                new StutterDiagControl(sp.GetRequiredService<MonitorOrchestrator>(), sp.GetRequiredService<QpcClock>()));

            // Start order = registration order: worker first (opens the store, starts a session),
            // then the IPC host. Shutdown is LIFO, so IPC stops before the worker tears the session down.
            builder.Services.AddHostedService<Worker>();
            builder.Services.AddHostedService<IpcHost>();

            builder.Services.AddWindowsService(o => o.ServiceName = ServiceControl.ServiceName);

            using var host = builder.Build();
            await host.RunAsync().ConfigureAwait(false);
            return 0;
        }
        catch (Exception ex)
        {
            Log.Fatal(ex, "StutterDiag service terminated unexpectedly");
            return 1;
        }
        finally
        {
            Log.CloseAndFlush();
        }
    }

    private static void ExpandSerilogPaths(IConfiguration configuration)
    {
        foreach (var sink in configuration.GetSection("Serilog:WriteTo").GetChildren())
        {
            string key = $"Serilog:WriteTo:{sink.Key}:Args:path";
            string? raw = configuration[key];
            if (!string.IsNullOrEmpty(raw))
                configuration[key] = Environment.ExpandEnvironmentVariables(raw);
        }
    }
}
