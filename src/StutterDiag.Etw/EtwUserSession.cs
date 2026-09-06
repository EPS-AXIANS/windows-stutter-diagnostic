using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Session;
using StutterDiag.Core.Config;
using StutterDiag.Core.Model;

namespace StutterDiag.Etw;

/// <summary>
/// Owns the real-time user-mode session (<c>StutterDiag-User</c>). Enables only the manifest
/// providers that <see cref="ProviderDiscovery"/> reports as registered, each with a
/// deliberately small keyword mask so the session stays cheap. Consumers attach their own
/// callbacks to <c>Session.Source.Dynamic</c> before <see cref="Start"/>.
/// </summary>
/// <remarks>Real-time ETW consumption requires administrator; if the session cannot be created
/// <see cref="Health"/> becomes <see cref="HealthStatus.Unavailable"/> and construction does not throw.</remarks>
public sealed class EtwUserSession : IDisposable
{
    public const string SessionName = "StutterDiag-User";

    // Well-known keyword masks. Values are provider-specific bit flags from each manifest.
    // NOTE(build): the DxgKrnl / StorPort / Kernel-Memory keyword values below are the ones
    //              PresentMon / WPA commonly use, but they MUST be checked against the
    //              installed manifests (wevtutil gp <provider>) before shipping.
    public const ulong DxgKrnlKeywords = 0x1;                // "Base": Present/Flip/QueuePacket/DmaPacket
    public const ulong DwmCoreKeywords = 0xFFFFFFFFFFFFFFFF;  // DWM-Core is low volume; take all
    public const ulong StorPortKeywords = 0xFFFFFFFFFFFFFFFF;  // port-driver I/O timing
    public const ulong KernelMemoryKeywords = 0x40;           // KERNEL_MEM_KEYWORD_HARD_FAULT-ish

    /// <summary>Providers we would like, in priority order. Each is enabled only if present.</summary>
    public static readonly IReadOnlyList<ProviderRequest> Wanted = new[]
    {
        new ProviderRequest("Microsoft-Windows-WHEA-Logger",              TraceEventLevel.Verbose,       ulong.MaxValue),
        new ProviderRequest("Microsoft-Windows-Kernel-Power",             TraceEventLevel.Informational, ulong.MaxValue),
        new ProviderRequest("Microsoft-Windows-Kernel-Processor-Power",   TraceEventLevel.Informational, ulong.MaxValue),
        new ProviderRequest("Microsoft-Windows-Kernel-PnP",              TraceEventLevel.Informational, ulong.MaxValue),
        new ProviderRequest("Microsoft-Windows-Kernel-Memory",           TraceEventLevel.Informational, KernelMemoryKeywords),
        new ProviderRequest("Microsoft-Windows-DxgKrnl",                 TraceEventLevel.Informational, DxgKrnlKeywords),
        new ProviderRequest("Microsoft-Windows-Dwm-Core",               TraceEventLevel.Informational, DwmCoreKeywords),
        new ProviderRequest("Microsoft-Windows-StorPort",               TraceEventLevel.Informational, StorPortKeywords),
        new ProviderRequest("Microsoft-Windows-TPM-WMI",                TraceEventLevel.Verbose,       ulong.MaxValue),
        new ProviderRequest("Microsoft-Windows-DeviceGuard",            TraceEventLevel.Informational, ulong.MaxValue),
        new ProviderRequest("Microsoft-Windows-DriverFrameworks-UserMode", TraceEventLevel.Warning,    ulong.MaxValue),
        new ProviderRequest("Microsoft-Windows-USB-USBXHCI",            TraceEventLevel.Warning,       ulong.MaxValue),
        new ProviderRequest("Microsoft-Windows-Audio",                  TraceEventLevel.Warning,       ulong.MaxValue),
        new ProviderRequest("Microsoft-Windows-Kernel-EventTracing",    TraceEventLevel.Informational, ulong.MaxValue),
    };

    private readonly ProviderDiscovery _discovery;
    private readonly List<string> _enabled = new();
    private readonly List<string> _missing = new();
    private TraceEventSession? _session;

    public EtwUserSession(EtwOptions options, ProviderDiscovery discovery)
    {
        _discovery = discovery;
        try
        {
            if (TraceEventSession.IsElevated() != true)
            {
                Health = MonitorHealth.Unavailable("user-mode realtime ETW requires administrator");
                return;
            }

            _session = new TraceEventSession(SessionName)
            {
                StopOnDispose = true,
                BufferSizeMB = Math.Max(4, (int)Math.Ceiling(options.BufferSizeKb * (double)options.BufferCount / 1024.0)),
            };
        }
        catch (Exception ex)
        {
            _session = null;
            LastError = ex;
            Health = MonitorHealth.Unavailable("user-mode realtime ETW requires administrator");
        }
    }

    public MonitorHealth Health { get; private set; } = MonitorHealth.Ok;
    public Exception? LastError { get; private set; }
    public bool IsAvailable => _session is not null;
    public TraceEventSession? Session => _session;

    /// <summary>Providers actually enabled on the session.</summary>
    public IReadOnlyList<string> EnabledProviders => _enabled;

    /// <summary>Requested providers that were not registered on this machine (for the report).</summary>
    public IReadOnlyList<string> MissingProviders => _missing;

    public event EventHandler<MonitorHealth>? HealthChanged;

    /// <summary>Enable every discovered provider from <see cref="Wanted"/>. Returns false when unavailable.</summary>
    public bool Start()
    {
        if (_session is null) return false;

        foreach (var req in Wanted)
        {
            if (!_discovery.IsAvailable(req.Name))
            {
                _missing.Add(req.Name);
                continue;
            }
            try
            {
                // EnableProvider(string, TraceEventLevel, ulong matchAnyKeywords, options)
                _session.EnableProvider(req.Name, req.Level, req.MatchAnyKeywords);
                _enabled.Add(req.Name);
            }
            catch (Exception ex)
            {
                LastError = ex;
                _missing.Add(req.Name);
            }
        }

        var h = _enabled.Count == 0
            ? MonitorHealth.Degraded("no user-mode providers could be enabled")
            : MonitorHealth.Ok;
        SetHealth(h);
        return _enabled.Count > 0;
    }

    /// <summary>True if a named provider ended up enabled (used by DxgKrnl/DWM-dependent monitors).</summary>
    public bool IsEnabled(string providerName) => _enabled.Contains(providerName);

    public void Stop()
    {
        try { _session?.Stop(); } catch { /* gone */ }
    }

    public void Dispose()
    {
        try { _session?.Dispose(); } catch { /* ignore */ }
        _session = null;
    }

    private void SetHealth(MonitorHealth h)
    {
        if (h == Health) return;
        Health = h;
        HealthChanged?.Invoke(this, h);
    }

    /// <summary>A provider we want on the user session, with its level and keyword mask.</summary>
    public readonly record struct ProviderRequest(string Name, TraceEventLevel Level, ulong MatchAnyKeywords);
}
