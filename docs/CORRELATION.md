# Correlation method — reviewer's guide

> This document explains, for reviewers, exactly what the "correlation" in Windows Stutter
> Diagnostic is and — just as important — what it is not. The short version: the tool measures
> **timing coincidence** between a detected stutter and other things Windows recorded around it.
> It never measures, infers, or reports a **mechanism**. Read `docs/ARCHITECTURE.md` §1 (non-goals)
> alongside this.

The code that implements everything below lives in `StutterDiag.Core.Correlation`
(`CorrelationEngine`, `ProximityScorer`, `BaselineStats`, `BaseRateSampler`, `SignalCatalog`) and
`StutterDiag.Core.Diagnostics` (`DiagnosticHypothesisEngine`). `StutterDiag.Reporting` only
*renders* their output; it adds no new inference.

---

## 1. One time axis: QPC

Every event, metric sample, DPC/ISR aggregate and stutter is timestamped in
**QueryPerformanceCounter ticks** on a single monotonic axis (`QpcClock`). Wall-clock UTC is
derived from a periodically re-anchored QPC↔UTC pair and is used **only** for display.

Correlation is always computed on the QPC axis. Nothing is ever ordered or windowed by
wall-clock time — clock adjustments over a multi-day run would otherwise reorder events or move
them in and out of a window. `MonitorEvent` carries both `TimestampQpc` (authoritative) and
`TimestampUtc` (display).

At report time the original run's QPC values are replayed as-is from the store; the report does
not re-anchor them.

---

## 2. The window: pre-roll / post-roll

For a stutter at QPC `t` with configured `correlation.preRollSeconds` / `postRollSeconds`
(default 5 s / 5 s):

```
window = [ t − preRoll , t + postRoll ]
stutter interval = [ t , t + durationMs ]
```

`CorrelationEngine.Analyze` pulls every `MonitorEvent`, `MetricSample` and `DpcIsrStat` whose
timestamp falls in `window`, plus the nearest `ProcessSnapshot`. That set — with the derived
correlations — is a `CorrelationWindow`, the unit the report renders per stutter.

The live pipeline keeps the last `highRes.ringBufferSeconds` of data in RAM
(`RingBufferDataSource`) so the pre-roll is always available without high-resolution ETW running
continuously. Report regeneration rebuilds an equivalent in-memory source from the event store
(`StutterDiag.Reporting.InMemoryCorrelationDataSource`) and re-runs the same engine.

---

## 3. Proximity → HIGH / MEDIUM / LOW

`ProximityScorer` maps the absolute distance in milliseconds between a signal and the stutter
interval to a `CorrelationScore`. `proximityMs = 0` means the signal overlaps the stutter
interval; otherwise it is the gap to the nearer edge.

| Condition (with default config)                              | Score        |
|-------------------------------------------------------------- |------------- |
| `|proximity| ≤ correlation.highProximityMs` (25 ms)          | `High`       |
| `|proximity| ≤ correlation.mediumProximityMs` (250 ms) **or** the signal is a metric that also broke its adaptive baseline in the window | `Medium` |
| `|proximity| ≤ (preRoll + postRoll)` (10 s)                  | `Low`        |
| otherwise                                                    | `NoEvidence` |

Per signal type, the engine keeps the single best occurrence in the window (higher score wins;
ties broken by smaller proximity). These are the `[HIGH]` / `[MEDIUM]` / `[LOW]` labels on each
stutter's "Possible correlations" list. **The score is a distance, not a weight of causation.**

DPC/ISR activity is attributed per driver (module resolved from the routine address via
`ImageLoad`; `unresolved` when it cannot be mapped). A per-driver DPC/ISR group is only
considered when its peak ≥ 1 ms or it broke baseline.

---

## 4. The adaptive MAD baseline

Fixed thresholds do not transfer between machines, so metric anomalies are judged against a
per-machine, per-run **rolling baseline** (`BaselineStats`):

- For each metric key, keep a time-bounded window of recent values
  (`correlation.baselineWindowMinutes`, default 15 min).
- Summarise it as **median** and **MAD** (median absolute deviation), with
  `MAD *= 1.4826` so it estimates the standard deviation for normal data.
- A value is "high" if `value > median + madK · MAD` (`correlation.madK`, default 4),
  "low" symmetrically. A minimum of 8 samples is required before any judgement.

MAD (not mean/σ) is used because it is robust to the very outliers we are hunting: a handful of
latency spikes barely move the median or the MAD, so the spike still stands out. Metrics checked
this way include `disk.read.latency.ms`, `disk.write.latency.ms`, `disk.queue`,
`mem.hardfaults.persec` (breach = high) and `cpu.freq.effective.mhz` (breach = low). A metric
breach promotes an in-window signal to at least `Medium`.

---

## 5. Base rate and lift

A raw "co-occurred with 9 of 47 stutters" is meaningless without knowing how often the same
signal shows up **anywhere**. `BaseRateSampler` runs alongside detection: it repeatedly inspects
a random `correlation.baseRateWindowSeconds`-wide window that is **not** near any stutter and
records which signal types appear. Over a run this yields

```
baseRate(signal) = P(signal appears in a random non-stutter window)
```

which is stored on every `StutterCorrelation`. The report shows

```
lift = correlatedFraction / baseRate
     = (correlatedStutters / totalStutters) / baseRate
```

`lift ≈ 1` → the signal is no more common near stutters than anywhere else (no temporal
association, whatever the raw count). `lift > 1` → over-represented near stutters. `lift` is
still **association only**: a common third factor (e.g. system load) can raise both.

At report time the base rates computed live are replayed
(`StutterDiag.Reporting.StoredBaseRateProvider`); the report does not resample.

---

## 6. Session aggregation: the OBSERVATION / HYPOTHESIS / NOT-PROVEN contract

`DiagnosticHypothesisEngine` rolls the per-stutter correlations up to one `DiagnosticFinding`
per signal type. Session score (`RankFinding`):

- never exceeds the best single-stutter proximity score for that signal;
- a signal that grazed `< 5 %` of stutters is demoted to `Low`;
- `High` requires a `High` single-stutter score **and** co-occurrence with `≥ 15 %` of stutters;
- `Medium` requires `≥ Medium` **and** `≥ 10 %`.

Each finding is emitted as a strict triplet, and the report renders it verbatim under fixed
headings:

- **`OBSERVATION:`** — what was seen: "X of Y stutters had this signal within the window, as
  close as N ms; it appears in ~Z % of random windows." Pure counts and timings.
- **`HYPOTHESIS:`** — the *only* forward-looking sentence, and it is deliberately weak: the two
  "may be temporally correlated"; whether they share a trigger or merely coincide under load
  "cannot be determined from this data alone."
- **`NOT PROVEN:`** — states the thing the data cannot support, always including "that <signal>
  produce the stutters. This tool measures timing coincidence, not mechanism."
- **`BASE RATE:`** — the denominator and lift from §5.

Signals that never co-occurred are emitted as **`NO EVIDENCE`** lines. The report always forces
an explicit line for `TPM/TBS` and `WHEA` even when there is no evidence, via the engine's
`alwaysReport` parameter — so "the TPM angle" is never silently absent.

---

## 7. Why the tool structurally cannot — and must not — claim causation

1. **It observes timing, not mechanism.** Nothing in the pipeline traces a stutter to a
   responsible code path. ETW gives a DPC's routine address and duration, not proof that that
   DPC stalled the thread that stuttered. TBS/TPM call durations for other processes are not
   observable at all (`docs/ARCHITECTURE.md` §12).
2. **Confounding is unhandled by design.** Load, thermals and power state move many signals at
   once. Lift adjusts for base rate but not for a common cause.
3. **Detection is heuristic and multi-method.** A stutter is itself an inference from several
   independent detectors; treating it as a precise fault event would overstate precision.
4. **The blast radius of a false causal claim is high.** The motivating question involves the
   TPM; "the TPM caused this" would invite users to disable a security device. The tool's
   remit is to *gather evidence for a human*, not to conclude.

This is enforced, not just documented:

- `DiagnosticHypothesisEngine` only ever produces the triplet above; no code path composes a
  conclusion sentence.
- `DiagnosticHypothesisEngine.ContainsCausalLanguage` backs
  `DiagnosticHypothesisEngineTests.Output_never_contains_causal_language`, which rejects the
  substrings `caused` / `caused by` / `is responsible for` / `root cause is` / `proves` (and
  more) anywhere in generated output.
- `StutterDiag.Reporting` adds no inference of its own: it formats `DiagnosticFinding`s,
  `StutterCorrelation`s and counts, routes every conclusion-shaped string through the
  observation/hypothesis framing, and every rendered page carries the literal sentence
  **"Correlation does not prove causation."**
- The A/B session comparison (`SessionComparer`) is explicitly descriptive: it lists how two
  runs differ and carries a header stating it "does not attribute any difference to the TPM, a
  driver, or any other component."
