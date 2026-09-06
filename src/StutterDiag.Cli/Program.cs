using StutterDiag.Core.Config;
using StutterDiag.Ipc;

namespace StutterDiag.Cli;

/// <summary>
/// <c>stutterdiag &lt;verb&gt;</c> — a thin client over the service's named-pipe IPC.
/// Verbs: <c>status</c>, <c>start [--mode Standard|Gaming]</c>, <c>stop</c>, <c>sessions</c>,
/// <c>report --format html|json|csv|zip --out &lt;path&gt; [--sessions id,id] [--compare]</c>,
/// <c>compare --a &lt;id&gt; --b &lt;id&gt; --out &lt;path&gt; [--format html|json|csv|zip]</c>.
/// Exit codes: 0 ok, 1 usage, 2 service-unavailable, 3 operation-failed.
/// </summary>
internal static class Program
{
    private const string ServiceDownMessage =
        "StutterDiag service is not reachable. Is the Windows service installed and started?";

    private static async Task<int> Main(string[] args)
    {
        var cli = CliArgs.Parse(args);
        if (cli.Verb is "" or "-h" or "--help" or "help")
            return Usage();

        string pipeName = ResolvePipeName(cli);
        await using var client = new IpcClientProxy(pipeName);

        try
        {
            return cli.Verb switch
            {
                "status" => await StatusAsync(client),
                "start" => await StartAsync(client, cli),
                "stop" => await StopAsync(client),
                "sessions" => await SessionsAsync(client),
                "report" => await ReportAsync(client, cli),
                "compare" => await CompareAsync(client, cli),
                _ => Usage(),
            };
        }
        catch (TimeoutException)
        {
            Console.Error.WriteLine(ServiceDownMessage);
            return 2;
        }
        catch (IpcException ex) when (LooksLikeTransport(ex.Message))
        {
            Console.Error.WriteLine(ServiceDownMessage);
            return 2;
        }
        catch (IpcException ex)
        {
            Console.Error.WriteLine($"operation failed: {ex.Message}");
            return 3;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"error: {ex.Message}");
            return 3;
        }
    }

    // ------------------------------------------------------------------

    private static async Task<int> StatusAsync(IStutterDiagControl client)
    {
        var s = await client.GetStatusAsync();
        Console.WriteLine($"Monitoring     : {(s.Monitoring ? "yes" : "no")}  (mode {s.Mode})");
        if (s.Monitoring)
        {
            Console.WriteLine($"Session        : {s.SessionId}  since {s.StartedUtcIso}  ({s.MonitoringSeconds:F0}s)");
        }
        Console.WriteLine($"Stutters       : {s.StuttersDetected}  (major {s.MajorStutters})");
        Console.WriteLine($"TPM / WHEA     : {s.TpmEvents} / {s.WheaEvents}");
        Console.WriteLine($"DPC spikes     : {s.DpcSpikes}");
        Console.WriteLine($"Elevated       : {(s.IsElevated ? "yes" : "no")}");
        if (s.LastEventText is not null)
            Console.WriteLine($"Last event     : {s.LastEventUtcIso}  {s.LastEventText}");

        if (s.Monitors.Count > 0)
        {
            Console.WriteLine("Monitors:");
            foreach (var m in s.Monitors)
                Console.WriteLine($"  {m.Name,-22} {m.Status}{(m.Note is null ? "" : $"  ({m.Note})")}");
        }
        foreach (var w in s.Warnings)
            Console.WriteLine($"! {w}");
        return 0;
    }

    private static async Task<int> StartAsync(IStutterDiagControl client, CliArgs cli)
    {
        string mode = cli.Get("mode", "Standard");
        var s = await client.StartMonitoringAsync(mode);
        Console.WriteLine(s.Monitoring
            ? $"Monitoring started (mode {s.Mode}, session {s.SessionId})."
            : "Start requested, but the service reports monitoring is not active.");
        return s.Monitoring ? 0 : 3;
    }

    private static async Task<int> StopAsync(IStutterDiagControl client)
    {
        var s = await client.StopMonitoringAsync();
        Console.WriteLine(s.Monitoring ? "Stop requested, but monitoring is still active." : "Monitoring stopped.");
        return s.Monitoring ? 3 : 0;
    }

    private static async Task<int> SessionsAsync(IStutterDiagControl client)
    {
        var sessions = await client.ListSessionsAsync();
        if (sessions.Count == 0)
        {
            Console.WriteLine("(no sessions recorded yet)");
            return 0;
        }

        Console.WriteLine($"{"ID",-6} {"Started (UTC)",-26} {"Mode",-9} {"TPM",-13} {"Stutters",-9} Label");
        foreach (var s in sessions)
            Console.WriteLine($"{s.Id,-6} {s.StartedUtcIso,-26} {s.Mode,-9} {s.TpmType,-13} {s.StutterCount,-9} {s.Label}");
        return 0;
    }

    private static async Task<int> ReportAsync(IStutterDiagControl client, CliArgs cli)
    {
        string? outPath = cli.Get("out");
        if (string.IsNullOrWhiteSpace(outPath))
        {
            Console.Error.WriteLine("report: --out <path> is required");
            return 1;
        }

        string format = NormalizeFormat(cli.Get("format") ?? InferFormat(outPath));
        var sessionIds = cli.LongList("sessions");
        bool compare = cli.Flag("compare");

        if (sessionIds.Count == 0)
        {
            var all = await client.ListSessionsAsync();
            if (all.Count == 0)
            {
                Console.Error.WriteLine("report: no sessions to report on");
                return 3;
            }
            sessionIds = new[] { all.Max(s => s.Id) };  // most recent session
        }

        if (compare && sessionIds.Count != 2)
        {
            Console.Error.WriteLine("report --compare needs exactly two --sessions ids");
            return 1;
        }

        var request = new ReportRequestDto
        {
            Format = format,
            OutputPath = outPath,
            SessionIds = sessionIds,
            CompareMode = compare,
        };

        string written = await client.GenerateReportAsync(request);
        Console.WriteLine($"Report written: {written}");
        return 0;
    }

    private static async Task<int> CompareAsync(IStutterDiagControl client, CliArgs cli)
    {
        string? a = cli.Get("a");
        string? b = cli.Get("b");
        string? outPath = cli.Get("out");
        if (a is null || b is null || string.IsNullOrWhiteSpace(outPath) ||
            !long.TryParse(a, out var idA) || !long.TryParse(b, out var idB))
        {
            Console.Error.WriteLine("compare: --a <id> --b <id> --out <path> are all required");
            return 1;
        }

        var request = new ReportRequestDto
        {
            Format = NormalizeFormat(cli.Get("format") ?? InferFormat(outPath)),
            OutputPath = outPath,
            SessionIds = new[] { idA, idB },
            CompareMode = true,
        };

        string written = await client.GenerateReportAsync(request);
        Console.WriteLine($"Comparison report written: {written}");
        return 0;
    }

    // ------------------------------------------------------------------

    private static int Usage()
    {
        Console.WriteLine(
            """
            usage: stutterdiag <verb> [options]

              status
              start   [--mode Standard|Gaming]
              stop
              sessions
              report  --out <path> [--format html|json|csv|zip] [--sessions id,id] [--compare]
              compare --a <id> --b <id> --out <path> [--format html|json|csv|zip]

            global:
              --pipe <name>   IPC pipe name (default: from AppConfig, "StutterDiag.Service")

            exit codes: 0 ok, 1 usage, 2 service-unavailable, 3 operation-failed
            """);
        return 1;
    }

    private static string ResolvePipeName(CliArgs cli)
    {
        string? overridden = cli.Get("pipe");
        if (!string.IsNullOrWhiteSpace(overridden)) return overridden;
        return AppConfigDefaults.Create().Service.IpcPipeName;
    }

    private static string InferFormat(string path)
    {
        string ext = Path.GetExtension(path).TrimStart('.').ToLowerInvariant();
        return ext switch
        {
            "json" => "Json",
            "csv" => "Csv",
            "zip" => "Zip",
            _ => "Html",
        };
    }

    private static string NormalizeFormat(string format) => format.Trim().ToLowerInvariant() switch
    {
        "json" => "Json",
        "csv" => "Csv",
        "zip" => "Zip",
        _ => "Html",
    };

    private static bool LooksLikeTransport(string message)
    {
        foreach (var token in new[] { "transport error", "unreachable", "pipe", "timed out", "cannot connect", "closed by server" })
            if (message.Contains(token, StringComparison.OrdinalIgnoreCase))
                return true;
        return false;
    }
}
