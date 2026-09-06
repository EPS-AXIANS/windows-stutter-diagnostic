namespace StutterDiag.Core.Model;

/// <summary>Broad classification of a captured <see cref="MonitorEvent"/>.</summary>
public enum EventCategory
{
    Stutter,
    UserMark,
    Tpm,
    Tbs,
    Whea,
    HardwareError,
    Dpc,
    Isr,
    DriverLoad,
    Cpu,
    CpuFrequency,
    PowerState,
    CState,
    PState,
    ThermalThrottle,
    Gpu,
    GpuDriver,
    Disk,
    DiskLatency,
    Memory,
    HardFault,
    Device,
    Pnp,
    Usb,
    Pcie,
    Audio,
    Network,
    DeviceGuard,
    EventLog,
    KernelPower,
    Acpi,
    SessionLifecycle,
    Health,
    Other
}

/// <summary>Severity of a captured event, aligned with ETW / Event Log levels.</summary>
public enum EventSeverity
{
    Verbose,
    Info,
    Warning,
    Error,
    Critical
}

/// <summary>Stutter magnitude buckets. Default thresholds: 50 / 100 / 250 / 1000 ms.</summary>
public enum StutterSeverity
{
    Micro,
    Major,
    Severe,
    Critical
}

/// <summary>Which detection method produced a stutter (candidate).</summary>
public enum DetectorKind
{
    Heartbeat,
    DpcIsrAccumulation,
    ReadyThreadLatency,
    Frametime,
    GpuPacketGap,
    UserMarked
}

/// <summary>How much trust to place in a single detection before corroboration.</summary>
public enum DetectionConfidence
{
    Low,
    Medium,
    High
}

/// <summary>
/// Strength of a purely <b>temporal</b> correlation between a signal and a stutter.
/// This is never a statement of causation.
/// </summary>
public enum CorrelationScore
{
    NoEvidence,
    Low,
    Medium,
    High
}

/// <summary>Why a process snapshot was taken.</summary>
public enum SnapshotTrigger
{
    Periodic,
    AutoStutter,
    UserMark,
    Manual
}

/// <summary>Best-effort classification of the platform TPM. See <see cref="TpmInfo.InferenceBasis"/>.</summary>
public enum TpmType
{
    NotDetected,
    Undetermined,
    Firmware,
    Discrete
}

/// <summary>Output format requested from an <c>IReportGenerator</c>.</summary>
public enum ReportFormat
{
    Html,
    Json,
    Csv,
    Zip
}

/// <summary>Operational state of an individual monitor.</summary>
public enum HealthStatus
{
    Ok,
    Degraded,
    Unavailable,
    Failed
}
