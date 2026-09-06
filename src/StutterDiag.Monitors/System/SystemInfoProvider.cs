using System.Management;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using StutterDiag.Core.Abstractions;
using StutterDiag.Core.Model;
using StutterDiag.Core.Time;
using StutterDiag.Monitors.Common;
using StutterDiag.Monitors.Tpm;

// NB: namespace is "Machine", not "System" / "SystemInfo": a segment named "System" would
// shadow the BCL root ("System.*"), and "SystemInfo" would shadow the model type
// StutterDiag.Core.Model.SystemInfo used throughout this file.
namespace StutterDiag.Monitors.Machine;

/// <summary>
/// One-shot machine description for the System page and the report header. Every value is a
/// string so <see cref="SystemInfo.Unavailable"/> / <see cref="SystemInfo.NotOnThisConfig"/>
/// can be stored verbatim wherever a real value cannot be read — nothing is invented.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class SystemInfoProvider : ISystemInfoProvider
{
    private const string Cimv2 = @"\\.\root\CIMV2";

    private readonly QpcClock _clock;
    private readonly ITpmMonitor? _tpm;

    public SystemInfoProvider(QpcClock clock, ITpmMonitor? tpm = null)
    {
        _clock = clock;
        _tpm = tpm;
    }

    public SystemInfo Collect()
    {
        if (!OperatingSystem.IsWindows())
        {
            return new SystemInfo
            {
                Windows = One("Status", "Not running on Windows"),
            };
        }

        var tpmInfo = SafeTpm();

        return new SystemInfo
        {
            Windows = CollectWindows(),
            Cpu = CollectCpu(),
            Gpus = CollectGpus(),
            Memory = CollectMemory(),
            Motherboard = CollectBaseBoard(),
            Bios = CollectBios(),
            Tpm = tpmInfo,
            Security = CollectSecurity(),
            Drivers = CollectDrivers()
        };
    }

    /// <summary>
    /// Stable, non-identifying machine fingerprint: SHA-256 of CPU name + baseboard serial +
    /// total RAM bytes, hex, truncated. Not part of <see cref="ISystemInfoProvider"/>; the
    /// Service calls this when building <see cref="MonitoringSession.MachineFingerprint"/>.
    /// </summary>
    public string ComputeMachineFingerprint()
    {
        if (!OperatingSystem.IsWindows()) return "0000000000000000";

        string cpu = FirstValue(Cimv2, "SELECT Name FROM Win32_Processor", "Name") ?? "cpu?";
        string board = FirstValue(Cimv2, "SELECT SerialNumber FROM Win32_BaseBoard", "SerialNumber") ?? "board?";
        string ram = FirstValue(Cimv2, "SELECT TotalPhysicalMemory FROM Win32_ComputerSystem", "TotalPhysicalMemory") ?? "0";

        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes($"{cpu.Trim()}|{board.Trim()}|{ram.Trim()}"));
        return Convert.ToHexString(hash)[..16].ToLowerInvariant();
    }

    // ------------------------------------------------------------------ Windows

    private IReadOnlyDictionary<string, string> CollectWindows()
    {
        var d = new Dictionary<string, string>();

        try
        {
            var vi = new NativeMethods.RTL_OSVERSIONINFOEXW
            {
                dwOSVersionInfoSize = (uint)System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.RTL_OSVERSIONINFOEXW>()
            };
            if (NativeMethods.RtlGetVersion(ref vi) == 0)
            {
                d["Version"] = $"{vi.dwMajorVersion}.{vi.dwMinorVersion}.{vi.dwBuildNumber}";
                d["BuildNumber"] = vi.dwBuildNumber.ToString();
                d["ProductType"] = vi.wProductType == 1 ? "Workstation" : "Server";
            }
        }
        catch { d["Version"] = SystemInfo.Unavailable; }

        try
        {
            const string cv = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion";
            d["ProductName"] = RegString(cv, "ProductName") ?? SystemInfo.Unavailable;
            d["DisplayVersion"] = RegString(cv, "DisplayVersion") ?? RegString(cv, "ReleaseId") ?? SystemInfo.Unavailable;
            string? build = RegString(cv, "CurrentBuildNumber");
            int? ubr = RegDword(cv, "UBR");
            if (!string.IsNullOrEmpty(build)) d["Build"] = ubr is { } u ? $"{build}.{u}" : build;
            d["InstallationType"] = RegString(cv, "InstallationType") ?? SystemInfo.Unavailable;
            d["EditionID"] = RegString(cv, "EditionID") ?? SystemInfo.Unavailable;
        }
        catch { /* keep RtlGetVersion values */ }

        try { d["MachineFingerprint"] = ComputeMachineFingerprint(); } catch { /* non-fatal */ }
        d.TryAdd("Version", SystemInfo.Unavailable);
        return d;
    }

    // ------------------------------------------------------------------ CPU

    private IReadOnlyDictionary<string, string> CollectCpu()
    {
        foreach (var mo in Query(Cimv2, "SELECT * FROM Win32_Processor"))
        {
            using (mo)
            {
                return new Dictionary<string, string>
                {
                    ["Name"] = Str(mo, "Name"),
                    ["Manufacturer"] = Str(mo, "Manufacturer"),
                    ["Cores"] = Str(mo, "NumberOfCores"),
                    ["LogicalProcessors"] = Str(mo, "NumberOfLogicalProcessors"),
                    ["MaxClockMHz"] = Str(mo, "MaxClockSpeed"),
                    ["L2CacheKB"] = Str(mo, "L2CacheSize"),
                    ["L3CacheKB"] = Str(mo, "L3CacheSize"),
                    ["Socket"] = Str(mo, "SocketDesignation"),
                    ["ProcessorId"] = Str(mo, "ProcessorId"),
                    ["Architecture"] = Str(mo, "Architecture"),
                    ["VirtualizationFirmwareEnabled"] = Str(mo, "VirtualizationFirmwareEnabled")
                };
            }
        }
        return One("Status", SystemInfo.Unavailable);
    }

    // ------------------------------------------------------------------ GPUs

    private IReadOnlyList<IReadOnlyDictionary<string, string>> CollectGpus()
    {
        var list = new List<IReadOnlyDictionary<string, string>>();
        foreach (var mo in Query(Cimv2, "SELECT * FROM Win32_VideoController"))
        {
            using (mo)
            {
                list.Add(new Dictionary<string, string>
                {
                    ["Name"] = Str(mo, "Name"),
                    ["DriverVersion"] = Str(mo, "DriverVersion"),
                    ["DriverDate"] = CimDate(mo, "DriverDate"),
                    ["VideoProcessor"] = Str(mo, "VideoProcessor"),
                    ["AdapterRAMBytes"] = Str(mo, "AdapterRAM"),
                    ["PNPDeviceID"] = Str(mo, "PNPDeviceID"),
                    ["Resolution"] = $"{Str(mo, "CurrentHorizontalResolution")}x{Str(mo, "CurrentVerticalResolution")}",
                    ["RefreshRateHz"] = Str(mo, "CurrentRefreshRate")
                });
            }
        }
        if (list.Count == 0) list.Add(One("Status", SystemInfo.Unavailable));
        return list;
    }

    // ------------------------------------------------------------------ Memory

    private IReadOnlyDictionary<string, string> CollectMemory()
    {
        var d = new Dictionary<string, string>();
        long total = 0;
        int modules = 0;
        var speeds = new List<string>();
        var parts = new List<string>();

        foreach (var mo in Query(Cimv2, "SELECT * FROM Win32_PhysicalMemory"))
        {
            using (mo)
            {
                modules++;
                if (long.TryParse(Raw(mo, "Capacity"), out long cap)) total += cap;
                string spd = Raw(mo, "ConfiguredClockSpeed") is { Length: > 0 } cs ? cs : Raw(mo, "Speed");
                if (!string.IsNullOrEmpty(spd)) speeds.Add(spd);
                string part = Raw(mo, "PartNumber").Trim();
                if (!string.IsNullOrEmpty(part)) parts.Add(part);
            }
        }

        if (modules == 0)
        {
            d["Status"] = SystemInfo.Unavailable;
            string csTotal = FirstValue(Cimv2, "SELECT TotalPhysicalMemory FROM Win32_ComputerSystem", "TotalPhysicalMemory") ?? "";
            if (long.TryParse(csTotal, out long t)) d["TotalMB"] = (t / (1024 * 1024)).ToString();
            return d;
        }

        d["Modules"] = modules.ToString();
        d["TotalMB"] = (total / (1024 * 1024)).ToString();
        d["ConfiguredSpeedsMHz"] = speeds.Count > 0 ? string.Join(", ", speeds.Distinct()) : SystemInfo.Unavailable;
        d["PartNumbers"] = parts.Count > 0 ? string.Join(", ", parts.Distinct()) : SystemInfo.Unavailable;
        return d;
    }

    // ------------------------------------------------------------------ BaseBoard / BIOS

    private IReadOnlyDictionary<string, string> CollectBaseBoard()
    {
        foreach (var mo in Query(Cimv2, "SELECT * FROM Win32_BaseBoard"))
        {
            using (mo)
            {
                return new Dictionary<string, string>
                {
                    ["Manufacturer"] = Str(mo, "Manufacturer"),
                    ["Product"] = Str(mo, "Product"),
                    ["Version"] = Str(mo, "Version"),
                    ["SerialNumber"] = Str(mo, "SerialNumber")
                };
            }
        }
        return One("Status", SystemInfo.Unavailable);
    }

    private IReadOnlyDictionary<string, string> CollectBios()
    {
        foreach (var mo in Query(Cimv2, "SELECT * FROM Win32_BIOS"))
        {
            using (mo)
            {
                return new Dictionary<string, string>
                {
                    ["Manufacturer"] = Str(mo, "Manufacturer"),
                    ["Version"] = Str(mo, "SMBIOSBIOSVersion"),
                    ["ReleaseDate"] = CimDate(mo, "ReleaseDate"),
                    ["SMBIOSVersion"] = $"{Str(mo, "SMBIOSMajorVersion")}.{Str(mo, "SMBIOSMinorVersion")}"
                };
            }
        }
        return One("Status", SystemInfo.Unavailable);
    }

    // ------------------------------------------------------------------ Security

    private IReadOnlyDictionary<string, string> CollectSecurity()
    {
        var d = new Dictionary<string, string>
        {
            ["SecureBoot"] = ReadSecureBoot(),
            ["HypervisorPresent"] = FirstValue(Cimv2, "SELECT HypervisorPresent FROM Win32_ComputerSystem", "HypervisorPresent") ?? SystemInfo.Unavailable
        };

        // Win32_DeviceGuard lives in a dedicated namespace and is absent on Home/older SKUs.
        bool dgSeen = false;
        foreach (var mo in Query(@"\\.\root\Microsoft\Windows\DeviceGuard", "SELECT * FROM Win32_DeviceGuard"))
        {
            using (mo)
            {
                dgSeen = true;
                int vbs = Int(mo, "VirtualizationBasedSecurityStatus");
                d["VBS"] = vbs switch { 0 => "Off", 1 => "Configured (not running)", 2 => "Running", _ => SystemInfo.Unavailable };

                var running = IntArray(mo, "SecurityServicesRunning");
                d["MemoryIntegrity(HVCI)"] = running.Contains(2) ? "On" : "Off";
                d["CredentialGuard"] = running.Contains(1) ? "On" : "Off";
                d["CoreIsolation"] = vbs == 2 ? "On" : "Off";

                int ci = Int(mo, "CodeIntegrityPolicyEnforcementStatus");
                d["CodeIntegrityPolicy"] = ci switch { 0 => "Off", 1 => "Audit", 2 => "Enforced", _ => SystemInfo.Unavailable };
            }
        }
        if (!dgSeen)
        {
            d["VBS"] = SystemInfo.NotOnThisConfig;
            d["MemoryIntegrity(HVCI)"] = SystemInfo.NotOnThisConfig;
            d["CoreIsolation"] = SystemInfo.NotOnThisConfig;
        }
        return d;
    }

    private string ReadSecureBoot()
    {
        try
        {
            int? v = RegDword(@"SYSTEM\CurrentControlSet\Control\SecureBoot\State", "UEFISecureBootEnabled");
            if (v is { } value) return value == 1 ? "On" : "Off";
        }
        catch { /* fall through to firmware probe */ }

        try
        {
            // A BIOS/CSM (non-UEFI) boot makes this call fail with ERROR_INVALID_FUNCTION.
            var buf = new byte[1];
            NativeMethods.GetFirmwareEnvironmentVariableW(
                "SecureBoot", "{8be4df61-93ca-11d2-aa0d-00e098032b8c}", buf, (uint)buf.Length);
            int err = System.Runtime.InteropServices.Marshal.GetLastWin32Error();
            if (err == 1) return SystemInfo.NotOnThisConfig; // ERROR_INVALID_FUNCTION -> legacy BIOS
            return buf[0] == 1 ? "On" : "Off";
        }
        catch
        {
            return SystemInfo.Unavailable;
        }
    }

    // ------------------------------------------------------------------ Drivers

    private IReadOnlyList<DriverInfo> CollectDrivers()
    {
        var list = new List<DriverInfo>();
        foreach (var mo in Query(Cimv2, "SELECT DeviceName, DriverVersion, DriverDate, Manufacturer, DeviceClass, InfName, DriverProviderName FROM Win32_PnPSignedDriver"))
        {
            using (mo)
            {
                try
                {
                    string name = Raw(mo, "DeviceName");
                    if (string.IsNullOrWhiteSpace(name)) name = Raw(mo, "InfName");
                    if (string.IsNullOrWhiteSpace(name)) continue;

                    DateTime? date = null;
                    string cim = Raw(mo, "DriverDate");
                    if (cim.Length > 0)
                    {
                        try { date = ManagementDateTimeConverter.ToDateTime(cim); } catch { /* leave null */ }
                    }

                    list.Add(new DriverInfo(
                        Name: name,
                        Version: NullIfEmpty(Raw(mo, "DriverVersion")),
                        Date: date,
                        Vendor: NullIfEmpty(Raw(mo, "DriverProviderName")) ?? NullIfEmpty(Raw(mo, "Manufacturer")),
                        DeviceClass: NullIfEmpty(Raw(mo, "DeviceClass")),
                        Path: NullIfEmpty(Raw(mo, "InfName"))));
                }
                catch { /* skip a malformed row */ }
            }
        }
        return list;
    }

    // ------------------------------------------------------------------ helpers

    private TpmInfo SafeTpm()
    {
        try
        {
            if (_tpm is not null) return _tpm.GetTpmInfo();
            // A TpmMonitor that was never started holds no resources (no subscriptions, no ETW).
            return new TpmMonitor(_clock).GetTpmInfo();
        }
        catch (Exception ex)
        {
            return TpmInfo.NotDetected($"TPM query threw {ex.GetType().Name}; treated as no information.");
        }
    }

    private static IReadOnlyList<ManagementBaseObject> Query(string scope, string wql)
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(new ManagementScope(scope), new ObjectQuery(wql));
            var list = new List<ManagementBaseObject>();
            foreach (ManagementBaseObject mo in searcher.Get()) list.Add(mo);
            return list;
        }
        catch
        {
            return Array.Empty<ManagementBaseObject>();
        }
    }

    // ---- HKLM reads via WMI StdRegProv (no Microsoft.Win32.Registry dependency) ----
    private const uint HKLM = 0x80000002;

    private static string? RegString(string subKey, string valueName) => RegInvoke("GetStringValue", subKey, valueName) as string;

    private static int? RegDword(string subKey, string valueName)
    {
        object? v = RegInvoke("GetDWORDValue", subKey, valueName);
        return v is null ? null : Convert.ToInt32(v);
    }

    private static object? RegInvoke(string method, string subKey, string valueName)
    {
        try
        {
            using var reg = new ManagementClass(new ManagementScope(@"\\.\root\default"), new ManagementPath("StdRegProv"), null);
            using ManagementBaseObject inParams = reg.GetMethodParameters(method);
            inParams["hDefKey"] = HKLM;
            inParams["sSubKeyName"] = subKey;
            inParams["sValueName"] = valueName;
            using ManagementBaseObject outParams = reg.InvokeMethod(method, inParams, null);
            if (Convert.ToInt32(outParams["ReturnValue"]) != 0) return null;
            return method == "GetStringValue" ? outParams["sValue"] : outParams["uValue"];
        }
        catch
        {
            return null;
        }
    }

    private string? FirstValue(string scope, string wql, string prop)
    {
        foreach (var mo in Query(scope, wql))
        {
            using (mo)
            {
                string v = Raw(mo, prop);
                if (v.Length > 0) return v;
            }
        }
        return null;
    }

    private static string Str(ManagementBaseObject mo, string prop)
    {
        string v = Raw(mo, prop);
        return v.Length == 0 ? SystemInfo.Unavailable : v;
    }

    private static string Raw(ManagementBaseObject mo, string prop)
    {
        try { return mo[prop]?.ToString()?.Trim() ?? ""; }
        catch { return ""; }
    }

    private static int Int(ManagementBaseObject mo, string prop)
    {
        try { return mo[prop] is null ? -1 : Convert.ToInt32(mo[prop]); }
        catch { return -1; }
    }

    private static int[] IntArray(ManagementBaseObject mo, string prop)
    {
        try
        {
            if (mo[prop] is IEnumerable<object> objs) return objs.Select(Convert.ToInt32).ToArray();
            if (mo[prop] is Array arr) return arr.Cast<object>().Select(Convert.ToInt32).ToArray();
        }
        catch { /* ignore */ }
        return Array.Empty<int>();
    }

    private static string CimDate(ManagementBaseObject mo, string prop)
    {
        string raw = Raw(mo, prop);
        if (raw.Length == 0) return SystemInfo.Unavailable;
        try { return ManagementDateTimeConverter.ToDateTime(raw).ToString("yyyy-MM-dd"); }
        catch { return raw; }
    }

    private static string? NullIfEmpty(string s) => string.IsNullOrWhiteSpace(s) ? null : s;

    private static IReadOnlyDictionary<string, string> One(string key, string value) => new Dictionary<string, string> { [key] = value };
}
