# Windows Stutter Diagnostic — Architecture & Implementation Contract

> This document is the **single source of truth** for module boundaries, namespaces,
> conventions and the public contract surface of `StutterDiag.Core`.
> Every project in the solution must conform to it. When in doubt, read the code
> in `src/StutterDiag.Core` — it is authoritative over prose.

---

## 1. Goal & non-goals

**Goal:** run in the background for hours/days at <1–2 % average CPU, detect
micro-stutters / freezes by several independent methods, and for every detected
event capture what Windows was doing in a window around it — so that *after the
fact* a human can look for **temporal correlations** with fTPM/dTPM activity,
drivers, DPC/ISR, WHEA, power-state changes, disk latency, etc.

**Non-goals:**

- Never modify, disable or reconfigure the TPM (or anything else).
- Never send data off the machine. No telemetry, no account, no cloud.
- Never claim causation. The tool produces `OBSERVATION` / `HYPOTHESIS` /
  `NOT PROVEN` triplets and `HIGH/MEDIUM/LOW/NO EVIDENCE` **correlation** labels only.
  No code path may emit the substring `caused` / `caused by` / `is responsible for`
  in a diagnostic conclusion. This is enforced by a unit test
  (`DiagnosticHypothesisEngineTests.Output_never_contains_causal_language`).

---

## 2. Technology

| Concern | Choice |
|---|---|
| Language / runtime | C# 12, .NET 8 (LTS), **x64 only** |
| Cross-platform TFM | `net8.0` for `Core`, `Ipc` (so they unit-test on CI Linux) |
| Windows TFM | `net8.0-windows` for `Etw`, `Monitors`, `Service`, `Reporting`, `Gui`, `Cli` |
| Background host | `Microsoft.Extensions.Hosting` + `Microsoft.Extensions.Hosting.WindowsServices` |
| ETW | `Microsoft.Diagnostics.Tracing.TraceEvent` |
| Storage | SQLite via `Microsoft.Data.Sqlite`, WAL mode, batched writes |
| IPC (Service ⇄ GUI/CLI) | newline-delimited JSON messages over a **named pipe**. No gRPC. |
| Logging | Serilog → rolling file (size-capped) + console (Debug builds) |
| WMI / CIM | `System.Management` (Windows-only, lives only in `Monitors`) |
| Perf counters | `System.Diagnostics.PerformanceCounter` (PDH) |
| Event Log | `System.Diagnostics.Eventing.Reader` (`EventLogWatcher`, push) |
| Report HTML | `Scriban` templating + a **zero-dependency** inline `<canvas>` timeline. No CDN, no external JS/CSS. |
| GUI | WPF, `CommunityToolkit.Mvvm`, `Hardcodet.NotifyIcon.Wpf` for the tray icon. Charts are custom-drawn (no chart lib). |
| Installer | WiX v4 MSI + a `dotnet publish` self-contained portable ZIP |
| Tests | xUnit + FluentAssertions (pinned 6.12.2) + NSubstitute |

Package versions are centralized in `Directory.Packages.props`. **Do not put
`Version=` on `PackageReference`**; add the version there instead.

---

## 3. Projects & responsibilities

```
src/
  StutterDiag.Core         net8.0        Contracts, models, QPC clock, config,
                                         SQLite store, correlation engine,
                                         baseline stats, hypothesis engine,
                                         ring buffers. NO Windows-only deps.
  StutterDiag.Ipc          net8.0        IPC message DTOs + named-pipe transport
                                         (server + client helpers). Shared by
                                         Service, Gui, Cli.
  StutterDiag.Etw          net8.0-windows  ETW session lifecycle (kernel + user),
                                         provider discovery, module/address
                                         resolution, session health. Emits
                                         Core.MonitorEvent / MetricSample / DpcIsrStat.
  StutterDiag.Monitors     net8.0-windows  All IMonitor / IStutterDetector /
                                         I*Monitor implementations that are not
                                         pure ETW plumbing: Heartbeat detector,
                                         CPU, GPU, Disk, Memory, Process, TPM,
                                         Power, Device, PerfCounter, EventLog,
                                         WHEA, DPC/ISR aggregation, Frametime.
                                         References Etw + Core.
  StutterDiag.Service      net8.0-windows  Windows Service host. MonitorOrchestrator,
                                         HighResWindowController, IPC server,
                                         session lifecycle, retention. exe.
  StutterDiag.Reporting    net8.0-windows  IReportGenerator implementations
                                         (HTML/JSON/CSV/ZIP), TimelineBuilder,
                                         SessionComparer, wraps DiagnosticHypothesisEngine.
  StutterDiag.Gui          net8.0-windows  WPF app. Dashboard/System/Timeline/
                                         Events/Settings/Compare pages. Tray icon.
                                         Global hotkey (Gaming Mode). exe.
  StutterDiag.Cli          net8.0-windows  `stutterdiag start|stop|status|report|compare`. exe.
tests/
  StutterDiag.Core.Tests
  StutterDiag.Monitors.Tests
  StutterDiag.Reporting.Tests
installer/
  StutterDiag.Installer    WiX v4 .wixproj
```

**Dependency direction (never violate):**

```
Core  ◄── Ipc
Core  ◄── Etw  ◄── Monitors ◄── Service
Core  ◄── Reporting ◄── Service
Core, Ipc ◄── Gui, Cli
```

`Core` depends on nothing in the solution. Nothing depends on `Gui`/`Service`/`Cli`.

---

## 4. Namespaces

Root namespace = assembly name. So:

- `StutterDiag.Core.Abstractions` — all interfaces
- `StutterDiag.Core.Model` — all DTO/record/enum types
- `StutterDiag.Core.Time` — `QpcClock`
- `StutterDiag.Core.Config` — `AppConfig` + loader
- `StutterDiag.Core.Storage` — `SqliteEventStore`, migrations
- `StutterDiag.Core.Correlation` — `CorrelationEngine`, `BaselineStats`, `ProximityScorer`, `BaseRateSampler`, `RingBufferDataSource`
- `StutterDiag.Core.Diagnostics` — `DiagnosticHypothesisEngine`
- `StutterDiag.Core.Retention` — `RetentionManager`
- `StutterDiag.Core.Util` — `BoundedRingBuffer<T>`, `AsyncBatcher<T>`
- `StutterDiag.Ipc` — messages + transport
- `StutterDiag.Etw` — sessions
- `StutterDiag.Monitors` — implementations (sub-namespace per domain, e.g. `StutterDiag.Monitors.Cpu`)
- `StutterDiag.Reporting`
- `StutterDiag.Service`
- `StutterDiag.Gui` (+ `.Views`, `.ViewModels`, `.Services`)
- `StutterDiag.Cli`

File-scoped namespaces everywhere. One top-level type per file, **except** that a
small cluster of tightly-related types (e.g. all `Model` enums, or a record plus its
row type) may share one file named after the primary type.

---

## 5. Time base — the one rule that matters most

Everything is timestamped on **one monotonic axis**: QueryPerformanceCounter ticks.

- `QpcClock.GetTimestamp()` → `long` QPC ticks (== `Stopwatch.GetTimestamp()` on Windows).
- `QpcClock.Frequency` → ticks/second.
- `QpcClock.QpcToUtc(long qpc)` / `QpcClock.UtcToQpc(DateTime utc)` — conversion using
  an anchor pair captured at startup and re-anchored periodically by the Service.
- TraceEvent gives `TraceEvent.TimeStampRelativeMSec` and a QPC; convert to our axis.
- Event Log records give `TimeCreated` (`DateTime`, 100 ns) → `UtcToQpc`.
- `MonitorEvent` carries **both** `TimestampQpc` (authoritative for ordering/correlation)
  and `TimestampUtc` (for display / cross-session).

Never sort or window events by wall-clock. Always by `TimestampQpc`.

---

## 6. Core contract surface (implement against these exact shapes)

### 6.1 `StutterDiag.Core.Abstractions`

```csharp
public interface IMonitor : IAsyncDisposable
{
    string Name { get; }
    MonitorHealth Health { get; }
    Task StartAsync(CancellationToken ct);
    Task StopAsync(CancellationToken ct);
    event EventHandler<MonitorEvent>? EventCaptured;
    event EventHandler<MonitorHealth>? HealthChanged;
}

// A monitor that also produces time-series metrics.
public interface IMetricSource
{
    event EventHandler<MetricSample>? SampleCaptured;
}

public interface IStutterDetector : IMonitor
{
    DetectorKind Kind { get; }
    bool SupportsHighResolutionMode { get; }
    void SetHighResolutionMode(bool enabled);
    event EventHandler<StutterCandidate>? StutterDetected;
}

public interface ITpmMonitor : IMonitor
{
    TpmInfo GetTpmInfo();                 // best-effort snapshot; fields may be Unknown
}

public interface IEventLogMonitor : IMonitor
{
    IReadOnlyCollection<string> ActiveChannels { get; }
    IReadOnlyCollection<string> UnavailableChannels { get; }
}

public interface IWheaMonitor : IMonitor { }        // emits EventCategory.Whea MonitorEvents

public interface IDpcMonitor : IMonitor
{
    // Rolling per-driver DPC/ISR aggregation for the last `window`.
    IReadOnlyList<DpcIsrStat> GetStats(long fromQpc, long toQpc);
}

public interface IProcessMonitor : IMonitor
{
    ProcessSnapshot CaptureSnapshot(SnapshotTrigger trigger);
}

public interface ICpuMonitor  : IMonitor, IMetricSource { }
public interface IGpuMonitor  : IMonitor, IMetricSource { }
public interface IDiskMonitor : IMonitor, IMetricSource { }
public interface IMemoryMonitor : IMonitor, IMetricSource { }
public interface IPowerMonitor : IMonitor { }       // emits C/P-state, power-state MonitorEvents
public interface IDeviceMonitor : IMonitor { }      // PnP / USB / audio / net device events

public interface ISystemInfoProvider
{
    SystemInfo Collect();                            // one-shot, for the System page + report
}

public interface IReportGenerator
{
    ReportFormat Format { get; }
    Task<string> GenerateAsync(ReportRequest request, CancellationToken ct);  // returns output path
}

// Persistence. SqliteEventStore implements the whole thing.
public interface IEventStore : IAsyncDisposable
{
    Task InitializeAsync(CancellationToken ct);
    Task<long> StartSessionAsync(MonitoringSession session, CancellationToken ct);
    Task EndSessionAsync(long sessionId, DateTime endedUtc, CancellationToken ct);

    ValueTask AppendEventAsync(MonitorEvent e);          // batched internally
    ValueTask AppendSampleAsync(MetricSample s);         // batched internally
    ValueTask AppendDpcIsrStatAsync(DpcIsrStat s);
    Task AddStutterAsync(Stutter s, CancellationToken ct);
    Task AddCorrelationsAsync(long stutterId, IEnumerable<StutterCorrelation> c, CancellationToken ct);
    Task AddProcessSnapshotAsync(ProcessSnapshot snap, CancellationToken ct);
    Task SaveSystemInfoAsync(long sessionId, SystemInfo info, CancellationToken ct);
    Task SaveDriversAsync(long sessionId, IEnumerable<DriverInfo> drivers, CancellationToken ct);
    Task RecordHealthAsync(long sessionId, string monitor, MonitorHealth health, CancellationToken ct);
    Task FlushAsync(CancellationToken ct);

    // Read side (report regeneration / GUI history)
    Task<IReadOnlyList<MonitoringSession>> GetSessionsAsync(CancellationToken ct);
    Task<IReadOnlyList<MonitorEvent>> GetEventsAsync(long sessionId, long fromQpc, long toQpc, CancellationToken ct);
    Task<IReadOnlyList<MetricSample>> GetSamplesAsync(long sessionId, long fromQpc, long toQpc, CancellationToken ct);
    Task<IReadOnlyList<Stutter>> GetStuttersAsync(long sessionId, CancellationToken ct);
    Task<IReadOnlyList<StutterCorrelation>> GetCorrelationsAsync(long stutterId, CancellationToken ct);
    Task<IReadOnlyList<DpcIsrStat>> GetDpcIsrStatsAsync(long sessionId, long fromQpc, long toQpc, CancellationToken ct);
    Task<SystemInfo?> GetSystemInfoAsync(long sessionId, CancellationToken ct);
}

// What CorrelationEngine reads to build a window. In-memory impl = RingBufferDataSource;
// historical impl wraps IEventStore.
public interface ICorrelationDataSource
{
    IReadOnlyList<MonitorEvent> GetEvents(long fromQpc, long toQpc);
    IReadOnlyList<MetricSample> GetSamples(long fromQpc, long toQpc);
    IReadOnlyList<DpcIsrStat> GetDpcIsrStats(long fromQpc, long toQpc);
    ProcessSnapshot? GetNearestSnapshot(long qpc);
}
```

### 6.2 `StutterDiag.Core.Model` (records unless noted)

```csharp
public enum EventCategory
{
    Stutter, UserMark, Tpm, Tbs, Whea, HardwareError, Dpc, Isr, DriverLoad,
    Cpu, CpuFrequency, PowerState, CState, PState, ThermalThrottle,
    Gpu, GpuDriver, Disk, DiskLatency, Memory, HardFault,
    Device, Pnp, Usb, Pcie, Audio, Network, DeviceGuard, EventLog,
    KernelPower, Acpi, SessionLifecycle, Health, Other
}

public enum EventSeverity { Verbose, Info, Warning, Error, Critical }

public enum StutterSeverity { Micro, Major, Severe, Critical }   // 50 / 100 / 250 / 1000 ms defaults

public enum DetectorKind { Heartbeat, DpcIsrAccumulation, ReadyThreadLatency, Frametime, GpuPacketGap, UserMarked }

public enum DetectionConfidence { Low, Medium, High }

public enum CorrelationScore { NoEvidence, Low, Medium, High }

public enum SnapshotTrigger { Periodic, AutoStutter, UserMark, Manual }

public enum TpmType { NotDetected, Undetermined, Firmware, Discrete }

public enum ReportFormat { Html, Json, Csv, Zip }

public enum HealthStatus { Ok, Degraded, Unavailable, Failed }

public sealed record MonitorHealth(HealthStatus Status, string? Note = null)
{
    public static readonly MonitorHealth Ok = new(HealthStatus.Ok);
    public static MonitorHealth Unavailable(string why) => new(HealthStatus.Unavailable, why);
    public static MonitorHealth Degraded(string why)    => new(HealthStatus.Degraded, why);
    public static MonitorHealth Failed(string why)      => new(HealthStatus.Failed, why);
}

public sealed record MonitorEvent
{
    public long TimestampQpc { get; init; }
    public DateTime TimestampUtc { get; init; }
    public EventCategory Category { get; init; }
    public string Source { get; init; } = "";          // monitor / provider short name
    public string? Provider { get; init; }              // ETW/event-log provider, if any
    public int? EventId { get; init; }
    public EventSeverity Severity { get; init; } = EventSeverity.Info;
    public string Message { get; init; } = "";
    public string? RawXml { get; init; }
    public IReadOnlyDictionary<string, string>? Data { get; init; }
}

public sealed record MetricSample(long TimestampQpc, string Metric, string Instance, double Value);
// Metric naming: "cpu.total.pct", "cpu.core.pct", "cpu.freq.mhz", "cpu.freq.effective.mhz",
// "gpu.engine.pct", "gpu.vram.usedmb", "disk.read.latency.ms", "disk.write.latency.ms",
// "disk.queue", "mem.available.mb", "mem.committed.mb", "mem.hardfaults.persec", ...
// Instance = "" for system-wide, else core index / device id / engine name.

public sealed record StutterCandidate(
    long TimestampQpc, double EstimatedDurationMs, DetectorKind Detector,
    DetectionConfidence Confidence, string? Note = null);

public sealed record Stutter
{
    public long Id { get; init; }                       // 0 until persisted
    public long SessionId { get; init; }
    public long TimestampQpc { get; init; }
    public DateTime TimestampUtc { get; init; }
    public double DurationMs { get; init; }
    public StutterSeverity Severity { get; init; }
    public DetectorKind Detector { get; init; }
    public DetectionConfidence Confidence { get; init; }
    public IReadOnlyList<DetectorKind> CorroboratedBy { get; init; } = Array.Empty<DetectorKind>();
    public bool UserMarked { get; init; }
}

public sealed record StutterCorrelation(
    long StutterId, string SignalType, double ProximityMs, CorrelationScore Score,
    double BaseRate, string Detail);
// SignalType examples: "TPM/TBS", "DPC:nvlddmkm.sys", "ISR:USBXHCI.SYS",
// "WHEA", "DiskLatency", "HardFault", "CpuFrequencyDrop", "CState", "GpuDriver", "KernelPower"

public sealed record DpcIsrStat(
    long WindowStartQpc, long WindowEndQpc, string Driver, string Kind /* "DPC"|"ISR" */,
    double TotalMs, long Count, double MaxMs);

public sealed record WheaError(
    long TimestampQpc, DateTime TimestampUtc, string ErrorSource, string Severity,
    string Description, string? RawXml);
// ErrorSource: Processor|Cache|Tlb|Bus|Pcie|Memory|Nmi|MachineCheck|Platform|Unknown
// Severity:    Corrected|Recoverable|Fatal|Informational|Unknown

public sealed record ProcessSnapshotRow(
    int Pid, string Name, double CpuPercent, long WorkingSetBytes, long PrivateBytes,
    long ReadBytesPerSec, long WriteBytesPerSec, int ThreadCount, int HandleCount,
    long PageFaultsDelta, double KernelTimeMs, double UserTimeMs,
    bool RecentlyStarted, bool ActivitySpike);

public sealed record ProcessSnapshot(
    long TimestampQpc, DateTime TimestampUtc, SnapshotTrigger Trigger,
    IReadOnlyList<ProcessSnapshotRow> Rows);

public sealed record TpmInfo
{
    public TpmType InferredType { get; init; } = TpmType.Undetermined;
    public string InferenceBasis { get; init; } = "";      // human-readable, always set
    public bool Present { get; init; }
    public string? SpecVersion { get; init; }
    public string? ManufacturerId { get; init; }           // raw 4-char / numeric
    public string? ManufacturerName { get; init; }         // decoded, e.g. "AMD", "Infineon"
    public string? ManufacturerVersion { get; init; }
    public string? InterfaceType { get; init; }            // "TIS" | "CRB" | null
    public string? PhysicalPresenceVersion { get; init; }
    public bool? IsEnabled { get; init; }
    public bool? IsActivated { get; init; }
    public bool? IsOwned { get; init; }
    public string Source { get; init; } = "";              // "Win32_Tpm+TBS", "TBS", "unavailable"
    public IReadOnlyList<string> Notes { get; init; } = Array.Empty<string>();
}

public sealed record DriverInfo(
    string Name, string? Version, DateTime? Date, string? Vendor, string? DeviceClass, string? Path);

public sealed record SystemInfo
{
    public IReadOnlyDictionary<string, string> Windows { get; init; } = Empty;
    public IReadOnlyDictionary<string, string> Cpu { get; init; } = Empty;
    public IReadOnlyList<IReadOnlyDictionary<string, string>> Gpus { get; init; } = Array.Empty<IReadOnlyDictionary<string,string>>();
    public IReadOnlyDictionary<string, string> Memory { get; init; } = Empty;
    public IReadOnlyDictionary<string, string> Motherboard { get; init; } = Empty;
    public IReadOnlyDictionary<string, string> Bios { get; init; } = Empty;
    public TpmInfo Tpm { get; init; } = new();
    public IReadOnlyDictionary<string, string> Security { get; init; } = Empty;  // SecureBoot, VBS, HVCI, HyperV, CoreIsolation
    public IReadOnlyList<DriverInfo> Drivers { get; init; } = Array.Empty<DriverInfo>();
    private static readonly IReadOnlyDictionary<string,string> Empty = new Dictionary<string,string>();
}
// Any value that cannot be obtained reliably MUST be the literal string
// "Unavailable" or "Not available on this Windows configuration" — never invented.

public sealed record MonitoringSession
{
    public long Id { get; init; }
    public DateTime StartedUtc { get; init; }
    public DateTime? EndedUtc { get; init; }
    public string Label { get; init; } = "";
    public TpmType TpmTypeInferred { get; init; }
    public string TpmBasis { get; init; } = "";
    public string MachineFingerprint { get; init; } = "";
    public string ConfigJson { get; init; } = "{}";
    public string Mode { get; init; } = "Standard";       // "Standard" | "Gaming"
}

public sealed record CorrelationWindow(
    Stutter Stutter, long FromQpc, long ToQpc,
    IReadOnlyList<MonitorEvent> Events, IReadOnlyList<MetricSample> Samples,
    IReadOnlyList<DpcIsrStat> DpcIsr, ProcessSnapshot? Snapshot,
    IReadOnlyList<StutterCorrelation> Correlations);

public sealed record DiagnosticFinding(
    CorrelationScore Score, string SignalType,
    string Observation, string Hypothesis, string NotProven,
    int CorrelatedStutters, int TotalStutters, double BaseRatePercent);

public sealed record ReportRequest
{
    public required string OutputPath { get; init; }       // file (html/json/csv) or dir/zip
    public required IReadOnlyList<long> SessionIds { get; init; }
    public ReportFormat Format { get; init; }
    public bool CompareMode { get; init; }                 // 2 sessions => A/B comparison
    public RedactionOptions Redaction { get; init; } = new();
}

public sealed record RedactionOptions
{
    public bool IncludeProcessNames { get; init; } = true;
    public bool IncludeUserNames { get; init; } = false;
    public bool IncludeCommandLines { get; init; } = false;
    public bool IncludeRawEventXml { get; init; } = true;
    public bool IncludeFullEventLog { get; init; } = true;
}

public sealed record SessionComparison(
    MonitoringSession A, MonitoringSession B,
    IReadOnlyList<ComparisonRow> Rows);

public sealed record ComparisonRow(string Metric, string ValueA, string ValueB, string Delta);
```

### 6.3 Config (`StutterDiag.Core.Config.AppConfig`)

Plain mutable POCO, bound from `appsettings.json` section `"StutterDiag"`.
`AppConfigDefaults.Create()` returns a fully-populated instance. `ConfigValidator.Validate`
clamps out-of-range values and returns warnings. Shape:

```jsonc
{
  "StutterDiag": {
    "storage":   { "databasePath": "%ProgramData%/StutterDiag/stutterdiag.sqlite",
                   "logDirectory": "%ProgramData%/StutterDiag/logs" },
    "stutterThresholds": { "microStutterMs": 50, "majorMs": 100, "severeMs": 250, "criticalMs": 1000 },
    "correlation": { "preRollSeconds": 5, "postRollSeconds": 5,
                     "highProximityMs": 25, "mediumProximityMs": 250,
                     "baselineWindowMinutes": 15, "madK": 4.0, "baseRateWindowSeconds": 10 },
    "heartbeat":  { "probeIntervalMs": 10, "probeCount": 3, "timerResolutionMs": 1,
                    "aggregateWindowMs": 10, "minReportMs": 40 },
    "highRes":    { "windowSeconds": 10, "enableContextSwitch": true, "enableProfile": true,
                    "ringBufferSeconds": 60 },
    "etw":        { "enableKernel": true, "enableGpu": true, "enableStorPort": true,
                    "bufferSizeKb": 128, "bufferCount": 32, "flushSeconds": 1 },
    "sampling":   { "perfCounterHz": 1, "processSnapshotSeconds": 2, "topProcessCount": 15 },
    "retention":  { "days": 14, "maxDbSizeMb": 2048 },
    "gamingMode": { "hotkey": "Ctrl+Alt+F12", "autoSaveWindow": true },
    "gpuVendorSdks": { "nvml": true, "adlx": true, "igcl": true },
    "privacy":    { "recordProcessCommandLines": false, "recordUserNames": false },
    "service":    { "ipcPipeName": "StutterDiag.Service", "autoStartMonitoring": true }
  }
}
```

---

## 7. Runtime data flow

```
IStutterDetector.StutterDetected ─┐
IMonitor.EventCaptured ───────────┼─► MonitorOrchestrator
IMetricSource.SampleCaptured ─────┘        │
                                          ├─► RingBufferDataSource (last N s, in RAM)
                                          ├─► BaselineStats.Observe(metric, value, qpc)
                                          └─► IEventStore.Append*  (batched → SQLite)

StutterCandidate ─► StutterAggregator (merge candidates < 30 ms apart, pick max duration,
                    classify severity, note corroborating detectors)
               ─► Stutter ─► IEventStore.AddStutterAsync
               ─► HighResWindowController.Trigger()  (enable CSwitch/Profile ETW for windowSeconds)
               ─► after postRoll elapses:
                     CorrelationEngine.Analyze(stutter, ICorrelationDataSource, AppConfig, BaselineStats)
                       → CorrelationWindow + List<StutterCorrelation>
                       → IEventStore.AddCorrelationsAsync
```

Report generation (Service on demand or Cli):

```
IEventStore (read) ─► CorrelationEngine (re-derive if needed) ─► DiagnosticHypothesisEngine
                   ─► TimelineBuilder ─► IReportGenerator(Html|Json|Csv|Zip)
```

---

## 8. ETW sessions (see docs/ETW.md for the full rationale + measured impact)

Two real-time sessions owned by `StutterDiag.Etw`:

- **`StutterDiag-Kernel`** (`KernelTraceEventParser`, needs admin / `SeSystemProfilePrivilege`).
  Always-on keywords: `DeferredProcedureCalls | Interrupt | ImageLoad | Process | Thread |
  MemoryHardFaults | DiskIO | DiskFileIO`.
  High-res-only (toggled by `HighResWindowController`): `ContextSwitch | Dispatcher | Profile`.
- **`StutterDiag-User`** — user-mode providers, each enabled only if discovered:
  `Microsoft-Windows-WHEA-Logger`, `Microsoft-Windows-Kernel-Power`,
  `Microsoft-Windows-Kernel-Processor-Power`, `Microsoft-Windows-Kernel-PnP`,
  `Microsoft-Windows-Kernel-Memory`, `Microsoft-Windows-DxgKrnl` (small keyword set),
  `Microsoft-Windows-Dwm-Core`, `Microsoft-Windows-StorPort`, `Microsoft-Windows-TPM-WMI`,
  `Microsoft-Windows-DeviceGuard`, `Microsoft-Windows-DriverFrameworks-UserMode`,
  `Microsoft-Windows-USB-USBXHCI`, `Microsoft-Windows-Audio`,
  `Microsoft-Windows-Kernel-EventTracing` (self-health / lost-event counters).

If a session cannot be created (no admin), `EtwKernelSession.Health` becomes
`Unavailable("requires administrator")` and the orchestrator continues with the
remaining monitors. Never throw out of a monitor's `StartAsync` for a missing capability.

---

## 9. Admin / privilege matrix (see docs/PERMISSIONS.md)

| Capability | Needs elevation | Degraded fallback |
|---|---|---|
| Kernel ETW (DPC/ISR/CSwitch/DiskIO) | yes | Heartbeat + perf counters + StorPort-user |
| `Microsoft-Windows-StorPort` realtime | yes | `PhysicalDisk` perf counters |
| Security event log | yes / *Event Log Readers* | skipped, reported |
| Full-detail process snapshot (`SeDebugPrivilege`) | yes | `NtQuerySystemInformation` w/o protected fields |
| Install / (de)register the Windows Service | yes (installer, once) | portable tray-app mode |
| Perf counters, WMI read, EventLogWatcher (System/App), user ETW, GPU counters, hotkey | no | — |

**The Service runs as a dedicated least-privilege account** (`NT SERVICE\StutterDiag`
with `SeSystemProfilePrivilege`, `SeDebugPrivilege`, "Log on as a service", and
membership in *Performance Log Users* + *Event Log Readers*), configurable to
`LocalSystem`. **The GUI never runs elevated.** A one-shot elevated helper
(`--elevated-collector`) is offered only when a capability is actually missing.

---

## 10. Coding conventions

- `async`/`await` end-to-end; every long-running loop takes a `CancellationToken`.
- **No allocation in ETW callbacks.** Parse into pooled/struct locals, hand off via a
  bounded `Channel<T>` to a writer task. The `Channel` drops-oldest with a counter if full.
- Every monitor is independent: a failure sets `Health` + emits a `Health` `MonitorEvent`
  and returns; it never brings down the orchestrator.
- Windows-only calls guarded by `OperatingSystem.IsWindows()` (so `net8.0` libs still load on CI).
- `TimeProvider` injected where testable; `QpcClock` wraps it for the QPC axis.
- Public types get a one-line XML `<summary>`. Non-obvious logic gets a `//` note. Match surrounding density.
- No `DateTime.Now`. Use `DateTime.UtcNow` via injected `TimeProvider`, or `QpcClock`.
- Strings shown to users that represent missing data: `"Unavailable"`.

---

## 11. Tests (minimum bar)

- `CorrelationEngine`: proximity → score mapping; window boundaries; base-rate lift.
- `BaselineStats`: median/MAD rolling correctness; anomaly threshold.
- `DiagnosticHypothesisEngine`: aggregation counts; **no causal language** (guard test);
  `NO EVIDENCE` emitted when a signal never co-occurs.
- `SqliteEventStore`: round-trip of every entity; batching flush; WAL file created.
- `QpcClock`: monotonic; QPC↔UTC round-trip within tolerance.
- `HeartbeatStutterDetector`: with an injected fake time source, a simulated wake delay
  ≥ threshold produces exactly one `StutterCandidate` of ~that duration.
- `TpmInferenceHeuristic`: manufacturer-id → `TpmType` table; `Undetermined` when ambiguous.
- `TimelineBuilder`: ordering, zoom windowing.
- `SessionComparer`: A/B rows for stutter count/duration/TPM/DPC/WHEA/CPU/GPU/storage/power.
- `HtmlReportGenerator`: produces self-contained HTML (no `http`/`https` external refs),
  contains the "Correlation does not prove causation" disclaimer.

---

## 12. What is genuinely not obtainable (state it, don't fake it)

| Data | Reason | What we do instead |
|---|---|---|
| Real per-app frametime without a present hook | OS gives no API | Optional PresentMon-style module over DxgKrnl/DWM ETW; else `Unavailable` |
| GPU temp / power / core clock (vendor-neutral) | No OS API | Optional NVML/ADLX/IGCL; else `Unavailable` |
| CPU per-core temperature, package power (RAPL) | No user-mode API; needs a driver we deliberately don't ship | `MSAcpi_ThermalZoneTemperature` if present; else `Unavailable` |
| Exact hardware C-state/P-state residency | MSRs unreadable from user mode | Approximate via `Kernel-Processor-Power` ETW + `% Cx Time` counters; labelled "approx." |
| fTPM vs dTPM (definitive) | Windows does not expose it | Heuristic (manufacturer id + PnP bus presence + interface type); `TpmInfo.InferenceBasis` always explains; `Undetermined` allowed |
| Duration of another process's TBS/TPM calls | Would need intrusive hooking | Out of scope; we only observe `tpm.sys` DPCs + TPM/TBS event-log entries |
| Precise ISR→driver attribution | ETW gives routine address only | Resolve via `ImageLoad`; else `ISR:unresolved` |
