# Privileges, elevation and degraded fallbacks

> Expands ARCHITECTURE.md §9. For every capability the tool uses, this document states
> **what it needs**, **why the OS requires that**, and **exactly what happens when the
> privilege is absent**. The guiding rule (ARCHITECTURE §12, §10) is unchanged: when a
> datum cannot be obtained, the tool records `Unavailable` / raises a `Health` event and
> keeps running every other collector — it never fabricates a value and never fails as a
> whole.

---

## 1. Design stance

- **The GUI never runs elevated.** It is a plain user-session WPF app that talks to the
  service over a named pipe. Nothing it does requires admin.
- **The Windows Service runs as a dedicated least-privilege account**
  (`NT SERVICE\StutterDiag`) granted only:
  - `SeSystemProfilePrivilege` — create the kernel ETW session.
  - `SeDebugPrivilege` — read full per-process detail.
  - *Log on as a service*.
  - membership in **Performance Log Users** (PDH / perf counters) and
    **Event Log Readers** (Security channel).
  It can be reconfigured to `LocalSystem`, but that is not required and not recommended.
- **One-shot elevated collector (`--elevated-collector`).** When a capability is actually
  missing at runtime (e.g. the service was installed without the kernel-ETW right, or the
  tool is running portable in tray-app mode), the tool offers to relaunch a short-lived
  elevated helper that captures just the elevated-only data for the current window and
  exits. It is offered only when the capability is genuinely absent, and the user must
  approve the UAC prompt each time. It is never auto-launched.
- Each monitor is independent. Losing one capability degrades exactly one monitor.

---

## 2. Capability matrix

| Capability | Needs elevation? | Why the OS requires it | Degraded fallback |
|---|---|---|---|
| **Kernel ETW session** (`StutterDiag-Kernel`: DPC/ISR, ContextSwitch, DiskIO, ImageLoad, MemoryHardFaults, Profile) | **Yes** — admin or `SeSystemProfilePrivilege` | `StartTrace` for the NT Kernel Logger / a system-logger session is a privileged operation; the kernel will not stream DPC/ISR/CSwitch to a non-privileged consumer. | `HeartbeatStutterDetector` (no privilege) stays the primary detector; `Cpu`/`Disk`/`Memory` PDH counters give the time-series; `Microsoft-Windows-StorPort` user provider (if reachable) gives storage timing. DPC/ISR attribution is simply absent and reported. |
| **`Microsoft-Windows-StorPort` real-time provider** | **Yes** (system logger) | StorPort's port-driver timing keyword is emitted on a kernel/system logger session. | `PhysicalDisk` PDH counters (`Avg. Disk sec/Read|Write`, queue, IOPS) from `DiskMonitor` — 1 Hz series instead of per-IRP timing. |
| **Security event-log channel** | **Yes** — admin *or* **Event Log Readers** | The Security log is ACL'd to administrators / that group by the OS. | `EventLogMonitor` records the channel under `UnavailableChannels`, subscribes to System/Application and every reachable `Microsoft-Windows-*` operational channel, and continues. |
| **Full-detail process snapshot** (`SeDebugPrivilege`) | **Yes** for cross-account/protected processes | Reading another security context's process detail (and any protected-process fields) requires `SeDebugPrivilege`. | `ProcessMonitor` calls `NtQuerySystemInformation(SystemProcessInformation)`, which returns CPU/IO/thread/handle/fault data for **all** PIDs without `SeDebugPrivilege`; only a few protected-process fields would be blank. If even that call fails it falls back to `System.Diagnostics.Process` (no IO-byte or page-fault deltas) and reports `Degraded`. |
| **Install / (de)register the Windows Service** | **Yes**, once, via the installer | Creating a service and assigning it account rights is privileged. | Portable **tray-app mode**: the same monitors run in the user session. Everything that needs elevation (kernel ETW, StorPort, Security log) is unavailable and reported; the heartbeat detector + PDH + WMI + user-mode event logs still work. |
| **Perf counters (PDH)** — `Processor Information`, `PhysicalDisk`, `Memory`, `GPU Engine`, `GPU Adapter Memory` | **No** (Performance Log Users covers a service; interactive users generally have it) | — | If a counter *category* is missing (older OS, disabled counters) the specific `MultiInstanceCounter` reports `Unavailable`, that metric is omitted, and the monitor health goes `Degraded`. Never throws. |
| **WMI / CIM reads** — `Win32_Tpm`, `Win32_Processor`, `Win32_VideoController`, `Win32_PhysicalMemory`, `Win32_BaseBoard`, `Win32_BIOS`, `Win32_PnPSignedDriver`, `Win32_PnPEntity`, `Win32_DeviceGuard`, `MSAcpi_ThermalZoneTemperature`, `StdRegProv` | **No** | `root\CIMV2` and `root\CIMV2\Security\MicrosoftTpm` are readable by authenticated users. `Win32_DeviceGuard` lives in `root\Microsoft\Windows\DeviceGuard` and is absent on Home/older SKUs. | Any class/namespace/property that cannot be read yields the literal `Unavailable` / `Not available on this Windows configuration`. `Win32_Tpm` reads use only the `*_InitialValue` **properties** and never invoke a method, so no elevation and no state change. |
| **`Tbsi_GetDeviceInfo` (tbs.dll)** | **No** | TBS device-info is a read-only query that does not open a TBS context. | If `tbs.dll` is absent or returns non-zero, `TpmInfo.InterfaceType` stays null and a note explains why; the manufacturer-id heuristic still runs. |
| **EventLogWatcher push subscriptions** (System, Application, operational `Microsoft-Windows-*` channels) | **No** (except Security, above) | These channels are world-readable. | Channels not present on the running build go to `UnavailableChannels`; a channel that fails to subscribe is listed with its error. Monitor health = `Degraded` while any are missing, `Ok` when all subscribed. |
| **User-mode ETW providers** (`Kernel-Power`, `Kernel-Processor-Power`, `Kernel-PnP`, `DriverFrameworks-UserMode`, `USB-USBXHCI`, `Audio`, `WHEA-Logger`, `TPM-WMI`, `DxgKrnl`, `DeviceGuard`, `Kernel-Memory`) | **No** for a real-time user session logger | User providers can be enabled on a private real-time session by a normal user; only the *kernel* keywords need privilege. | Delivered to the telemetry monitors through `IEtwEventFeed` (adapted by the Service from `StutterDiag.Etw`'s `EtwUserSession`). If the feed is not live, `CpuPowerStateMonitor`, `DeviceMonitor`, `WheaMonitor`, `TpmMonitor` and `GpuDriverEventMonitor` fall back to `EventLogWatcher` and/or polling and report `Degraded` (P-state / C-state transitions in particular are then not observed — only AC/DC and power-plan changes via `GetSystemPowerStatus` polling). |
| **GPU vendor SDKs** (NVML / ADLX / IGCL) | **No** | Loaded opportunistically with `NativeLibrary.TryLoad`. | If the vendor DLL is absent (or, for ADLX/IGCL, no flat-export binding exists), the provider stays disabled and `gpu.freq.mhz` / `gpu.temp.c` / `gpu.power.w` are simply **absent** — reported `Unavailable`, never estimated. `gpu.engine.pct` / `gpu.vram.usedmb` from PDH are unaffected. |
| **`GetSystemPowerStatus` / `PowerGetActiveScheme` polling** | **No** | Standard Win32 read APIs. | Always available on Windows; this is itself the fallback for the power ETW feed. |
| **`RegisterPowerSettingNotification`** | **No**, but needs a message pump or a service status handle | The OS delivers `WM_POWERBROADCAST` / `PBT_POWERSETTINGCHANGE` only to a window or a service control handler. | `CpuPowerStateMonitor` exposes `RegisterForPowerSettingNotifications(...)` for a host that has one; with no pump it is inert and the 2 s `GetSystemPowerStatus` / active-scheme poll is the effective mechanism. |
| **Global hotkey (Gaming Mode mark)** | **No** | `RegisterHotKey` works per user session. | If the chord is already owned, the GUI reports it and the user picks another. |
| **`RtlGetVersion`, `GetFirmwareEnvironmentVariableW`** | **No** (firmware var read returns `ERROR_INVALID_FUNCTION` on legacy BIOS, handled) | Read-only. | Secure Boot state falls back: `StdRegProv` read of `HKLM\SYSTEM\CurrentControlSet\Control\SecureBoot\State\UEFISecureBootEnabled`, then the firmware-variable probe; if both fail → `Unavailable`; legacy BIOS → `Not available on this Windows configuration`. |

---

## 3. What no privilege level can obtain (ARCHITECTURE §12)

Elevation does **not** unlock these; they are recorded as `Unavailable` with the reason:

- **Per-core CPU temperature, package power / RAPL** — no user-mode API; MSRs need a ring-0
  driver this tool deliberately does not ship. `MSAcpi_ThermalZoneTemperature` (a platform
  thermal zone, *not* the die) is emitted as `cpu.temp.c` when a zone exists, labelled as an
  ACPI reading; otherwise nothing.
- **Exact hardware C-state / P-state residency** — MSRs unreadable from user mode.
  Approximated from `Kernel-Processor-Power` ETW + `% C1/C2/C3 Time` counters and labelled
  "approx.".
- **fTPM vs dTPM, definitively** — Windows does not expose it. `TpmInferenceHeuristic`
  combines manufacturer id + LPC/SPI PnP presence + TBS interface type; `TpmInfo.InferenceBasis`
  always explains the guess and `Undetermined` is a valid result.
- **Real per-application frametime without a present hook** — optional PresentMon-style
  module over DxgKrnl/DWM ETW, else `Unavailable`.
- **GPU core clock / temperature / board power without a vendor SDK** — optional
  NVML/ADLX/IGCL, else `Unavailable`.
- **Duration of another process's TBS/TPM calls** — would need intrusive hooking; out of
  scope. Only `tpm.sys` DPCs and TPM/TBS event-log entries are observed.

---

## 4. Behaviour summary

| Run mode | Kernel ETW | StorPort RT | Security log | Full process detail | Everything else |
|---|---|---|---|---|---|
| Service as `NT SERVICE\StutterDiag` (default) | yes | yes | yes (Event Log Readers) | yes (`SeDebugPrivilege`) | yes |
| Service as `LocalSystem` | yes | yes | yes | yes | yes |
| Portable tray-app, elevated | yes | yes | yes | yes | yes |
| Portable tray-app, normal user | **no → heartbeat + PDH + StorPort-user** | **no → PDH** | **no → skipped, reported** | **partial → NtQuerySystemInformation** | yes |

In every cell marked "no", the affected monitor sets `Health = Unavailable/Degraded`, emits
one `EventCategory.Health` `MonitorEvent` explaining the gap, and the session continues.
