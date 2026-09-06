# Integration notes — build-time verification checklist

This solution was authored without a .NET SDK or a Windows machine in the loop, so **nothing
has been compiler-verified**. The design, contracts and cross-module wiring are consistent, but
the first `dotnet build` on Windows will surface fixups — almost all concentrated in the
platform-API surfaces below. This file is the punch list; work top to bottom.

Build order that isolates failures fastest:
```
dotnet build src/StutterDiag.Core            # pure BCL — should be clean
dotnet build src/StutterDiag.Ipc            # pure BCL
dotnet build src/StutterDiag.Reporting      # Core + Scriban
dotnet build src/StutterDiag.Etw            # TraceEvent — see §1
dotnet build src/StutterDiag.Monitors       # WMI / PDH / native — see §2
dotnet build src/StutterDiag.Service        # wiring — see §3
dotnet build src/StutterDiag.Gui            # WPF — see §4
dotnet test                                 # see §5
dotnet build installer/StutterDiag.Installer # WiX — see §6
```

---

## 1. `StutterDiag.Etw` — TraceEvent 3.1.16 API surface

Search the project for `NOTE(build):` — every uncertain call is tagged inline. Key items:

1. **`ProviderDiscovery.cs`** — confirm `TraceEventProviders.GetPublishedProviders()`,
   `GetProviderName(Guid)`, `GetProviderGuidByName(string)` exact names/return types.
2. **`EtwKernelSession.cs`** — `KernelTraceEventParser.Keywords` member spelling, notably
   `DeferedProcedureCalls` (one "r"), `MemoryHardFaults`, `Dispatcher`, `Profile`, `DiskFileIO`.
3. **Live keyword update:** whether a second `EnableKernelProvider(mask)` on a running session
   updates keywords in place or throws. Drives `EnableHighResolution()` →
   `KeywordChangeResult.RestartRequired` → `Restart()`. One branch is dead once known.
4. **Kernel session name:** does 3.1.16 on Win10+ allow a private kernel session named
   `"StutterDiag-Kernel"`, or is it forced to the machine-wide singleton
   `KernelTraceEventParser.KernelSessionName` ("NT Kernel Logger")? If the latter, only one such
   session can exist machine-wide — handle "already in use".
5. `TraceEventSession.BufferSizeMB`, `.StopOnDispose`, `TraceEventSession.IsElevated()` shape.
6. **Kernel parser event/callback names** in `EtwProcessing.Bind()`: `PerfInfoDPC`(DPCTraceData),
   `PerfInfoISR`(ISRTraceData), `ThreadCSwitch`(CSwitchTraceData),
   `DispatcherReadyThread`(DispatcherReadyThreadTraceData),
   `ImageLoad`/`ImageDCStart`/`ImageUnload`(ImageLoadTraceData),
   `DiskIORead`/`DiskIOWrite`(DiskIOTraceData), `MemoryHardFault`(MemoryHardFaultTraceData).
7. **TraceData field members** — `DPCTraceData.Routine` (is it `ulong` or an `Address` struct?)
   / `.ElapsedTimeMSec`; same for `ISRTraceData`; `CSwitchTraceData.NewThreadID/NewProcessID/
   NewThreadPriority`; `DispatcherReadyThreadTraceData.AwakenedThreadID/AwakenedProcessID`;
   `DiskIOTraceData.ElapsedTimeMSec/TransferSize/DiskNumber/FileName`;
   `MemoryHardFaultTraceData.ElapsedTimeMSec/ByteCount/FileName`;
   `ImageLoadTraceData.ImageBase/ImageSize/FileName`.
   **If per-event DPC/ISR `ElapsedTimeMSec` is not exposed**, `DpcIsrAccumulationDetector` and
   `DpcIsrMonitor` fall back to counts only (already noted in `docs/ETW.md`).
8. `TraceEvent.TimeStampRelativeMSec` (confident) vs `TraceEvent.TimeStampQPC` (if public,
   swap the anchor math in `EtwTimebase` for a direct `return e.TimeStampQPC;`).
9. `EtwSessionHealth.cs` — `TraceEventSource.EventsLost`; the `Microsoft-Windows-Kernel-EventTracing`
   loss/buffer event names (currently matched defensively on "Lost"/"Buffer" substrings).
10. **DxgKrnl / DWM-Core / StorPort / Kernel-Memory keyword values** and DxgKrnl scheduler /
    present event + payload field names — verify against installed manifests (`wevtutil gp ...`)
    and PresentMon's `PresentMonTraceConsumer`. `GpuPacketGapDetector` and `FrametimeMonitor`
    are the most speculative code in the solution; treat their output as provisional until a
    real trace is inspected.
11. `Channel.CreateBounded<T>(BoundedChannelOptions, Action<T> itemDropped)` overload (fine on net8).

---

## 2. `StutterDiag.Monitors` — WMI / PDH / native

1. **`IEtwEventFeed` is defined in Monitors (`Common/IEtwEventFeed.cs`), not in Etw.** The Service
   must provide the adapter (`EtwUserFeedAdapter`) that turns `EtwUserSession.Session.Source`
   into decoded `EtwFeedEvent`s. Every ETW-fed monitor takes `IEtwEventFeed?` and falls back to
   EventLog/polling when it's null — so the solution still runs if the adapter is stubbed.
2. **`SYSTEM_PROCESS_INFORMATION` x64 offsets** in `Process/ProcessMonitor.cs` (`OFF_*`) — from
   phnt/ProcessHacker. Bounds-guarded (wrong offsets degrade data, not stability). Verify against
   `phnt` `ntexapi.h` for the target Windows builds.
3. **`TPM_DEVICE_INFO`** (4×UINT32) and `TPM_IFTYPE_*` mapping (`1→TIS`, `7→CRB`) vs `tbs.h`.
4. **`Win32_Tpm`** members: `ManufacturerIdTxt`, `ManufacturerId` (uint, 4 BE ASCII bytes),
   `SpecVersion`, `PhysicalPresenceVersionInfo`, `ManufacturerVersion`,
   `Is{Enabled,Activated,Owned}_InitialValue`. Some exist only on newer Windows — all reads are
   try-guarded, so worst case is a null field.
5. **`Win32_DeviceGuard`** (`root\Microsoft\Windows\DeviceGuard`):
   `VirtualizationBasedSecurityStatus` (0/1/2), `SecurityServicesRunning` uint[] (1=Cred Guard,
   2=HVCI), `CodeIntegrityPolicyEnforcementStatus`. Verify enum values.
6. **Perf-counter names** — `GPU Adapter Memory\Dedicated Usage` /
   `GPU Process Memory\Dedicated Usage` (may be `Total Committed`);
   `Processor Information\% Processor Performance`, `Processor Frequency`, `% C1 Time`,
   `Parking Status`. Missing counters degrade gracefully to `Unavailable`, so this is a
   completeness check, not a crash risk.
7. GPU Engine / Adapter Memory instance token regexes (`engtype_*`, `phys_*`).
8. Event-log channel spellings in `EventLogMonitor.Candidates` / `WheaMonitor`
   (`Microsoft-Windows-Kernel-WHEA/Errors`, `Microsoft-Windows-DxgKrnl-Operational`,
   `Microsoft-Windows-Kernel-Power/Thermal-Operational`, …). Discovered via `GetLogNames()` so
   unknowns are skipped — verify the useful ones are spelled right.
9. WHEA-Logger event IDs vary by build; classification is payload-field first, keyword/level
   fallback. Display-TDR event id **4101**.
10. **NVML ABI** in `Gpu/VendorGpuProviders.cs`: `nvmlInit_v2`, `nvmlShutdown`,
    `nvmlDeviceGetHandleByIndex_v2`, `nvmlDeviceGetName`, `nvmlDeviceGetClockInfo` (type 0 =
    GRAPHICS), `nvmlDeviceGetTemperature` (sensor 0 = GPU), `nvmlDeviceGetPowerUsage` (mW);
    success = 0; Cdecl. **ADLX / IGCL are intentionally not bound** (COM/dispatch-table APIs) —
    those metrics report `Unavailable`.
11. `MSAcpi_ThermalZoneTemperature` (`root\WMI`) `CurrentTemperature` = tenths of Kelvin.
12. `EventLogQuery.TolerateQueryErrors` assumed present.
13. HKLM reads go through WMI `StdRegProv` (no `Microsoft.Win32.Registry` dependency). Swap to
    the Registry API later if preferred.
14. **Namespace deviation:** `System/SystemInfoProvider.cs` is `namespace StutterDiag.Monitors.Machine`
    (a segment named `System` would shadow the BCL root). DI code references
    `StutterDiag.Monitors.Machine.SystemInfoProvider`.
15. Extra public members the Service wires beyond the interfaces:
    `ProcessMonitor.SnapshotCaptured` (persist periodic snapshots) and
    `SystemInfoProvider.ComputeMachineFingerprint()` (→ `MonitoringSession.MachineFingerprint`).

---

## 3. `StutterDiag.Service` — wiring

- The `EtwUserFeedAdapter` (§2.1) must register its parser callbacks on
  `EtwUserSession.Session.Source` **before** `EtwProcessing.StartAsync` starts the pump.
- Start order: `ProviderDiscovery.Refresh()` → `EtwKernelSession.Start()` /
  `EtwUserSession.Start()` → build adapter → construct every monitor/detector →
  `foreach .StartAsync` → `EtwProcessing.StartAsync` → timers.
- Pass `EtwProcessing.Timebase` to `GpuPacketGapDetector`, `EtwDiskIoMonitor`, `FrametimeMonitor`.
- `SystemInfoProvider` lives in `StutterDiag.Monitors.Machine`.
- Report calls go through `StutterDiag.Reporting.ReportGeneratorFactory.GenerateAsync(IEventStore,
  ReportRequest, QpcClock, CancellationToken)`.
- Least-privilege service account (`NT SERVICE\StutterDiag` + `SeSystemProfilePrivilege` +
  `SeDebugPrivilege` + Perf Log Users + Event Log Readers) via `sc.exe` + `LsaAddAccountRights`;
  document the `LocalSystem` fallback if `LsaAddAccountRights` isn't wired.

---

## 4. `StutterDiag.Gui` — WPF

- `asInvoker` only — never relaunch elevated.
- Pipe name resolution: `--pipe` arg → `STUTTERDIAG_PIPE` env → `%LOCALAPPDATA%\StutterDiag\gui.settings.json` → default `"StutterDiag.Service"`.
- Assumes `Hardcodet.NotifyIcon.Wpf` 2.0.1 exposes `TaskbarIcon.IconSource` (ImageSource) — verify;
  if not, add a small `.ico` and switch to `Icon`.
- WPF `IValueConverter` nullable-annotation warnings are expected and harmless
  (`TreatWarningsAsErrors` is off).

### IPC contract gaps — CLOSED in the post-review integration pass
- `TimelineEntryDto` now carries `long? StutterId`; new `IStutterDiagControl.GetStutterWindowAsync(sessionId, stutterId)`
  returns `StutterWindowDto` (correlations, in-window events, top processes, CPU/GPU/disk/DPC
  summary). Wired into `TimelineView` as a detail panel.
- New `GetRecentFindingsAsync(count)` runs `DiagnosticHypothesisEngine` over the current/most-recent
  session and returns `RecentFindingDto` (Score + OBSERVATION / HYPOTHESIS / NOT PROVEN). Wired into
  `DashboardView` as the "Automatic diagnostic" card, with a `CorrelationScoreToBrushConverter`.
- `SessionDto` gained `MajorStutters` / `TpmEvents` / `WheaEvents` / `DpcSpikes`, fed by the new
  `IEventStore.GetSessionCountsAsync` (GROUP BY scalar queries in `SqliteEventStore`).
- Still open (minor): `MarkResultDto` has no resulting stutter id. Not needed for v1.

---

## 5. Tests (≈89 xUnit methods across 3 projects)

- `StutterDiag.Core.Tests` targets `net8.0` and should run on any OS/CI.
- `StutterDiag.Monitors.Tests` / `StutterDiag.Reporting.Tests` target `net8.0-windows` → CI runs on
  `windows-latest` (see `build/ci.yml`).
- Anything needing live ETW / WMI / perf counters / a real TPM is `[Fact(Skip=...)]` (5 such, all in
  `TpmMonitorTests` — `TpmMonitor`'s decode helpers are `private`; add a public/`internal` seam to
  unskip them).
- **Fixed post-review:** `AsyncBatcher` now counts `DropOldest` capacity-overflow losses via the
  `Channel.CreateBounded(itemDropped:)` callback (previously `DroppedCount` only moved after
  `DisposeAsync`). `AsyncBatcherTests` updated accordingly.
- **`HeartbeatStutterDetector.Measure`** brackets its timing with `GC.CollectionCount` and suppresses
  a wake delay that overlapped one of StutterDiag's own GCs (returns no candidate). Deterministic
  with the test's non-allocating fake clock; a concurrent background GC is a residual sub-permille
  flake. Intended behaviour, documented in `docs/TESTING.md`.
- `TpmInferenceHeuristic` is in namespace `StutterDiag.Core.Tpm` and returns
  `TpmInferenceResult(TpmType Type, string Basis)` (not `TpmInfo`).

---

## 6. `installer/StutterDiag.Installer` — WiX v4

- Needs the WiX 4 SDK (`dotnet tool install --global wix`, or the `WixToolset.Sdk` MSBuild SDK
  package restored).
- Payload is expected from `dotnet publish` of Service/Gui/Cli into a `publish\` root; verify the
  `Files`/`HarvestDirectory` root property matches your publish layout.
- Fixed `UpgradeCode` + `MajorUpgrade`; component GUIDs auto (`*`).
- Service name must be exactly `StutterDiag.Service` (matches `ServiceControl.cs`).
