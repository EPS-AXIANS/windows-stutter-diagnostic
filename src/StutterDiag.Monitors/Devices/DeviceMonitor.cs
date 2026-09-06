using System.Diagnostics.Eventing.Reader;
using System.Runtime.Versioning;
using StutterDiag.Core.Abstractions;
using StutterDiag.Core.Model;
using StutterDiag.Core.Time;
using StutterDiag.Monitors.Common;

namespace StutterDiag.Monitors.Devices;

/// <summary>
/// Device-level disturbances: PnP arrival/removal/start-failure, UMDF device lifecycle, xHCI
/// controller/device resets, and audio/network device state changes. Prefers an injected
/// <see cref="IEtwEventFeed"/> (Kernel-PnP / DriverFrameworks / USB-USBXHCI / Audio) and falls
/// back to event-log push subscriptions. Emits Device / Pnp / Usb / Pcie / Audio / Network.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class DeviceMonitor : MonitorBase, IDeviceMonitor
{
    private const string WarnPlus = "*[System[(Level=1 or Level=2 or Level=3)]]";

    private readonly IEtwEventFeed? _etw;
    private readonly List<EventLogSubscription> _subs = new();

    public DeviceMonitor(QpcClock clock, IEtwEventFeed? etwFeed = null) : base("Device", clock)
    {
        _etw = etwFeed;
    }

    public override Task StartAsync(CancellationToken ct)
    {
        if (!OperatingSystem.IsWindows())
        {
            SetHealth(MonitorHealth.Unavailable("device monitor requires Windows"));
            return Task.CompletedTask;
        }

        try
        {
            if (_etw is not null)
            {
                _etw.EventReceived += OnEtwEvent;
                _etw.Subscribe(EtwFeedTopic.KernelPnp);
                _etw.Subscribe(EtwFeedTopic.DriverFrameworks);
                _etw.Subscribe(EtwFeedTopic.UsbXhci);
                _etw.Subscribe(EtwFeedTopic.Audio);
            }

            TrySubscribe("Microsoft-Windows-Kernel-PnP/Configuration", null);
            TrySubscribe("Microsoft-Windows-DriverFrameworks-UserMode/Operational", WarnPlus);
            TrySubscribe("System",
                "*[System[Provider[" +
                "@Name='Microsoft-Windows-Kernel-PnP' or " +
                "@Name='Microsoft-Windows-USB-USBXHCI' or " +
                "@Name='Microsoft-Windows-USB-USBHUB3' or " +
                "@Name='USBXHCI' or @Name='USBHUB3' or " +
                "@Name='Microsoft-Windows-Audio' or " +
                "@Name='Microsoft-Windows-Ndis' or @Name='e1dexpress' or @Name='nvlddmkm' or " +
                "@Name='ACPI' or @Name='pci']]]");

            int active = _subs.Count(s => s.Active);
            if (active == 0 && _etw is null)
                SetHealth(MonitorHealth.Degraded("no device event source subscribed"));
            else if (active < _subs.Count || _etw is null)
                SetHealth(MonitorHealth.Degraded(
                    _etw is null
                        ? "no ETW feed: relying on event-log device notifications only (coarser, delayed)"
                        : $"{active}/{_subs.Count} device channels active"));
            else
                SetHealth(MonitorHealth.Ok);
        }
        catch (Exception ex)
        {
            SetHealth(MonitorHealth.Failed(ex.Message));
        }
        return Task.CompletedTask;
    }

    public override Task StopAsync(CancellationToken ct)
    {
        if (_etw is not null) _etw.EventReceived -= OnEtwEvent;
        foreach (var s in _subs) s.Dispose();
        _subs.Clear();
        return Task.CompletedTask;
    }

    private void TrySubscribe(string channel, string? xpath)
    {
        var sub = new EventLogSubscription(channel, xpath, rec =>
        {
            var category = Classify(SafeProvider(rec), SafeMessage(rec));
            RaiseEvent(EventRecordConverter.ToMonitorEvent(rec, Clock, Name, category));
        });
        sub.TryStart();
        _subs.Add(sub);
    }

    private void OnEtwEvent(object? sender, EtwFeedEvent e)
    {
        EventCategory category = e.Topic switch
        {
            EtwFeedTopic.UsbXhci => EventCategory.Usb,
            EtwFeedTopic.Audio => EventCategory.Audio,
            EtwFeedTopic.DriverFrameworks => EventCategory.Device,
            _ => Classify(e.ProviderName, e.FormattedMessage ?? "")
        };

        RaiseEvent(new MonitorEvent
        {
            TimestampQpc = e.TimestampQpc,
            TimestampUtc = e.TimestampUtc,
            Category = category,
            Source = Name,
            Provider = e.ProviderName,
            EventId = e.EventId,
            Severity = e.Level <= 2 ? EventSeverity.Error : e.Level == 3 ? EventSeverity.Warning : EventSeverity.Info,
            Message = e.FormattedMessage ?? $"{e.ProviderName} id {e.EventId} ({e.TaskName} {e.OpcodeName}).".Trim(),
            Data = new Dictionary<string, string>(e.Payload) { ["provider"] = e.ProviderName, ["topic"] = e.Topic.ToString() }
        });
    }

    private static EventCategory Classify(string provider, string message)
    {
        string p = provider.ToLowerInvariant();
        string m = message.ToLowerInvariant();

        if (p.Contains("usb") || m.Contains("usb")) return EventCategory.Usb;
        if (p.Contains("audio") || m.Contains("audio")) return EventCategory.Audio;
        if (p.Contains("ndis") || p.Contains("netvsc") || p.Contains("e1d") || p.Contains("network") || m.Contains("network adapter"))
            return EventCategory.Network;
        if (p == "pci" || m.Contains("pci express") || m.Contains("pcie")) return EventCategory.Pcie;
        if (p.Contains("pnp") || m.Contains("device") && (m.Contains("start") || m.Contains("reset") || m.Contains("disconnect") || m.Contains("removed")))
            return EventCategory.Pnp;
        if (p.Contains("driverframeworks")) return EventCategory.Device;
        return EventCategory.Device;
    }

    private static string SafeProvider(EventRecord r) { try { return r.ProviderName ?? ""; } catch { return ""; } }
    private static string SafeMessage(EventRecord r) { try { return r.FormatDescription() ?? ""; } catch { return ""; } }

    public override async ValueTask DisposeAsync()
    {
        await StopAsync(CancellationToken.None).ConfigureAwait(false);
        await base.DisposeAsync().ConfigureAwait(false);
    }
}
