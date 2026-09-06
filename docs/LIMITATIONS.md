# Limitations & honest boundaries

This tool is a **correlation recorder**, not an oracle. Read this before drawing conclusions.

## 1. It never proves causation

Every "possible cause" the tool prints is a statement about **timing coincidence** measured on
one machine over one run. The report is deliberately structured as
`OBSERVATION → HYPOTHESIS → NOT PROVEN`, and no code path can emit "X caused Y". A signal that
sits 12 ms before a stutter in 30 of 40 cases is *interesting*; it is still not proof, because:

- both could be downstream of a third, unobserved trigger;
- the tool cannot see inside the TPM, the firmware, the GPU scheduler, or most driver internals;
- ETW ordering across CPUs has finite resolution (sub-µs, but non-zero);
- a user-mode heartbeat probe can be delayed for reasons that are not a *felt* stutter.

Use the fTPM/dTPM A/B comparison (§21 of the spec) as the actual experiment: change **one** thing,
measure both configurations under the same workload, compare the distributions.

## 2. Data that Windows does not expose reliably — shown as `Unavailable`, never faked

| Data | Why it's hard | What the tool does instead |
|---|---|---|
| Real per-application frame time without hooking Present | No OS API returns it | Optional PresentMon-technique module over `Microsoft-Windows-DxgKrnl` + `Dwm-Core` ETW. Needs a presenting app and a realtime ETW session (admin). Off → `Unavailable`. |
| GPU temperature / power / core & memory clocks (vendor-neutral) | No Windows API | Optional NVML (NVIDIA) / ADLX (AMD) / IGCL (Intel) loaded dynamically if their DLLs exist. Otherwise `Unavailable`. No dependency on MSI Afterburner or similar. |
| CPU per-core temperature, package power (RAPL) | No user-mode API; needs a kernel driver we deliberately do **not** ship | `MSAcpi_ThermalZoneTemperature` (WMI) if the platform provides a usable thermal zone — often it does not. Otherwise `Unavailable`. |
| Exact hardware C-state / P-state residency | The precise values live in CPU MSRs, unreadable from user mode | Approximated from `Microsoft-Windows-Kernel-Processor-Power` ETW transitions + `Processor Information\% Cx Time` counters. Labelled "approx." |
| Firmware TPM vs discrete TPM (definitive) | Windows does not report it | Heuristic: manufacturer id (`AMD`/`INTC`/`MSFT`/`QCOM` ⇒ firmware; `IFX`/`STM`/`NTC`/… ⇒ discrete) + whether a discrete TPM device is enumerated on the LPC/SPI bus + TBS interface type. `TpmInfo.InferenceBasis` always states what the guess rests on. Ambiguous ⇒ `Undetermined`. |
| Duration of another process's TBS/TPM calls | Would require intrusive API hooking of other processes | Out of scope by design. The tool only observes `tpm.sys` DPC activity in ETW and TPM/TBS entries in the event log, and flags their *timing* relative to stutters. |
| Precise ISR → driver attribution | ETW ISR events carry a routine address, not a module | Resolved via `ImageLoad` events. Unmapped ⇒ `ISR:unresolved`. |
| Anything in the Security event log without rights | ACL | Skipped and reported; needs admin or *Event Log Readers* membership. |

## 3. The tool can perturb what it measures

ETW is efficient but not free. The always-on kernel keywords (DPC/ISR/ImageLoad/DiskIO/hard-faults)
are typically well under 0.5 % CPU on a normal desktop. Context-switch, ready-thread and profile
tracing are heavier (can be 1–3 %+ under load) and are therefore **only enabled for a few seconds
around a trigger** (an auto-detected stutter or a user mark), then disabled again. The tool
self-monitors via `Microsoft-Windows-Kernel-EventTracing` and records dropped/lost-event counts in
every report — if those are high, treat that run's fine-grained data with suspicion.

## 4. Environmental caveats

- **Anti-cheat software** may react to a realtime kernel ETW session or a low-level keyboard hook.
  The Gaming-Mode hotkey uses `RegisterHotKey` first (benign) and only falls back to `WH_KEYBOARD_LL`
  if that fails. You are warned in the UI before enabling Gaming Mode.
- **Virtualization-based security (VBS/HVCI)** changes scheduling and interrupt behaviour; it is
  reported on the System page but never modified.
- Results from a laptop on battery vs. AC are not comparable — power policy dominates. Compare
  like with like.
- Clock discipline over multi-day runs: the QPC↔UTC anchor is re-taken periodically, but absolute
  wall-clock timestamps can still drift by tens of ms over days. All correlation math uses the
  monotonic QPC axis, which is not affected.

## 5. What "no admin" costs you

Running without elevation (portable mode) disables the kernel ETW session entirely: no DPC/ISR
latency, no per-IO disk latency, no context-switch analysis. Detection falls back to the user-mode
heartbeat probe + performance counters + event-log watchers. Stutters are still recorded; the
"which driver / which DPC" attribution is not available. The UI states this plainly.
