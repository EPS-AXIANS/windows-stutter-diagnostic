using System.Collections.Concurrent;
using System.Diagnostics.Eventing.Reader;
using System.Runtime.Versioning;
using StutterDiag.Core.Abstractions;
using StutterDiag.Core.Config;
using StutterDiag.Core.Model;
using StutterDiag.Core.Time;
using StutterDiag.Monitors.Common;

namespace StutterDiag.Monitors.EventLog;

/// <summary>
/// Push subscriptions (never polling) to the Windows event-log channels relevant to stutter
/// diagnosis. Channel presence is discovered at start via
/// <see cref="EventLogSession.GetLogNames"/>; present channels go to <see cref="ActiveChannels"/>,
/// the rest to <see cref="UnavailableChannels"/>. Each record is stored with QPC + UTC
/// timestamps, provider, id, level, rendered message, raw XML and structured EventData.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class EventLogMonitor : MonitorBase, IEventLogMonitor
{
    // Broad channels: filter to Warning and worse. Operational channels are low volume: take all.
    private const string WarnPlus = "*[System[(Level=1 or Level=2 or Level=3)]]";

    private static readonly Candidate[] Candidates =
    {
        new("System",       WarnPlus, EventCategory.EventLog),
        new("Application",  WarnPlus, EventCategory.EventLog),
        new("Security",     WarnPlus, EventCategory.EventLog),

        new("Microsoft-Windows-Kernel-PnP/Configuration",              null, EventCategory.Pnp),
        new("Microsoft-Windows-Kernel-Power/Thermal-Operational",      null, EventCategory.ThermalThrottle),
        new("Microsoft-Windows-Kernel-WHEA/Errors",                    null, EventCategory.Whea),
        new("Microsoft-Windows-Kernel-WHEA/Operational",              WarnPlus, EventCategory.Whea),
        new("Microsoft-Windows-UserPnp/DeviceInstall",               WarnPlus, EventCategory.Pnp),
        new("Microsoft-Windows-DriverFrameworks-UserMode/Operational", WarnPlus, EventCategory.Device),
        new("Microsoft-Windows-DeviceSetupManager/Operational",       WarnPlus, EventCategory.Pnp),
        new("Microsoft-Windows-DeviceSetupManager/Admin",             null, EventCategory.Pnp),
        new("Microsoft-Windows-Ntfs/Operational",                    WarnPlus, EventCategory.Disk),
        new("Microsoft-Windows-Storage-Storport/Operational",         null, EventCategory.Disk),
        new("Microsoft-Windows-StorageSpaces-Driver/Operational",    WarnPlus, EventCategory.Disk),
        new("Microsoft-Windows-Diagnostics-Performance/Operational", WarnPlus, EventCategory.Other),
        new("Microsoft-Windows-DxgKrnl-Operational",                 WarnPlus, EventCategory.GpuDriver),
        new("Microsoft-Windows-TPM-WMI",                              null, EventCategory.Tpm),
        new("Microsoft-Windows-WLAN-AutoConfig/Operational",         WarnPlus, EventCategory.Network),
        new("Microsoft-Windows-NDIS/Operational",                    WarnPlus, EventCategory.Network),
        new("Microsoft-Windows-Kernel-Power/Diagnostic",             null, EventCategory.KernelPower),
    };

    private readonly List<EventLogSubscription> _subs = new();
    private readonly ConcurrentBag<string> _active = new();
    private readonly ConcurrentBag<string> _unavailable = new();

    public EventLogMonitor(AppConfig config, QpcClock clock) : base("EventLog", clock)
    {
        _ = config; // reserved (retention/volume knobs); channel set is fixed for now
    }

    public IReadOnlyCollection<string> ActiveChannels => _active.ToArray();
    public IReadOnlyCollection<string> UnavailableChannels => _unavailable.ToArray();

    public override Task StartAsync(CancellationToken ct)
    {
        if (!OperatingSystem.IsWindows())
        {
            SetHealth(MonitorHealth.Unavailable("event-log monitor requires Windows"));
            return Task.CompletedTask;
        }

        HashSet<string> present;
        try
        {
            present = new HashSet<string>(EventLogSession.GlobalSession.GetLogNames(), StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception ex)
        {
            SetHealth(MonitorHealth.Failed($"could not enumerate event-log channels: {ex.Message}"));
            return Task.CompletedTask;
        }

        foreach (var c in Candidates)
        {
            if (!present.Contains(c.Channel))
            {
                _unavailable.Add(c.Channel);
                continue;
            }

            var sub = new EventLogSubscription(c.Channel, c.Xpath, rec => OnRecord(rec, c));
            if (sub.TryStart())
            {
                _subs.Add(sub);
                _active.Add(c.Channel);
            }
            else
            {
                _subs.Add(sub); // keep for disposal symmetry
                _unavailable.Add($"{c.Channel} ({sub.Error})");
            }
        }

        if (_active.IsEmpty)
            SetHealth(MonitorHealth.Failed("no event-log channels could be subscribed"));
        else if (!_unavailable.IsEmpty)
            SetHealth(MonitorHealth.Degraded($"{_active.Count} channels active, {_unavailable.Count} unavailable"));
        else
            SetHealth(MonitorHealth.Ok);

        return Task.CompletedTask;
    }

    public override Task StopAsync(CancellationToken ct)
    {
        foreach (var s in _subs) s.Dispose();
        _subs.Clear();
        return Task.CompletedTask;
    }

    private void OnRecord(EventRecord rec, Candidate c)
    {
        EventCategory category = Classify(SafeProvider(rec), c.Channel, c.DefaultCategory);
        RaiseEvent(EventRecordConverter.ToMonitorEvent(rec, Clock, Name, category)
            .WithData("channel", c.Channel));
    }

    private static string SafeProvider(EventRecord rec)
    {
        try { return rec.ProviderName ?? ""; }
        catch { return ""; }
    }

    /// <summary>Best-effort provider/channel -&gt; category. Keeps the fixed default when nothing matches.</summary>
    private static EventCategory Classify(string provider, string channel, EventCategory fallback)
    {
        string p = provider.ToLowerInvariant();
        string ch = channel.ToLowerInvariant();

        if (p.Contains("whea") || ch.Contains("whea")) return EventCategory.Whea;
        if (p == "tbs") return EventCategory.Tbs;
        if (p.Contains("tpm")) return EventCategory.Tpm;
        if (p.Contains("kernel-power") || p.Contains("kernel-processor-power")) return EventCategory.KernelPower;
        if (p.Contains("thermal")) return EventCategory.ThermalThrottle;
        if (p.Contains("dxgkrnl") || p.Contains("display") || p.Contains("nvlddmkm") || p.Contains("amdkmdag") || p.Contains("igfx"))
            return EventCategory.GpuDriver;
        if (p.Contains("kernel-pnp") || p.Contains("userpnp") || p.Contains("devicesetupmanager") || p.Contains("pnp"))
            return EventCategory.Pnp;
        if (p.Contains("driverframeworks") || p.Contains("usb")) return EventCategory.Device;
        if (p.Contains("ntfs") || p.Contains("storport") || p.Contains("storage") || p.Contains("disk"))
            return EventCategory.Disk;
        if (p.Contains("ndis") || p.Contains("wlan") || p.Contains("tcpip") || p.Contains("dhcp"))
            return EventCategory.Network;
        if (p.Contains("audio") || p.Contains("audiosrv")) return EventCategory.Audio;
        if (p.Contains("acpi")) return EventCategory.Acpi;
        if (p.Contains("bugcheck") || p.Contains("kernel-general")) return EventCategory.HardwareError;

        return fallback;
    }

    public override async ValueTask DisposeAsync()
    {
        await StopAsync(CancellationToken.None).ConfigureAwait(false);
        await base.DisposeAsync().ConfigureAwait(false);
    }

    private readonly record struct Candidate(string Channel, string? Xpath, EventCategory DefaultCategory);
}
