namespace StutterDiag.Core.Config;

/// <summary>
/// Root configuration, bound from <c>appsettings.json</c> section <c>"StutterDiag"</c>.
/// Mutable POCO so the GUI can edit it and push it back through IPC. Use
/// <see cref="AppConfigDefaults.Create"/> for a fully-populated instance and
/// <see cref="ConfigValidator.Validate"/> to clamp values.
/// </summary>
public sealed class AppConfig
{
    public const string SectionName = "StutterDiag";

    public StorageOptions Storage { get; set; } = new();
    public StutterThresholdOptions StutterThresholds { get; set; } = new();
    public CorrelationOptions Correlation { get; set; } = new();
    public HeartbeatOptions Heartbeat { get; set; } = new();
    public HighResOptions HighRes { get; set; } = new();
    public EtwOptions Etw { get; set; } = new();
    public SamplingOptions Sampling { get; set; } = new();
    public RetentionOptions Retention { get; set; } = new();
    public GamingModeOptions GamingMode { get; set; } = new();
    public GpuVendorSdkOptions GpuVendorSdks { get; set; } = new();
    public PrivacyOptions Privacy { get; set; } = new();
    public ServiceOptions Service { get; set; } = new();
}

public sealed class StorageOptions
{
    public string DatabasePath { get; set; } = @"%ProgramData%\StutterDiag\stutterdiag.sqlite";
    public string LogDirectory { get; set; } = @"%ProgramData%\StutterDiag\logs";
}

public sealed class StutterThresholdOptions
{
    public double MicroStutterMs { get; set; } = 50;
    public double MajorMs { get; set; } = 100;
    public double SevereMs { get; set; } = 250;
    public double CriticalMs { get; set; } = 1000;
}

public sealed class CorrelationOptions
{
    public double PreRollSeconds { get; set; } = 5;
    public double PostRollSeconds { get; set; } = 5;
    /// <summary>±window for a HIGH temporal proximity score.</summary>
    public double HighProximityMs { get; set; } = 25;
    /// <summary>±window for a MEDIUM temporal proximity score.</summary>
    public double MediumProximityMs { get; set; } = 250;
    public double BaselineWindowMinutes { get; set; } = 15;
    /// <summary>Anomaly threshold multiplier on the rolling MAD.</summary>
    public double MadK { get; set; } = 4.0;
    /// <summary>Width of the random windows sampled to compute each signal's base rate.</summary>
    public double BaseRateWindowSeconds { get; set; } = 10;
}

public sealed class HeartbeatOptions
{
    public int ProbeIntervalMs { get; set; } = 10;
    public int ProbeCount { get; set; } = 3;
    public int TimerResolutionMs { get; set; } = 1;
    public int AggregateWindowMs { get; set; } = 10;
    /// <summary>Minimum wake-delay to record as a candidate (below this it is just jitter).</summary>
    public double MinReportMs { get; set; } = 40;
}

public sealed class HighResOptions
{
    public double WindowSeconds { get; set; } = 10;
    public bool EnableContextSwitch { get; set; } = true;
    public bool EnableProfile { get; set; } = true;
    /// <summary>How much high-res history to keep in RAM so pre-roll is always available.</summary>
    public double RingBufferSeconds { get; set; } = 60;
}

public sealed class EtwOptions
{
    public bool EnableKernel { get; set; } = true;
    public bool EnableGpu { get; set; } = true;
    public bool EnableStorPort { get; set; } = true;
    public int BufferSizeKb { get; set; } = 128;
    public int BufferCount { get; set; } = 32;
    public double FlushSeconds { get; set; } = 1;
}

public sealed class SamplingOptions
{
    public double PerfCounterHz { get; set; } = 1;
    public double ProcessSnapshotSeconds { get; set; } = 2;
    public int TopProcessCount { get; set; } = 15;
}

public sealed class RetentionOptions
{
    public int Days { get; set; } = 14;
    public int MaxDbSizeMb { get; set; } = 2048;
}

public sealed class GamingModeOptions
{
    /// <summary>Global hotkey to mark a felt stutter, e.g. "Ctrl+Alt+F12".</summary>
    public string Hotkey { get; set; } = "Ctrl+Alt+F12";
    public bool AutoSaveWindow { get; set; } = true;
}

public sealed class GpuVendorSdkOptions
{
    public bool Nvml { get; set; } = true;   // NVIDIA
    public bool Adlx { get; set; } = true;   // AMD
    public bool Igcl { get; set; } = true;   // Intel
}

public sealed class PrivacyOptions
{
    public bool RecordProcessCommandLines { get; set; }
    public bool RecordUserNames { get; set; }
}

public sealed class ServiceOptions
{
    public string IpcPipeName { get; set; } = "StutterDiag.Service";
    public bool AutoStartMonitoring { get; set; } = true;
}
