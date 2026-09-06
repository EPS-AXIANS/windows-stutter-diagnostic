using StutterDiag.Core.Model;

namespace StutterDiag.Core.Correlation;

/// <summary>
/// Maps raw observations to stable "signal type" keys used for correlation and for the
/// aggregated ranking in the report (e.g. "TPM/TBS", "DPC:nvlddmkm.sys", "DiskLatency").
/// Keeping this in one place means the live engine and the report agree on names.
/// </summary>
public static class SignalCatalog
{
    public const string TpmTbs = "TPM/TBS";
    public const string Whea = "WHEA";
    public const string HardwareError = "HardwareError";
    public const string DiskLatency = "DiskLatency";
    public const string HardFault = "HardFault";
    public const string CpuFrequencyDrop = "CpuFrequencyDrop";
    public const string ThermalThrottle = "ThermalThrottle";
    public const string CState = "CState";
    public const string PowerState = "PowerState";
    public const string KernelPower = "KernelPower";
    public const string GpuDriver = "GpuDriver";
    public const string GpuPacketGap = "GpuPacketGap";
    public const string DeviceReset = "DeviceReset";
    public const string DriverLoad = "DriverLoad";
    public const string AudioGlitch = "AudioGlitch";
    public const string NetworkEvent = "NetworkEvent";
    public const string AcpiEvent = "AcpiEvent";

    public static string Dpc(string driver) => $"DPC:{driver}";
    public static string Isr(string driver) => $"ISR:{driver}";

    /// <summary>Classify a <see cref="MonitorEvent"/>. Returns null if the event carries no correlatable signal.</summary>
    public static string? Classify(MonitorEvent e) => e.Category switch
    {
        EventCategory.Tpm or EventCategory.Tbs => TpmTbs,
        EventCategory.Whea => Whea,
        EventCategory.HardwareError => HardwareError,
        EventCategory.DiskLatency => DiskLatency,
        EventCategory.HardFault => HardFault,
        EventCategory.CpuFrequency => CpuFrequencyDrop,
        EventCategory.ThermalThrottle => ThermalThrottle,
        EventCategory.CState => CState,
        EventCategory.PState => PowerState,
        EventCategory.PowerState => PowerState,
        EventCategory.KernelPower => KernelPower,
        EventCategory.GpuDriver => GpuDriver,
        EventCategory.Gpu => GpuDriver,
        EventCategory.Dpc => Dpc(DriverOf(e)),
        EventCategory.Isr => Isr(DriverOf(e)),
        EventCategory.DriverLoad => DriverLoad,
        EventCategory.Device or EventCategory.Pnp or EventCategory.Usb or EventCategory.Pcie => DeviceReset,
        EventCategory.Audio => AudioGlitch,
        EventCategory.Network => NetworkEvent,
        EventCategory.Acpi => AcpiEvent,
        _ => null
    };

    private static string DriverOf(MonitorEvent e)
        => e.Data is not null && e.Data.TryGetValue("driver", out var d) && !string.IsNullOrWhiteSpace(d)
            ? d
            : "unresolved";

    /// <summary>Human label for the report's correlation ranking.</summary>
    public static string DisplayName(string signalType) => signalType switch
    {
        TpmTbs => "TPM / TBS events",
        Whea => "WHEA hardware errors",
        DiskLatency => "Storage latency spikes",
        HardFault => "Hard page faults",
        CpuFrequencyDrop => "CPU effective-frequency drops",
        ThermalThrottle => "Thermal throttling",
        CState => "Deep C-state transitions",
        PowerState => "Power / P-state changes",
        KernelPower => "Kernel-Power events",
        GpuDriver => "GPU driver activity",
        GpuPacketGap => "GPU packet execution gaps",
        DeviceReset => "Device reset / reconnect",
        DriverLoad => "Driver load / unload",
        AudioGlitch => "Audio subsystem events",
        NetworkEvent => "Network subsystem events",
        AcpiEvent => "ACPI events",
        _ when signalType.StartsWith("DPC:", StringComparison.Ordinal) => $"DPC in {signalType[4..]}",
        _ when signalType.StartsWith("ISR:", StringComparison.Ordinal) => $"ISR in {signalType[4..]}",
        _ => signalType
    };
}
