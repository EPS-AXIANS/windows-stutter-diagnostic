using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using StutterDiag.Core.Abstractions;
using StutterDiag.Core.Model;
using StutterDiag.Core.Time;
using StutterDiag.Monitors.Common;

namespace StutterDiag.Monitors.Cpu;

/// <summary>
/// Power-state, P-state, C-state and thermal-throttle transitions.
/// <para>
/// Two sources feed it: (1) an injected <see cref="IEtwEventFeed"/> carrying
/// <c>Microsoft-Windows-Kernel-Power</c> and <c>Microsoft-Windows-Kernel-Processor-Power</c>
/// events (approximate residency — MSRs are unreadable from user mode, ARCHITECTURE §12);
/// (2) polling of <c>GetSystemPowerStatus</c> and the active power scheme for AC/DC and
/// power-plan changes. <see cref="RegisterForPowerSettingNotifications"/> is offered for a
/// host that has a message pump / service handle, but polling is the always-available path.
/// </para>
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class CpuPowerStateMonitor : MonitorBase, IPowerMonitor
{
    private readonly IEtwEventFeed? _etw;
    private readonly TimeSpan _pollPeriod;

    private Timer? _pollTimer;
    private byte _lastAcLine = 0xFF;
    private byte _lastBatterySaver = 0xFF;
    private Guid _lastScheme = Guid.Empty;
    private readonly List<IntPtr> _notificationHandles = new();

    public CpuPowerStateMonitor(IEtwEventFeed? etwFeed, QpcClock clock, TimeSpan? pollPeriod = null)
        : base("Cpu.PowerState", clock)
    {
        _etw = etwFeed;
        _pollPeriod = pollPeriod is { } p && p > TimeSpan.Zero ? p : TimeSpan.FromSeconds(2);
    }

    public override Task StartAsync(CancellationToken ct)
    {
        if (!OperatingSystem.IsWindows())
        {
            SetHealth(MonitorHealth.Unavailable("power-state monitor requires Windows"));
            return Task.CompletedTask;
        }

        try
        {
            if (_etw is not null)
            {
                _etw.EventReceived += OnEtwEvent;
                _etw.Subscribe(EtwFeedTopic.KernelPower);
                _etw.Subscribe(EtwFeedTopic.KernelProcessorPower);
            }

            _pollTimer = new Timer(_ => Poll(), null, TimeSpan.Zero, _pollPeriod);

            SetHealth(_etw is { IsLive: true }
                ? MonitorHealth.Ok
                : MonitorHealth.Degraded(
                    "no live ETW feed: P-state/C-state transitions are not observed; only AC/DC and power-plan changes are polled"));
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

        _pollTimer?.Dispose();
        _pollTimer = null;

        foreach (var h in _notificationHandles)
        {
            try { NativeMethods.UnregisterPowerSettingNotification(h); } catch { /* ignore */ }
        }
        _notificationHandles.Clear();
        return Task.CompletedTask;
    }

    /// <summary>
    /// Optional: register for <c>WM_POWERBROADCAST</c> / <c>PBT_POWERSETTINGCHANGE</c> delivery.
    /// The caller must pump those messages (a window) or be a service passing its status handle
    /// with <paramref name="serviceHandle"/> = true; otherwise nothing is delivered and polling
    /// remains the effective mechanism.
    /// </summary>
    public void RegisterForPowerSettingNotifications(IntPtr recipient, bool serviceHandle = false)
    {
        if (!OperatingSystem.IsWindows() || recipient == IntPtr.Zero) return;
        int flags = serviceHandle ? NativeMethods.DEVICE_NOTIFY_SERVICE_HANDLE : NativeMethods.DEVICE_NOTIFY_WINDOW_HANDLE;
        foreach (var guid in new[]
        {
            NativeMethods.GUID_ACDC_POWER_SOURCE,
            NativeMethods.GUID_POWERSCHEME_PERSONALITY,
            NativeMethods.GUID_MONITOR_POWER_ON
        })
        {
            var g = guid;
            IntPtr h = NativeMethods.RegisterPowerSettingNotification(recipient, ref g, flags);
            if (h != IntPtr.Zero) _notificationHandles.Add(h);
        }
    }

    // ----------------------------------------------------------------- polling

    private void Poll()
    {
        try
        {
            if (NativeMethods.GetSystemPowerStatus(out var s))
            {
                if (_lastAcLine != 0xFF && s.ACLineStatus != _lastAcLine)
                {
                    RaiseEvent(NewEvent(EventCategory.PowerState,
                        s.ACLineStatus switch
                        {
                            1 => "Power source changed to AC (mains).",
                            0 => "Power source changed to DC (battery).",
                            _ => "Power source changed to unknown."
                        },
                        EventSeverity.Info,
                        data: new Dictionary<string, string>
                        {
                            ["acLineStatus"] = s.ACLineStatus.ToString(),
                            ["batteryLifePercent"] = s.BatteryLifePercent == 0xFF ? "unknown" : s.BatteryLifePercent.ToString()
                        }));
                }
                _lastAcLine = s.ACLineStatus;

                byte saver = (byte)(s.SystemStatusFlag & 0x1);
                if (_lastBatterySaver != 0xFF && saver != _lastBatterySaver)
                {
                    RaiseEvent(NewEvent(EventCategory.PowerState,
                        saver == 1 ? "Battery saver turned on." : "Battery saver turned off.",
                        EventSeverity.Info));
                }
                _lastBatterySaver = saver;
            }

            PollActiveScheme();
        }
        catch
        {
            // Transient WMI/PDH-independent Win32 failure; next tick retries.
        }
    }

    private void PollActiveScheme()
    {
        IntPtr guidPtr = IntPtr.Zero;
        try
        {
            if (NativeMethods.PowerGetActiveScheme(IntPtr.Zero, out guidPtr) != 0 || guidPtr == IntPtr.Zero)
                return;

            Guid scheme = Marshal.PtrToStructure<Guid>(guidPtr);
            if (scheme == _lastScheme) return;

            string name = ReadSchemeFriendlyName(guidPtr) ?? scheme.ToString();
            if (_lastScheme != Guid.Empty)
            {
                RaiseEvent(NewEvent(EventCategory.PowerState,
                    $"Active power plan changed to '{name}'.",
                    EventSeverity.Info,
                    data: new Dictionary<string, string> { ["schemeGuid"] = scheme.ToString(), ["schemeName"] = name }));
            }
            _lastScheme = scheme;
        }
        finally
        {
            if (guidPtr != IntPtr.Zero) NativeMethods.LocalFree(guidPtr);
        }
    }

    private static string? ReadSchemeFriendlyName(IntPtr schemeGuidPtr)
    {
        try
        {
            uint size = 0;
            NativeMethods.PowerReadFriendlyName(IntPtr.Zero, schemeGuidPtr, IntPtr.Zero, IntPtr.Zero, null, ref size);
            if (size is 0 or > 4096) return null;
            var buffer = new byte[size];
            if (NativeMethods.PowerReadFriendlyName(IntPtr.Zero, schemeGuidPtr, IntPtr.Zero, IntPtr.Zero, buffer, ref size) != 0)
                return null;
            return System.Text.Encoding.Unicode.GetString(buffer).TrimEnd('\0');
        }
        catch
        {
            return null;
        }
    }

    // ----------------------------------------------------------------- ETW

    private void OnEtwEvent(object? sender, EtwFeedEvent e)
    {
        if (e.Topic is not (EtwFeedTopic.KernelPower or EtwFeedTopic.KernelProcessorPower)) return;

        var (category, message) = Classify(e);
        var data = new Dictionary<string, string>(e.Payload)
        {
            ["provider"] = e.ProviderName,
            ["etwEventId"] = e.EventId.ToString(),
            ["task"] = e.TaskName,
            ["opcode"] = e.OpcodeName
        };

        RaiseEvent(new MonitorEvent
        {
            TimestampQpc = e.TimestampQpc,
            TimestampUtc = e.TimestampUtc,
            Category = category,
            Source = Name,
            Provider = e.ProviderName,
            EventId = e.EventId,
            Severity = category == EventCategory.ThermalThrottle ? EventSeverity.Warning : EventSeverity.Info,
            Message = message,
            Data = data
        });
    }

    // Manifest task/opcode names differ across Windows builds; match on keywords and keep the
    // raw ids in Data so the exact mapping can be verified at build time.
    private static (EventCategory Category, string Message) Classify(EtwFeedEvent e)
    {
        string hay = $"{e.TaskName} {e.OpcodeName} {e.FormattedMessage}".ToLowerInvariant();

        if (hay.Contains("thermal") || hay.Contains("throttl"))
            return (EventCategory.ThermalThrottle, Describe("Thermal throttle / performance cap", e));

        if (e.Topic == EtwFeedTopic.KernelProcessorPower)
        {
            if (hay.Contains("idle") || hay.Contains("cstate") || hay.Contains("c-state"))
                return (EventCategory.CState, Describe("Processor idle-state (C-state) transition", e));
            if (hay.Contains("perf") || hay.Contains("pstate") || hay.Contains("p-state") || hay.Contains("frequency"))
                return (EventCategory.PState, Describe("Processor performance-state (P-state) transition", e));
            return (EventCategory.PState, Describe("Processor-power event", e));
        }

        // Kernel-Power
        if (hay.Contains("sleep") || hay.Contains("suspend") || hay.Contains("resume") || hay.Contains("wake"))
            return (EventCategory.PowerState, Describe("System sleep/resume transition", e));
        if (hay.Contains("monitor") || hay.Contains("display"))
            return (EventCategory.PowerState, Describe("Display power transition", e));
        return (EventCategory.KernelPower, Describe("Kernel-Power event", e));
    }

    private static string Describe(string what, EtwFeedEvent e)
        => $"{what} ({e.ProviderName} id {e.EventId}{(string.IsNullOrEmpty(e.OpcodeName) ? "" : $", {e.OpcodeName}")}).";

    public override async ValueTask DisposeAsync()
    {
        await StopAsync(CancellationToken.None).ConfigureAwait(false);
        await base.DisposeAsync().ConfigureAwait(false);
    }
}
