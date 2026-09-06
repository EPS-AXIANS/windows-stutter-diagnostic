# Testing

## How to run

```powershell
dotnet restore StutterDiag.sln
dotnet build   StutterDiag.sln -c Release
dotnet test    StutterDiag.sln -c Release
```

`dotnet test` runs three xUnit projects:

| Project | TFM | Notes |
|---|---|---|
| `tests/StutterDiag.Core.Tests` | `net8.0` | Pure logic. Runs on any OS. |
| `tests/StutterDiag.Monitors.Tests` | `net8.0-windows` | Heartbeat detector via its test seam; TPM monitor stubs. Build/run on Windows. |
| `tests/StutterDiag.Reporting.Tests` | `net8.0-windows` | HTML/JSON generators, timeline, session comparer. Build/run on Windows. |

Coverage is collected with `coverlet.collector` (`--collect:"XPlat Code Coverage"`), matching `build/ci.yml`.

`tests/Directory.Build.props` sets `IsPackable=false` / `IsTestProject=true` and re-imports the
repo-root `Directory.Build.props`.

## What is covered

### Core (`StutterDiag.Core.Tests`)

- **DiagnosticHypothesisEngineTests** - aggregation counts across stutters; `NO EVIDENCE` for an
  `alwaysReport` signal that never co-occurs; ordering; **`Output_never_contains_causal_language`**
  (guard test backing ARCHITECTURE.md §1 / §11, via `ContainsCausalLanguage` and a raw
  case-insensitive scan for "caused" / "because of the" / "proves").
- **CorrelationEngineTests** - proximity -> HIGH/MEDIUM/LOW/NoEvidence via a fake
  `ICorrelationDataSource`; the pre-roll window edge; an injected `BaselineStats` breach lifting a
  far-in-time metric to >= MEDIUM; base rate flowing from an `IBaseRateProvider` stub onto
  `StutterCorrelation.BaseRate`; window bounds.
- **BaselineStatsTests** - median & MAD on a known series (`* 1.4826`); `IsHigh`/`IsLow` around
  `median +/- MadK*MAD`; time-window eviction; `< 8` samples -> no baseline.
- **ProximityScorerTests** - the full mapping table incl. `metricIsAnomalous` bumping LOW -> MEDIUM
  and forcing >= MEDIUM past the window edge.
- **StutterAggregatorTests** - merge within `MergeWindowMs` (max duration + both detectors in
  `CorroboratedBy`); no merge when apart; severity at each threshold (`Classify`); `UserMarked` ->
  Confidence High; `Flush` finalises only after the window.
- **SqliteEventStoreTests** - temp-file DB; `InitializeAsync` schema + `-wal` file; round-trip of
  session / events (Data dict + RawXml) / samples / dpc-isr / stutter (`AddStutterReturningIdAsync`)
  + correlations / process snapshot + rows / SystemInfo / drivers / health; `FlushAsync` makes
  batched appends queryable; `PruneSessionsAsync` deletes an ended old session and its children;
  `GetDatabaseSizeBytesAsync > 0`. The class is its own `IAsyncLifetime` fixture and deletes the
  `.sqlite` / `-wal` / `-shm` files on teardown.
- **QpcClockTests** - `GetTimestamp` monotonic over a spin; `QpcToUtc(UtcToQpc(t)) ~= t`;
  `Reanchor` sane.
- **AsyncBatcherTests** - explicit `FlushAsync` delivers buffered items; a large input flushes in
  bounded batches; dispose drains; `DroppedCount` after the writer is completed. **Deviation:** the
  channel uses `BoundedChannelFullMode.DropOldest`, whose `TryWrite` returns `true` while the writer
  is open, so **capacity overflow is dropped silently and does not move `DroppedCount`** - the
  counter only advances for an `Enqueue` after `DisposeAsync`. The test asserts that real behaviour.
- **BoundedRingBufferTests** - overwrite-oldest at capacity; inclusive `Query`; `EvictOlderThan`.
- **ConfigValidatorTests** - out-of-range clamps + warning text; non-increasing stutter thresholds
  reset to defaults; empty pipe name restored; default config -> no warnings.
- **TpmInferenceHeuristicTests** - `AMD`/`INTC`/`MSFT` -> Firmware; `IFX`/`STM`/`NTC` -> Discrete;
  discrete-device-on-bus wins over a firmware vendor id; unknown vendor + no bus device ->
  Undetermined (basis names the interface type); `present:false` -> NotDetected; `Basis` always
  non-empty. (`TpmInferenceHeuristic` lives in `StutterDiag.Core.Tpm` and returns
  `TpmInferenceResult(TpmType Type, string Basis)`.)
- **RetentionManagerTests** - with a substituted `IEventStore`: prune by age, then keep pruning the
  oldest finished session while over the size cap; stop when only the running session remains
  (no infinite loop); the by-age cutoff is `now - RetentionDays`.
- **SignalCatalogTests** - `Classify` for every correlatable `EventCategory` and null for the rest;
  `Dpc`/`Isr` key formatting and the `driver` / `unresolved` fallback; `DisplayName` for known keys
  and pass-through for unknown.

### Monitors (`StutterDiag.Monitors.Tests`)

- **HeartbeatStutterDetectorTests** - a `FakeProbeClock` (public nested
  `HeartbeatStutterDetector.IProbeClock`) whose `SleepMs` advances virtual time by a fixed real
  amount, driven through the `internal Measure(int probeId, int intervalMs, out StutterCandidate?)`
  seam (InternalsVisibleTo is set in `StutterDiag.Monitors.csproj`). Asserts: a wake delay
  `>= MinReportMs` yields exactly one candidate with `EstimatedDurationMs ~= delay - interval` and
  `Detector == Heartbeat`; a sub-threshold delay yields none; every probe emits a
  `sched.wakedelay.ms` sample. Test parallelism is disabled for this assembly (`AssemblyInfo.cs`)
  because `Measure` reads `GC.CollectionCount` deltas to suppress self-GC pauses - see below.
- **TpmMonitorTests** - `TpmInferenceHeuristic` itself is covered in Core. `TpmMonitor` has no
  public/internal pure helpers (`DecodeManufacturerId` uint->ASCII, `DecodeManufacturerName`,
  `MapInterfaceType` are all `private`), so only "construction touches no hardware" is asserted; the
  live-WMI / TBS / PnP / EventLog paths are recorded as `[Fact(Skip="requires Windows TPM")]`.

### Reporting (`StutterDiag.Reporting.Tests`)

- `TestModel.cs` hand-builds a complete `ReportModel` (two stutters, a HIGH TPM/TBS ranking row, a
  `NO EVIDENCE` WHEA finding from the real `DiagnosticHypothesisEngine`) - no `IEventStore` needed.
- **HtmlReportGeneratorTests** - `Render(model)` contains "Correlation does not prove causation.";
  no `http://` / `https://`; `DiagnosticHypothesisEngine.ContainsCausalLanguage(html)` is false;
  contains `OBSERVATION` / `HYPOTHESIS` / `NOT PROVEN` / `NO EVIDENCE`; the ranking line
  `1. ... Correlated with X/Y stutters`; a `[HIGH]` / `badge high` TPM/TBS row.
- **JsonReportGeneratorTests** - `Serialize(model)` parses as JSON; enums are camelCase strings
  (`format` -> `"json"`, `score` -> `"high"`); an infinite `Lift` from a zero base rate serialises
  as the string `"Infinity"` without throwing; top-level shape.
- **TimelineBuilderTests** - `Build` entries sorted by Qpc; `Zoom(center, +/-ms)` and
  `Zoom(stutters, id, +/-ms)` narrow to the window and keep the stutter entry; unknown id -> empty.
- **SessionComparerTests** - `Compare(a, b)` first row is the `HeaderNote`; a row for every
  documented metric (stutter count, mean/p95 duration, TPM, DPC, WHEA, CPU%, GPU%, storage,
  power-state, top signals); deterministic row order; the `Delta` column is a plain signed value.
- Where a loader path is exercised, set `ReportDataLoader.GeneratedUtcOverride` for stable output.

## Needs a real Windows box (not exercised in CI)

These are hardware / OS-service dependent and are either `[Fact(Skip=...)]` or simply out of scope
for unit tests:

- **Live ETW** - kernel session (DPC/ISR/CSwitch/DiskIO), user providers (WHEA-Logger,
  Kernel-Power, DxgKrnl, StorPort, TPM-WMI, ...). Needs admin / `SeSystemProfilePrivilege`.
- **WMI / CIM** - `Win32_Tpm`, `Win32_PnPEntity` (SecurityDevices), `Win32_PnPSignedDriver`,
  `MSAcpi_ThermalZoneTemperature`, system-info queries.
- **TBS** - `Tbsi_GetDeviceInfo` (TPM interface type / version).
- **Performance counters (PDH)** - CPU/GPU/disk/memory samplers.
- **EventLogWatcher** - System / Application / Security channel push subscriptions.
- **Windows Service lifecycle** - `ServiceInstall` / `ServiceControl`, the `AUTOSTART` registry
  override, actual start/stop under the SCM.
- **Global hotkey**, **named-pipe IPC** end-to-end, **WPF GUI**.
- **The WiX MSI** - built by `dotnet build installer/StutterDiag.Installer` on Windows after a
  `dotnet publish` of the three exes; see `installer/StutterDiag.Installer/StutterDiag.Installer.wixproj`.

## Determinism notes

- No `DateTime.Now`; no `Task.Delay`-based timing assertions. `AsyncBatcherTests` waits on a
  drain condition with a generous 5 s cap, never on a fixed delay.
- **`HeartbeatStutterDetector.Measure`** brackets its work with `GC.CollectionCount(0/2)` reads and
  discards a wake delay that overlapped one of *its own* collections. The test does no allocation
  between those reads and disables intra-assembly test parallelism, so in practice the delta is
  zero; a concurrent background GC on another thread remains a theoretical (sub-permille) flake.

## Compiler verification

No code in this change set was compiled or run here (no .NET SDK / no Windows in the authoring
environment). Tests and the installer are written to be correct against the current `src/` and are
expected to build on the first Windows `dotnet build`; minor fixups may surface. See
`docs/INTEGRATION-NOTES.md`.
