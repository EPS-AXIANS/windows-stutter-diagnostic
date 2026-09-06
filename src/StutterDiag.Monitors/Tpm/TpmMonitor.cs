using System.Diagnostics.Eventing.Reader;
using System.Management;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using StutterDiag.Core.Abstractions;
using StutterDiag.Core.Model;
using StutterDiag.Core.Time;
using StutterDiag.Core.Tpm;
using StutterDiag.Monitors.Common;

namespace StutterDiag.Monitors.Tpm;

/// <summary>
/// Read-only TPM description and TPM/TBS event stream. Merges CIM <c>Win32_Tpm</c>, the
/// <c>Tbsi_GetDeviceInfo</c> TBS call and a PnP probe for a discrete part, then runs
/// <see cref="TpmInferenceHeuristic"/> for the firmware-vs-discrete guess. Fields that cannot
/// be read stay null. No TPM state-changing API is ever called (no method invokes on
/// <c>Win32_Tpm</c>, no TBS context is opened).
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class TpmMonitor : MonitorBase, ITpmMonitor
{
    private readonly IEtwEventFeed? _etw;
    private readonly List<EventLogSubscription> _subs = new();
    private TpmInfo? _cached;

    public TpmMonitor(QpcClock clock, IEtwEventFeed? etwFeed = null) : base("Tpm", clock)
    {
        _etw = etwFeed;
    }

    // ------------------------------------------------------------------ snapshot

    public TpmInfo GetTpmInfo()
    {
        if (_cached is not null) return _cached;
        _cached = Build();
        return _cached;
    }

    private TpmInfo Build()
    {
        if (!OperatingSystem.IsWindows())
            return TpmInfo.NotDetected("Not running on Windows; TPM cannot be queried.");

        var notes = new List<string>();

        // ---- Win32_Tpm (root\CIMV2\Security\MicrosoftTpm) ---------------------
        bool wmiPresent = false;
        string? specVersion = null, mfrIdRaw = null, mfrName = null, mfrVersion = null, ppVersion = null;
        bool? isEnabled = null, isActivated = null, isOwned = null;

        try
        {
            using var searcher = new ManagementObjectSearcher(
                new ManagementScope(@"\\.\root\CIMV2\Security\MicrosoftTpm"),
                new ObjectQuery("SELECT * FROM Win32_Tpm"));

            foreach (ManagementBaseObject mo in searcher.Get())
            {
                using (mo)
                {
                    wmiPresent = true;
                    specVersion = AsString(mo, "SpecVersion");
                    ppVersion = AsString(mo, "PhysicalPresenceVersionInfo");
                    mfrVersion = AsString(mo, "ManufacturerVersion");

                    mfrIdRaw = AsString(mo, "ManufacturerIdTxt");
                    uint? mfrIdNum = AsUInt(mo, "ManufacturerId");
                    if (string.IsNullOrWhiteSpace(mfrIdRaw) && mfrIdNum is { } v)
                        mfrIdRaw = DecodeManufacturerId(v);
                    mfrName = DecodeManufacturerName(mfrIdRaw);

                    isEnabled = AsBool(mo, "IsEnabled_InitialValue");
                    isActivated = AsBool(mo, "IsActivated_InitialValue");
                    isOwned = AsBool(mo, "IsOwned_InitialValue");
                }
                break; // exactly one instance expected
            }

            if (!wmiPresent) notes.Add("Win32_Tpm returned no instance.");
        }
        catch (ManagementException ex)
        {
            notes.Add($"Win32_Tpm query failed ({ex.GetType().Name}: {ex.Message}).");
        }
        catch (Exception ex)
        {
            notes.Add($"Win32_Tpm query failed ({ex.GetType().Name}).");
        }

        // ---- TBS device info -------------------------------------------------
        string? interfaceType = null;
        bool tbsOk = false;
        try
        {
            var info = new NativeMethods.TPM_DEVICE_INFO();
            uint r = NativeMethods.Tbsi_GetDeviceInfo((uint)Marshal.SizeOf<NativeMethods.TPM_DEVICE_INFO>(), ref info);
            if (r == NativeMethods.TBS_SUCCESS)
            {
                tbsOk = true;
                interfaceType = MapInterfaceType(info.tpmInterfaceType);
                if (specVersion is null && info.tpmVersion is 1 or 2)
                    specVersion = info.tpmVersion == 2 ? "2.0" : "1.2";
                notes.Add($"TBS: tpmVersion={info.tpmVersion}, interfaceType={info.tpmInterfaceType} (raw), impRevision={info.tpmImpRevision}.");
            }
            else
            {
                notes.Add($"Tbsi_GetDeviceInfo returned 0x{r:X8}.");
            }
        }
        catch (DllNotFoundException)
        {
            notes.Add("tbs.dll not present.");
        }
        catch (Exception ex)
        {
            notes.Add($"Tbsi_GetDeviceInfo failed ({ex.GetType().Name}).");
        }

        // ---- Discrete-part probe (LPC/SPI PnP enumeration) -----------------
        bool? discreteOnLpcOrSpi = ProbeDiscreteBus(notes);

        bool present = wmiPresent || tbsOk;
        var result = TpmInferenceHeuristic.Infer(present, mfrName, mfrIdRaw, interfaceType, discreteOnLpcOrSpi);

        string source = wmiPresent && tbsOk ? "Win32_Tpm+TBS"
            : wmiPresent ? "Win32_Tpm"
            : tbsOk ? "TBS"
            : "unavailable";

        return new TpmInfo
        {
            InferredType = result.Type,
            InferenceBasis = result.Basis,
            Present = present,
            SpecVersion = specVersion,
            ManufacturerId = mfrIdRaw,
            ManufacturerName = mfrName,
            ManufacturerVersion = mfrVersion,
            InterfaceType = interfaceType,
            PhysicalPresenceVersion = ppVersion,
            IsEnabled = isEnabled,
            IsActivated = isActivated,
            IsOwned = isOwned,
            Source = source,
            Notes = notes
        };
    }

    /// <summary>
    /// Look for a SecurityDevices PnP entity enumerated on an LPC or SPI bus. Windows enumerates
    /// every TPM 2.0 as <c>ACPI\MSFT0101</c> regardless of packaging, so a bare ACPI match is
    /// inconclusive and returns false (heuristic then falls back to the manufacturer id).
    /// </summary>
    private static bool? ProbeDiscreteBus(List<string> notes)
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(
                new ManagementScope(@"\\.\root\CIMV2"),
                new ObjectQuery("SELECT DeviceID, PNPDeviceID, Name, Caption FROM Win32_PnPEntity WHERE PNPClass = 'SecurityDevices'"));

            bool sawSecurityDevice = false;
            foreach (ManagementBaseObject mo in searcher.Get())
            {
                using (mo)
                {
                    sawSecurityDevice = true;
                    string id = ((AsString(mo, "DeviceID") ?? "") + " " + (AsString(mo, "PNPDeviceID") ?? "")).ToUpperInvariant();
                    if (id.Contains("SPI\\") || id.Contains("LPC\\") || id.Contains("\\LPCTPM") || id.Contains("SPB\\"))
                    {
                        notes.Add($"PnP: a SecurityDevices entity is enumerated on an LPC/SPI bus ({id.Trim()}).");
                        return true;
                    }
                }
            }

            if (sawSecurityDevice)
            {
                notes.Add("PnP: SecurityDevices entity present but only via ACPI (inconclusive for discrete vs firmware).");
                return false;
            }

            notes.Add("PnP: no SecurityDevices entity enumerated.");
            return false;
        }
        catch (Exception ex)
        {
            notes.Add($"PnP probe failed ({ex.GetType().Name}); discrete-bus signal unavailable.");
            return null;
        }
    }

    // ------------------------------------------------------------------ IMonitor

    public override Task StartAsync(CancellationToken ct)
    {
        if (!OperatingSystem.IsWindows())
        {
            SetHealth(MonitorHealth.Unavailable("TPM monitor requires Windows"));
            return Task.CompletedTask;
        }

        try
        {
            _ = GetTpmInfo(); // warm the cache; also surfaces query health via Notes

            if (_etw is not null)
            {
                _etw.EventReceived += OnEtwEvent;
                _etw.Subscribe(EtwFeedTopic.TpmWmi);
            }

            // Push subscriptions. Channel names vary by OS build; unavailable ones are simply skipped.
            TrySubscribe("Microsoft-Windows-TPM-WMI", null, EventCategory.Tpm);
            TrySubscribe("Microsoft-Windows-TPM-WMI/Admin", null, EventCategory.Tpm);
            TrySubscribe("System", "*[System[Provider[@Name='TBS' or @Name='Microsoft-Windows-TPM-WMI']]]", EventCategory.Tbs);

            int active = _subs.Count(s => s.Active);
            if (active == 0 && _etw is null)
                SetHealth(MonitorHealth.Degraded("no TPM/TBS event source subscribed (channels absent); GetTpmInfo() still works"));
            else if (active < _subs.Count)
                SetHealth(MonitorHealth.Degraded($"{active}/{_subs.Count} TPM/TBS channels active"));
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

    private void TrySubscribe(string channel, string? xpath, EventCategory category)
    {
        var sub = new EventLogSubscription(channel, xpath, rec =>
        {
            var evt = EventRecordConverter.ToMonitorEvent(rec, Clock, Name, ClassifyRecord(rec, category));
            RaiseEvent(evt);
        });
        sub.TryStart();
        _subs.Add(sub);
    }

    private static EventCategory ClassifyRecord(EventRecord rec, EventCategory fallback)
    {
        try
        {
            string? p = rec.ProviderName;
            if (string.Equals(p, "TBS", StringComparison.OrdinalIgnoreCase)) return EventCategory.Tbs;
            if (p is not null && p.Contains("TPM", StringComparison.OrdinalIgnoreCase)) return EventCategory.Tpm;
        }
        catch { /* ignore */ }
        return fallback;
    }

    private void OnEtwEvent(object? sender, EtwFeedEvent e)
    {
        if (e.Topic != EtwFeedTopic.TpmWmi) return;
        RaiseEvent(new MonitorEvent
        {
            TimestampQpc = e.TimestampQpc,
            TimestampUtc = e.TimestampUtc,
            Category = EventCategory.Tpm,
            Source = Name,
            Provider = e.ProviderName,
            EventId = e.EventId,
            Severity = e.Level <= 2 ? EventSeverity.Error : e.Level == 3 ? EventSeverity.Warning : EventSeverity.Info,
            Message = e.FormattedMessage ?? $"{e.ProviderName} id {e.EventId} ({e.TaskName}).",
            Data = new Dictionary<string, string>(e.Payload) { ["provider"] = e.ProviderName }
        });
    }

    // ------------------------------------------------------------------ helpers

    private static string DecodeManufacturerId(uint id)
    {
        // TCG capability value: 4 ASCII bytes, most-significant byte first.
        Span<char> chars = stackalloc char[4];
        chars[0] = (char)((id >> 24) & 0xFF);
        chars[1] = (char)((id >> 16) & 0xFF);
        chars[2] = (char)((id >> 8) & 0xFF);
        chars[3] = (char)(id & 0xFF);
        var s = new string(chars).Replace('\0', ' ').TrimEnd();
        return string.IsNullOrWhiteSpace(s) ? id.ToString() : s;
    }

    private static string? DecodeManufacturerName(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        string k = raw.Trim().Trim('\0').ToUpperInvariant();
        return k switch
        {
            "AMD" => "AMD",
            "IFX" => "Infineon",
            "STM" or "SMSC" => "STMicroelectronics",
            "INTC" or "INTEL" => "Intel",
            "NTC" or "NUVOTON" => "Nuvoton",
            "IBM" => "IBM",
            "BRCM" or "BROADCOM" => "Broadcom",
            "MSFT" => "Microsoft (Pluton)",
            "QCOM" => "Qualcomm",
            "ATML" => "Atmel",
            "HPE" => "HPE",
            "GOOG" => "Google",
            _ => null
        };
    }

    // TPM_IFTYPE_* from tbs.h — re-verify at build time (flagged in report).
    private static string? MapInterfaceType(uint iftype) => iftype switch
    {
        1 => "TIS",
        7 => "CRB",
        _ => null
    };

    private static string? AsString(ManagementBaseObject mo, string prop)
    {
        try { return mo[prop] as string; }
        catch { return null; }
    }

    private static uint? AsUInt(ManagementBaseObject mo, string prop)
    {
        try { return mo[prop] is null ? null : Convert.ToUInt32(mo[prop]); }
        catch { return null; }
    }

    private static bool? AsBool(ManagementBaseObject mo, string prop)
    {
        try { return mo[prop] is null ? null : Convert.ToBoolean(mo[prop]); }
        catch { return null; }
    }

    public override async ValueTask DisposeAsync()
    {
        await StopAsync(CancellationToken.None).ConfigureAwait(false);
        await base.DisposeAsync().ConfigureAwait(false);
    }
}
