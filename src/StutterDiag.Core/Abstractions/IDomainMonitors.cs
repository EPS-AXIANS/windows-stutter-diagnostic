using StutterDiag.Core.Model;

namespace StutterDiag.Core.Abstractions;

/// <summary>Reports TPM presence/type/version/state and TPM/TBS events. Never changes TPM state.</summary>
public interface ITpmMonitor : IMonitor
{
    /// <summary>Best-effort snapshot; unknown fields stay null / <see cref="TpmType.Undetermined"/>.</summary>
    TpmInfo GetTpmInfo();
}

/// <summary>Subscribes (push, not polling) to relevant Windows event log channels.</summary>
public interface IEventLogMonitor : IMonitor
{
    IReadOnlyCollection<string> ActiveChannels { get; }
    IReadOnlyCollection<string> UnavailableChannels { get; }
}

/// <summary>Emits <see cref="EventCategory.Whea"/> events for hardware error records.</summary>
public interface IWheaMonitor : IMonitor { }

/// <summary>Rolling per-driver DPC / ISR aggregation.</summary>
public interface IDpcMonitor : IMonitor
{
    IReadOnlyList<DpcIsrStat> GetStats(long fromQpc, long toQpc);
}

/// <summary>Periodic low-res process sampling plus on-demand high-res snapshots.</summary>
public interface IProcessMonitor : IMonitor
{
    ProcessSnapshot CaptureSnapshot(SnapshotTrigger trigger);
}

public interface ICpuMonitor : IMonitor, IMetricSource { }

public interface IGpuMonitor : IMonitor, IMetricSource { }

public interface IDiskMonitor : IMonitor, IMetricSource { }

public interface IMemoryMonitor : IMonitor, IMetricSource { }

/// <summary>Emits power-state, C-state and P-state transition events.</summary>
public interface IPowerMonitor : IMonitor { }

/// <summary>Emits PnP / USB / PCIe / audio / network device change and reset events.</summary>
public interface IDeviceMonitor : IMonitor { }

/// <summary>One-shot machine description for the System page and the report header.</summary>
public interface ISystemInfoProvider
{
    SystemInfo Collect();
}
