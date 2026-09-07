# Windows Stutter Diagnostic

A local, no-telemetry Windows tool that runs in the background for hours or days at very low
overhead and records, for every micro-stutter / freeze it can detect, **what Windows was doing
at that exact moment** — so you can look for temporal correlations with fTPM/dTPM activity,
drivers, DPC/ISR latency, WHEA, power-state changes, disk latency, and more.

> It does **not** modify or disable the TPM (or anything else). It collects evidence.
> It reports **temporal correlations**, never causation. See [`docs/LIMITATIONS.md`](docs/LIMITATIONS.md).

---

## What it does

- **Detects stutters several independent ways** (so it's not one fragile counter):
  a high-priority user-mode scheduler-latency probe ("LatencyMon-lite"), cumulative DPC+ISR
  time from ETW, ready-thread latency, PresentMon-technique frame-time gaps, GPU packet-execution
  gaps, and an explicit **user-marked** hotkey for stutters too short or too subjective to catch
  automatically.
- **Classifies** each by configurable thresholds: `50 ms` micro / `100 ms` major / `250 ms` severe
  / `1000 ms` critical.
- **Builds a window** around every stutter (default −5 s … +5 s) and gathers everything in it:
  TPM/TBS events, WHEA records, per-driver DPC/ISR peaks, disk latency, hard page faults, CPU
  effective-frequency drops, C/P-state changes, Kernel-Power events, GPU driver activity, a
  high-resolution process snapshot, and more.
- **Scores correlations** as `HIGH` / `MEDIUM` / `LOW` purely by temporal proximity and by whether
  a metric broke its adaptive per-machine baseline — and computes a **base rate** (how often the
  same signal appears in random non-stutter windows) so the report shows a meaningful lift ratio,
  not a bare count.
- **Reports** as self-contained HTML (with a zoomable timeline), JSON, CSV, or a ZIP bundle.
- **Compares two sessions** (e.g. an fTPM run vs a dTPM run) side by side — the actual experiment.
- Every automated conclusion is rendered as `OBSERVATION:` / `HYPOTHESIS:` / `NOT PROVEN:`.
  No code path can print "the TPM caused the stutter".

Dedicated TPM section: presence, spec version, manufacturer, interface, enabled/activated/owned,
a **firmware-vs-discrete inference** with its basis always stated (or `Undetermined`), plus TPM-WMI
and TBS event tracking. It never calls a TPM state-changing API.

---

## Architecture

```
┌─ StutterDiag.Gui (WPF, never elevated) ─┐   named pipe (JSON)   ┌─ StutterDiag.Service ─┐
│  Dashboard · System · Timeline · Events │◄────────────────────► │  (Windows Service,    │
│  Settings · Compare · Report · tray     │                       │   least-privilege)    │
│  Gaming-Mode global hotkey              │                       │  MonitorOrchestrator  │
└────────────────────────────────────────┘                       │  HighResWindowCtl     │
                                                                 │  CorrelationEngine    │
StutterDiag.Cli  ── start|stop|status|report|compare ───────────► │  RetentionManager     │
                                                                 └──────────┬────────────┘
                                                        SQLite (WAL) + rolling logs
                                                                            │
                     StutterDiag.Reporting ── HTML / JSON / CSV / ZIP ◄─────┘
```

| Project | TFM | Role |
|---|---|---|
| `StutterDiag.Core` | `net8.0` | Contracts, models, QPC time base, SQLite store, correlation engine, adaptive baseline, diagnostic engine, retention. No Windows-only deps (unit-tests on any OS). |
| `StutterDiag.Ipc` | `net8.0` | Newline-delimited-JSON contract + named-pipe transport shared by service / GUI / CLI. |
| `StutterDiag.Etw` | `net8.0-windows` | ETW session lifecycle (kernel + user), provider discovery, address→driver resolution, session-health / lost-event tracking. |
| `StutterDiag.Monitors` | `net8.0-windows` | All detectors and monitors: heartbeat, DPC/ISR, CPU, GPU, disk, memory, process, TPM, power, devices, event log, WHEA, frame time, system info. |
| `StutterDiag.Service` | `net8.0-windows` | The background Windows Service. Owns collection, correlation, storage, retention, and the IPC server. Runs with or without the GUI. |
| `StutterDiag.Reporting` | `net8.0-windows` | Report generators + zoomable timeline + session comparison. |
| `StutterDiag.Gui` | `net8.0-windows` | WPF front end. `asInvoker` only — never auto-elevates. |
| `StutterDiag.Cli` | `net8.0-windows` | Headless control + report generation. |

Full contract: [`docs/ARCHITECTURE.md`](docs/ARCHITECTURE.md).
ETW sessions & providers & measured impact: [`docs/ETW.md`](docs/ETW.md).
Correlation method: [`docs/CORRELATION.md`](docs/CORRELATION.md).
Elevation matrix: [`docs/PERMISSIONS.md`](docs/PERMISSIONS.md).

---

## Build

**Prerequisites:** Windows 10/11 x64 and the [.NET 8 SDK](https://dotnet.microsoft.com/download).
The MSI uses the [WiX v5](https://wixtoolset.org/) MSBuild SDK, which `dotnet` restores from NuGet
automatically — no global `wix` tool required.

```powershell
git clone <this repo>
cd StutterDiag
dotnet restore StutterDiag.sln
dotnet build  StutterDiag.sln -c Release
dotnet test   StutterDiag.sln -c Release   # Core / Monitors / Reporting xUnit projects
```

See [`docs/TESTING.md`](docs/TESTING.md) for what the tests cover and what needs a real Windows box
(live ETW / WMI / perf counters / the service lifecycle).

### Portable build (no installer, no admin required to run)

```powershell
./build/publish-portable.ps1     # -> artifacts/StutterDiag-portable-<version>.zip
```

The portable build runs as a single tray application. Without elevation the **kernel ETW session
is unavailable** (no DPC/ISR or per-IO disk latency); detection falls back to the heartbeat probe,
performance counters and event-log watchers. The UI states which sensors are degraded.

### Installer (MSI)

Publish the three executables, then build the MSI (WiX v5, per-machine, x64):

```powershell
dotnet publish src/StutterDiag.Service/StutterDiag.Service.csproj -c Release -r win-x64 --self-contained true -o installer/StutterDiag.Installer/publish/service
dotnet publish src/StutterDiag.Gui/StutterDiag.Gui.csproj         -c Release -r win-x64 --self-contained true -o installer/StutterDiag.Installer/publish/gui
dotnet publish src/StutterDiag.Cli/StutterDiag.Cli.csproj         -c Release -r win-x64 --self-contained true -o installer/StutterDiag.Installer/publish/cli
dotnet build  installer/StutterDiag.Installer -c Release   # -> installer/StutterDiag.Installer/bin/x64/Release/StutterDiag.msi
```

(Building the solution without publishing first still succeeds — the installer project links a
skeleton MSI in that case.)

The MSI installs the GUI + CLI, registers **`StutterDiag.Service`** (display name
"Windows Stutter Diagnostic Service") as a Windows service — `Start=demand` by default, or
Automatic (delayed) when installed with `msiexec /i StutterDiag.msi AUTOSTART=1`. It adds a
Start-menu shortcut for the GUI. Uninstall stops and removes the service. Collected data in
`%ProgramData%\StutterDiag` is left in place.

### Deploying to a non-technical operator ([`deploy/`](deploy/README.md))

- **You have a Windows + SDK box:** `deploy/publish-release.ps1` builds one self-contained zip
  they just extract and double-click — no SDK, no git, one UAC prompt, a French menu
  (`Lancer-StutterDiag.bat` → `1`).
- **You're on Linux and the operator must build:** send them the source; they double-click
  `deploy/Build-Windows.bat`. It installs the .NET 8 SDK into their user profile (no admin),
  compiles, and on success builds the pack and registers the service. On failure it writes
  `deploy/build-errors.txt` for them to send back — expect 2–4 rounds of fix/push/re-run since
  the tree is not yet compiler-verified.

---

## Run

### As a service (recommended)

After installing, or manually from an elevated prompt:

```powershell
StutterDiag.Service.exe install       # register the service (least-privilege account)
StutterDiag.Service.exe start
```

Then launch **Windows Stutter Diagnostic** from the Start menu. The GUI connects to the service
over a local named pipe; you can close the GUI and the service keeps recording.

`StutterDiag.Service.exe uninstall` removes it.

### From the CLI

```powershell
stutterdiag status
stutterdiag start --mode Gaming
stutterdiag stop
stutterdiag report --format html --out C:\temp\stutter-report.html
stutterdiag report --format zip  --out C:\temp\bundle.zip --sessions 12
stutterdiag compare --a 11 --b 12 --out C:\temp\ftpm-vs-dtpm.html
```

### Gaming Investigation mode

Start a session in **Gaming** mode, play normally, and press the configurable hotkey
(default `Ctrl+Alt+F12`) the instant you feel a hitch. The tool records
`USER MARKED STUTTER` with a precise timestamp and saves the high-resolution window around it.

---

## Configuration

All settings live under the `"StutterDiag"` section of the service's `appsettings.json`
(`%ProgramData%\StutterDiag\appsettings.json` once installed) and are editable from the GUI's
**Settings** page. Key groups: stutter thresholds, correlation window & proximity, heartbeat probe,
high-resolution window, ETW toggles & buffers, sampling rates, retention (days + max DB size),
Gaming-Mode hotkey, optional GPU vendor SDKs, privacy toggles. See
[`docs/ARCHITECTURE.md` §6.3](docs/ARCHITECTURE.md).

---

## Privacy

- 100 % local. **No network calls, no telemetry, no account, no cloud.**
- Process command lines and user names are **not** recorded unless you opt in.
- Exports let you choose what to include (process names, raw event XML, full event log, …).
- Collected data and reports stay under `%ProgramData%\StutterDiag` until you delete them or
  retention prunes them.

---

## Compatibility

Windows 10 64-bit and Windows 11 64-bit. Providers and counters are discovered at runtime; where a
metric is unavailable on your configuration the tool shows `Unavailable` /
`Not available on this Windows configuration` — it never fabricates a value and never crashes for a
missing capability.

---

## License / status

Local diagnostic utility. See [`docs/LIMITATIONS.md`](docs/LIMITATIONS.md) for what this tool can
and cannot tell you — read it before acting on any result.
