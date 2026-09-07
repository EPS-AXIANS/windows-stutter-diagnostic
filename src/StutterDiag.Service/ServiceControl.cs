using System.Diagnostics;
using System.Runtime.Versioning;
using System.Security.Principal;
using System.ServiceProcess;
using StutterDiag.Service.Interop;

namespace StutterDiag.Service;

/// <summary>
/// The <c>install | uninstall | start | stop | status</c> verbs. Every verb prints what it did.
/// The mutating verbs require elevation and relaunch themselves with the <c>runas</c> verb when
/// not already elevated. Install registers the service delayed-auto on the service-scoped
/// virtual account <c>NT SERVICE\StutterDiag.Service</c> (least privilege; ARCHITECTURE §9),
/// falling back to <c>LocalSystem</c>, and grants the account its privileges via LSA.
/// </summary>
public static class ServiceControl
{
    public const string ServiceName = "StutterDiag.Service";
    public const string DisplayName = "Windows Stutter Diagnostic";

    // The doc's shorthand is "NT SERVICE\StutterDiag"; the actual service-scoped virtual account
    // is name-derived from the service, hence ".Service". It is always a resolvable NT SERVICE SID.
    private const string VirtualAccount = @"NT SERVICE\StutterDiag.Service";

    private static readonly string[] Verbs = { "install", "uninstall", "start", "stop", "status" };

    public static bool IsVerb(string arg) => Array.Exists(Verbs, v => string.Equals(v, arg, StringComparison.OrdinalIgnoreCase));

    /// <summary>Exit codes: 0 ok, 1 usage / elevation declined, 3 operation failed.</summary>
    public static int Run(string[] args)
    {
        if (!OperatingSystem.IsWindows())
        {
            Console.Error.WriteLine("Service control verbs are only supported on Windows.");
            return 1;
        }

        string verb = args[0].ToLowerInvariant();

        bool needsElevation = verb is "install" or "uninstall" or "start" or "stop";
        if (needsElevation && !IsElevated())
        {
            Console.WriteLine($"'{verb}' needs administrator rights. Relaunching elevated (UAC prompt)...");
            return RelaunchElevated(args);
        }

        try
        {
            return verb switch
            {
                "install" => Install(),
                "uninstall" => Uninstall(),
                "start" => StartService(),
                "stop" => StopService(),
                "status" => ShowStatus(),
                _ => Usage(),
            };
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"{verb} failed: {ex.Message}");
            return 3;
        }
    }

    private static int Usage()
    {
        Console.WriteLine("usage: StutterDiag.Service <install|uninstall|start|stop|status>");
        return 1;
    }

    // ------------------------------------------------------------------

    [SupportedOSPlatform("windows")]
    private static int Install()
    {
        string exe = Environment.ProcessPath
                     ?? throw new InvalidOperationException("cannot resolve the service executable path");
        string binPath = $"\"{exe}\"";

        Console.WriteLine($"Installing '{ServiceName}' ({exe})");

        string account = VirtualAccount;
        // Quiet: a rejected virtual account is an expected outcome handled by the LocalSystem
        // fallback below, so it must not abort the caller.
        int rc = Sc($"create {ServiceName} binPath= {binPath} start= delayed-auto obj= \"{VirtualAccount}\" DisplayName= \"{DisplayName}\"",
                    quietOnFailure: true);
        if (rc == 0)
        {
            Console.WriteLine($"  created on virtual service account {VirtualAccount}");
        }
        else
        {
            Console.WriteLine("  virtual service account was rejected; retrying on LocalSystem");
            rc = Sc($"create {ServiceName} binPath= {binPath} start= delayed-auto obj= LocalSystem DisplayName= \"{DisplayName}\"");
            if (rc != 0)
            {
                Console.Error.WriteLine("  sc create failed");
                return 3;
            }
            account = "LocalSystem";
            Console.WriteLine("  created on LocalSystem");
        }

        Sc($"description {ServiceName} \"Detects micro-stutters and records what Windows was doing around them. Local only; no telemetry.\"");
        Sc($"failure {ServiceName} reset= 86400 actions= restart/60000/restart/60000/restart/120000");
        Sc($"failureflag {ServiceName} 1");

        if (account.StartsWith("NT SERVICE", StringComparison.OrdinalIgnoreCase))
        {
            GrantPrivileges(account);
            AddToLocalGroup("Performance Log Users", account);
            AddToLocalGroup("Event Log Readers", account);
            // Ask the SCM to keep only these privileges in the service token (least privilege).
            Sc($"privs {ServiceName} SeSystemProfilePrivilege/SeDebugPrivilege/SeChangeNotifyPrivilege");
        }
        else
        {
            Console.WriteLine("  LocalSystem already holds every required privilege; skipping LSA / group steps");
        }

        Console.WriteLine($"Done. Start with:  {ServiceName} start   (or: sc start {ServiceName})");
        return 0;
    }

    [SupportedOSPlatform("windows")]
    private static int Uninstall()
    {
        Console.WriteLine($"Removing '{ServiceName}'");

        // Install runs this first to clear any previous registration, so on a first install
        // nothing is there to remove. That is the normal case, not a failure: sc would exit
        // 1060 and its stderr would abort the caller.
        if (!ServiceExists())
        {
            Console.WriteLine("  not installed; nothing to remove");
            return 0;
        }

        Sc($"stop {ServiceName}", quietOnFailure: true);
        TryWaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(15));

        int rc = Sc($"delete {ServiceName}", quietOnFailure: true);
        if (rc != 0 && ServiceExists())
        {
            Console.Error.WriteLine("  sc delete failed (is the service still stopping? try again)");
            return 3;
        }

        Console.WriteLine("Done. (The service-scoped virtual account and any group memberships are removed with the service.)");
        return 0;
    }

    [SupportedOSPlatform("windows")]
    private static int StartService()
    {
        using var sc = new ServiceController(ServiceName);
        if (sc.Status == ServiceControllerStatus.Running)
        {
            Console.WriteLine($"'{ServiceName}' is already running.");
            return 0;
        }

        Console.WriteLine($"Starting '{ServiceName}'...");
        sc.Start();
        sc.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromSeconds(30));
        Console.WriteLine($"'{ServiceName}' is {sc.Status}.");
        return sc.Status == ServiceControllerStatus.Running ? 0 : 3;
    }

    [SupportedOSPlatform("windows")]
    private static int StopService()
    {
        using var sc = new ServiceController(ServiceName);
        if (sc.Status == ServiceControllerStatus.Stopped)
        {
            Console.WriteLine($"'{ServiceName}' is already stopped.");
            return 0;
        }

        Console.WriteLine($"Stopping '{ServiceName}'...");
        sc.Stop();
        sc.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(30));
        Console.WriteLine($"'{ServiceName}' is {sc.Status}.");
        return sc.Status == ServiceControllerStatus.Stopped ? 0 : 3;
    }

    [SupportedOSPlatform("windows")]
    private static int ShowStatus()
    {
        try
        {
            using var sc = new ServiceController(ServiceName);
            var status = sc.Status; // throws if not installed
            Console.WriteLine($"{ServiceName}: {status} (startType={sc.StartType})");
            return 0;
        }
        catch (InvalidOperationException)
        {
            Console.WriteLine($"{ServiceName}: not installed");
            return 3;
        }
    }

    // ------------------------------------------------------------------

    [SupportedOSPlatform("windows")]
    private static void GrantPrivileges(string account)
    {
        try
        {
            var sid = (SecurityIdentifier)new NTAccount(account).Translate(typeof(SecurityIdentifier));
            LsaPrivileges.Grant(sid, "SeSystemProfilePrivilege", "SeDebugPrivilege", "SeServiceLogonRight");
            Console.WriteLine($"  granted SeSystemProfilePrivilege + SeDebugPrivilege + SeServiceLogonRight to {account}");
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"  could not grant privileges via LSA ({ex.Message}).");
            Console.Error.WriteLine($"  fallback: ntrights.exe -u \"{account}\" +r SeSystemProfilePrivilege  (repeat for SeDebugPrivilege)");
        }
    }

    [SupportedOSPlatform("windows")]
    private static void AddToLocalGroup(string group, string account)
    {
        int rc = Run("net.exe", $"localgroup \"{group}\" \"{account}\" /add");
        if (rc == 0)
            Console.WriteLine($"  added {account} to \"{group}\"");
        else
            Console.Error.WriteLine(
                $"  could not add {account} to \"{group}\" (group may not exist on this SKU, or it is already a member). " +
                $"fallback: net localgroup \"{group}\" \"{account}\" /add");
    }

    [SupportedOSPlatform("windows")]
    private static bool ServiceExists()
    {
        try
        {
            using var sc = new ServiceController(ServiceName);
            _ = sc.Status; // throws if not installed
            return true;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    [SupportedOSPlatform("windows")]
    private static void TryWaitForStatus(ServiceControllerStatus status, TimeSpan timeout)
    {
        try
        {
            using var sc = new ServiceController(ServiceName);
            sc.WaitForStatus(status, timeout);
        }
        catch
        {
            // not installed / already gone / timed out — the caller reports the real outcome
        }
    }

    // ------------------------------------------------------------------

    private static int Sc(string arguments, bool quietOnFailure = false) => Run("sc.exe", arguments, quietOnFailure);

    /// <param name="quietOnFailure">
    /// Do not echo the child's stderr when it fails. Set this whenever the caller handles the
    /// failure itself: callers run us under PowerShell with <c>$ErrorActionPreference = 'Stop'</c>,
    /// where any stderr write by a native command becomes a terminating error, so echoing an
    /// expected failure would abort the caller before it can recover.
    /// </param>
    private static int Run(string fileName, string arguments, bool quietOnFailure = false)
    {
        var psi = new ProcessStartInfo(fileName, arguments)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };

        using var p = Process.Start(psi) ?? throw new InvalidOperationException($"could not start {fileName}");
        string stdout = p.StandardOutput.ReadToEnd();
        string stderr = p.StandardError.ReadToEnd();
        p.WaitForExit();

        if (!string.IsNullOrWhiteSpace(stdout)) Console.WriteLine(Indent(stdout));
        if (p.ExitCode != 0 && !quietOnFailure && !string.IsNullOrWhiteSpace(stderr)) Console.Error.WriteLine(Indent(stderr));
        return p.ExitCode;
    }

    private static string Indent(string text) => "    " + text.Trim().ReplaceLineEndings("\n    ");

    [SupportedOSPlatform("windows")]
    private static bool IsElevated()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }

    private static int RelaunchElevated(string[] args)
    {
        var psi = new ProcessStartInfo
        {
            FileName = Environment.ProcessPath!,
            UseShellExecute = true,
            Verb = "runas",
            Arguments = string.Join(' ', Array.ConvertAll(args, a => a.Contains(' ') ? $"\"{a}\"" : a)),
        };

        try
        {
            using var p = Process.Start(psi);
            if (p is null) return 1;
            p.WaitForExit();
            return p.ExitCode;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Elevation was declined or failed: {ex.Message}");
            return 1;
        }
    }
}
