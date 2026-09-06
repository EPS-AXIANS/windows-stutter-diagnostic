# ETW sessions, providers and cost

This document is the reviewer-facing companion to `src/StutterDiag.Etw`. It lists every
real-time session the tool starts, every provider and keyword it enables, what each yields,
**why** it is collected, and the measured/expected CPU cost. See `docs/ARCHITECTURE.md` §8 for
the contract and `docs/LIMITATIONS.md` §3 for the "the tool perturbs what it measures" caveat.

All of this needs **administrator** (or the service account's `SeSystemProfilePrivilege`).
Real-time ETW consumption — kernel *and* user — is elevation-gated. When a session cannot be
created the owning type sets `Health = Unavailable(...)` and the orchestrator continues with
the remaining monitors; nothing throws.

---

## 1. Sessions

| Session | Type | Owner | Elevation |
|---|---|---|---|
| `StutterDiag-Kernel` | NT kernel logger (`TraceEventSession` + `EnableKernelProvider`) | `EtwKernelSession` | required |
| `StutterDiag-User`   | user-mode multi-provider session | `EtwUserSession` | required |

Both feed **one** consumer, `EtwProcessing`, which runs `Source.Process()` on a dedicated
background thread per session. The per-event callbacks do **no allocation** for the
high-frequency kernel events (DPC / ISR / CSwitch / ReadyThread): they parse into a flat
`EtwRecord` value and push it into a bounded, **drop-oldest** `System.Threading.Channels`
channel. A single worker task drains the channel, resolves driver names off the hot path
(`ModuleResolver`) and raises the CLR events consumed by `StutterDiag.Monitors`. Disk-I/O and
hard-fault events carry a file-name string and therefore do allocate one string per event —
accepted because those events are orders of magnitude rarer than DPC/ISR.

Buffer sizing comes from `AppConfig.Etw` (`bufferSizeKb` × `bufferCount`), folded into
`TraceEventSession.BufferSizeMB` (TraceEvent 3.1.16 exposes only the total).

---

## 2. `StutterDiag-Kernel` — keywords

`KernelTraceEventParser.Keywords` flags. **Note the TraceEvent spelling
`DeferedProcedureCalls` (single "r").**

### Always on — target < 0.5 % CPU on a normal desktop

| Keyword | Events | Yields | Why |
|---|---|---|---|
| `Process`, `Thread` | process/thread rundown + lifetime | pid/tid → name mapping for every other event | attribution; "recently started" flag on snapshots |
| `ImageLoad` | driver + module load/unload, DC start rundown | address→module map | resolve DPC/ISR routine addresses to a driver file (`ModuleResolver`); driver load/unload as a correlatable signal |
| `DeferedProcedureCalls` | every DPC: routine address, CPU, elapsed | per-driver DPC time, DPC-accumulation stutters | DPC storms starve normal threads → micro-freeze; primary "which driver" evidence |
| `Interrupt` | every ISR: routine address, CPU, elapsed, vector | per-driver ISR time | same as DPC, one layer lower (line-based ISR → driver) |
| `MemoryHardFaults` | hard page faults: file, thread, elapsed | per-process hard-fault bursts | a hard fault blocks the faulting thread on disk — a direct, attributable stall |
| `DiskIO` + `DiskFileIO` | physical read/write completion: disk, size, service time, file, initiating pid | per-I/O latency spikes with file + process | storage latency is a classic stutter cause; `DiskFileIO` adds the file name |

### High-resolution only — 1–3 %+ CPU under load, gated to trigger windows

Enabled by `HighResWindowController` for `HighRes.WindowSeconds` around an auto-detected
stutter or a user mark, then disabled again.

| Keyword | Events | Yields | Why |
|---|---|---|---|
| `ContextSwitch` | every CPU thread switch: new/old tid, pid, priority, wait reason | ready→running latency, who ran instead | *scheduling* stutters (thread was ready but couldn't get a core) |
| `Dispatcher` | ReadyThread (thread made runnable) | pairs with CSwitch to measure ready→running gap | isolates "scheduler latency" from "CPU stolen by DPC" |
| `Profile` | sampled-profile interrupts (1 kHz) with stacks | where CPU time went during the window | shows the hot path during a hitch without always-on stack cost |

### Runtime toggling

`EtwKernelSession.EnableHighResolution()` / `DisableHighResolution()` first attempt an
**in-place** second `EnableKernelProvider` call with the combined keyword mask. On modern
Windows the kernel keyword mask can be updated on a live session, **but TraceEvent 3.1.16 may
reject the call** — in that case the method returns `KeywordChangeResult.RestartRequired` and
the caller invokes `EtwKernelSession.Restart()`, which tears the OS session down, recreates it
with the desired mask and raises `SessionRecreated`. `EtwProcessing` handles that event by
re-binding its parser callbacks and restarting the pump thread. **`NOTE(build)`: confirm which
path 3.1.16 actually takes and delete the dead branch.**

---

## 3. `StutterDiag-User` — providers

Each provider is enabled **only if `ProviderDiscovery` reports it registered** on the machine
(`TraceEventProviders.GetPublishedProviders()`), so the report can state exactly which data
sources were available. Keyword masks are deliberately small.

| Provider | Keywords (mask) | Yields | Why |
|---|---|---|---|
| `Microsoft-Windows-WHEA-Logger` | all | machine-check / PCIe / memory / bus corrected+fatal error records | hardware errors are a high-value correlation signal |
| `Microsoft-Windows-Kernel-Power` | Informational | sleep/resume, monitor power, battery/AC transitions | power-state changes reshuffle scheduling & clocks |
| `Microsoft-Windows-Kernel-Processor-Power` | Informational | P-state / idle-state / throttle transitions | approx. C/P-state residency (MSRs are unreadable from user mode); throttling ↔ stutter |
| `Microsoft-Windows-Kernel-PnP` | Informational | device arrival/removal/start problems | a device reset mid-session can hitch the whole box |
| `Microsoft-Windows-Kernel-Memory` | `0x40` (hard-fault-ish) | memory-pressure / low-memory conditions | pressure → paging → hard faults → stalls |
| `Microsoft-Windows-DxgKrnl` | `0x1` ("Base": Present / Flip / QueuePacket / DmaPacket) | GPU-scheduler packets, present completions, TDR/adapter reset | GPU packet-execution gaps and PresentMon-style frame time |
| `Microsoft-Windows-Dwm-Core` | all (low volume) | DWM composition presents | frame time for composed (windowed) apps |
| `Microsoft-Windows-StorPort` | all | port-driver I/O request timing (sees queueing the kernel layer hides) | second, lower storage-latency view |
| `Microsoft-Windows-TPM-WMI` | all | TPM/TBS provisioning + error events | fTPM/dTPM timing vs. stutters is the whole point of the tool |
| `Microsoft-Windows-DeviceGuard` | Informational | VBS / HVCI status changes | VBS changes interrupt/scheduling behaviour |
| `Microsoft-Windows-DriverFrameworks-UserMode` | Warning | UMDF device lifecycle / faults | user-mode driver hiccups (audio, input, printers) |
| `Microsoft-Windows-USB-USBXHCI` | Warning | xHCI controller / device resets | a USB re-enumeration storm stalls the input/audio path |
| `Microsoft-Windows-Audio` | Warning | audio engine state, glitches | audio glitch is often the first *felt* symptom |
| `Microsoft-Windows-Kernel-EventTracing` | all | ETW self-health: lost / dropped events, buffer overflows | every report must state how trustworthy its own fine-grained data is |

**`NOTE(build)`** — the DxgKrnl, StorPort and Kernel-Memory keyword *values*, and the DxgKrnl /
DWM / StorPort event names and payload field names used by `FrametimeMonitor`,
`GpuPacketGapDetector` and `EtwDiskIoMonitor`, are the values PresentMon / WPA commonly use but
**must be checked against the installed manifests** (`wevtutil gp <provider>`) and PresentMon's
`PresentMonTraceConsumer`. They vary by Windows build.

---

## 4. Time base

Every ETW timestamp is put on the application's single QPC axis (`ARCHITECTURE.md` §5) by
`EtwTimebase`:

- **Exact path (preferred):** TraceEvent's raw hardware counter `TraceEvent.TimeStampQPC` is the
  *same* counter as `QpcClock.GetTimestamp()` / `Stopwatch.GetTimestamp()` on Windows, so the
  mapping is literally `return e.TimeStampQPC;`. Whether that member is public in 3.1.16 needs
  confirming — hence it is not the code path today.
- **Shipped path:** anchor `QpcClock.GetTimestamp()` to the first ETW event's
  `TimeStampRelativeMSec` (a definitely-public `double`), then every later event is
  `anchorQpc + MsToTicks(relMSec − anchorRelMSec)`. Lock-free after the first call; accurate to
  a single fixed sub-millisecond offset that does not affect windowing or ordering.

`EtwProcessing.Timebase` is the shared instance — user-session monitors (`FrametimeMonitor`,
`GpuPacketGapDetector`, `EtwDiskIoMonitor` StorPort path) should be given it so all sources map
onto the same offset.

---

## 5. Expected performance impact

| Scope | Typical CPU | Notes |
|---|---|---|
| Always-on kernel keywords (DPC/ISR/ImageLoad/DiskIO/hard-fault) | **< 0.5 %** on a normal desktop | dominated by DPC/ISR event rate; a driver behaving badly raises it |
| `StutterDiag-User` providers (small keyword masks) | **< 0.3 %** | mostly idle; spikes on device/power events |
| `ContextSwitch` + `Dispatcher` | **1–3 %+** under load | scales with switch rate; the reason it is windowed |
| `Profile` @ 1 kHz with stacks | **~1–2 %** during the window | stack walks are the cost |
| `EtwProcessing` worker + channel | negligible | bounded channel, drop-oldest; dropped/processed counts are in every report |

The tool self-reports lost/dropped events (`EtwSessionHealth`, via
`Microsoft-Windows-Kernel-EventTracing` and `TraceEventSource.EventsLost`) and raises a
`EventCategory.Health` `MonitorEvent` whenever losses increase. If a run shows high losses,
treat its fine-grained (high-res-window) data with suspicion.

---

## 6. Public surface used by `StutterDiag.Monitors` / `StutterDiag.Service`

- `EtwKernelSession` — `Start()`, `EnableHighResolution()` / `DisableHighResolution()` →
  `KeywordChangeResult`, `Restart()`, `SessionRecreated`, `Health`, `IsAvailable`, `Session`.
- `EtwUserSession` — `Start()`, `IsEnabled(name)`, `EnabledProviders`, `MissingProviders`,
  `Health`, `Session`.
- `ProviderDiscovery` — `IsAvailable(name)`, `MissingFrom(wanted)`, `AllProviders`, `Refresh()`.
- `ModuleResolver` — `AddImage(...)`, `RemoveImage(...)`, `Resolve(addr)` /
  `Resolve(addr, pid)` → `ModuleResolution(Name, Version)` (`"unresolved"` when unknown).
- `EtwProcessing : IEtwKernelStream, IAsyncDisposable` — `StartAsync` / `StopAsync`, `Timebase`,
  and the `DpcIsrObserved` / `ReadyThreadObserved` / `ContextSwitchObserved` / `DiskIoObserved`
  / `HardFaultObserved` events plus `DroppedRecordCount` / `ProcessedRecordCount`.
- `EtwSessionHealth` — `Attach(source, label)`, `Poll()`, `EventCaptured`, `LostEventCount`,
  `BufferOverflowEvents`.
