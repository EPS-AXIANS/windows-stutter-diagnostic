using StutterDiag.Core.Abstractions;
using StutterDiag.Core.Config;
using StutterDiag.Core.Model;
using StutterDiag.Core.Time;

namespace StutterDiag.Core.Correlation;

/// <summary>
/// Builds the window around a detected stutter and derives purely temporal correlations.
/// It answers "what was Windows doing at that moment?" — never "what caused it".
/// </summary>
public sealed class CorrelationEngine
{
    private readonly QpcClock _clock;
    private readonly CorrelationOptions _opt;
    private readonly ProximityScorer _scorer;

    public CorrelationEngine(QpcClock clock, CorrelationOptions options)
    {
        _clock = clock;
        _opt = options;
        _scorer = new ProximityScorer(options);
    }

    public CorrelationWindow Analyze(
        Stutter stutter,
        ICorrelationDataSource source,
        BaselineStats? baseline = null,
        IBaseRateProvider? baseRates = null)
    {
        baseRates ??= NullBaseRateProvider.Instance;

        long pre = _clock.MsToTicks(_opt.PreRollSeconds * 1000.0);
        long post = _clock.MsToTicks(_opt.PostRollSeconds * 1000.0);
        long from = stutter.TimestampQpc - pre;
        long to = stutter.TimestampQpc + post;
        long stutterEnd = stutter.TimestampQpc + _clock.MsToTicks(stutter.DurationMs);

        var events = source.GetEvents(from, to);
        var samples = source.GetSamples(from, to);
        var dpcIsr = source.GetDpcIsrStats(from, to);
        var snapshot = source.GetNearestSnapshot(stutter.TimestampQpc);

        var best = new Dictionary<string, StutterCorrelation>(StringComparer.Ordinal);

        void Consider(string signalType, double proximityMs, bool anomalous, string detail)
        {
            var score = _scorer.Score(proximityMs, anomalous);
            if (score == CorrelationScore.NoEvidence) return;

            var candidate = new StutterCorrelation(
                stutter.Id, signalType, proximityMs, score,
                baseRates.GetBaseRate(signalType), detail);

            if (!best.TryGetValue(signalType, out var existing)
                || score > existing.Score
                || (score == existing.Score && Math.Abs(proximityMs) < Math.Abs(existing.ProximityMs)))
            {
                best[signalType] = candidate;
            }
        }

        // --- events ---
        foreach (var e in events)
        {
            var signal = SignalCatalog.Classify(e);
            if (signal is null) continue;
            Consider(signal, ProximityMs(e.TimestampQpc, stutter.TimestampQpc, stutterEnd), anomalous: false,
                $"{e.TimestampUtc:HH:mm:ss.fff} {e.Source}"
                + (e.EventId is { } id ? $" (id {id})" : "")
                + $": {Truncate(e.Message, 160)}");
        }

        // --- DPC / ISR per driver ---
        foreach (var group in dpcIsr.GroupBy(s => (s.Driver, s.Kind)))
        {
            var stats = group.ToList();
            double maxMs = stats.Max(s => s.MaxMs);
            long totalCount = stats.Sum(s => s.Count);
            string baseKey = $"{group.Key.Kind.ToLowerInvariant()}.{group.Key.Driver}.max.ms";
            bool anomalous = baseline?.IsHigh(baseKey, maxMs) ?? false;
            if (maxMs < 1.0 && !anomalous) continue;

            var nearest = stats.OrderBy(s => WindowDistanceMs(s, stutter.TimestampQpc)).First();
            string signal = group.Key.Kind == "ISR"
                ? SignalCatalog.Isr(group.Key.Driver)
                : SignalCatalog.Dpc(group.Key.Driver);
            Consider(signal, WindowDistanceMs(nearest, stutter.TimestampQpc), anomalous || maxMs >= 2.0,
                $"{group.Key.Kind} peak {maxMs:F1} ms across {totalCount} calls near the stutter");
        }

        // --- adaptive metric anomalies ---
        if (baseline is not null)
        {
            ConsiderMetric(samples, "disk.read.latency.ms", SignalCatalog.DiskLatency, true, "Disk read latency", baseline, stutter, Consider);
            ConsiderMetric(samples, "disk.write.latency.ms", SignalCatalog.DiskLatency, true, "Disk write latency", baseline, stutter, Consider);
            ConsiderMetric(samples, "disk.queue", SignalCatalog.DiskLatency, true, "Disk queue length", baseline, stutter, Consider);
            ConsiderMetric(samples, "mem.hardfaults.persec", SignalCatalog.HardFault, true, "Hard page fault rate", baseline, stutter, Consider);
            ConsiderMetric(samples, "cpu.freq.effective.mhz", SignalCatalog.CpuFrequencyDrop, false, "CPU effective frequency", baseline, stutter, Consider);
        }

        var correlations = best.Values
            .OrderByDescending(c => c.Score)
            .ThenBy(c => Math.Abs(c.ProximityMs))
            .ToList();

        return new CorrelationWindow(stutter, from, to, events, samples, dpcIsr, snapshot, correlations);
    }

    private void ConsiderMetric(
        IReadOnlyList<MetricSample> samples, string metric, string signalType, bool high, string label,
        BaselineStats baseline, Stutter stutter, Action<string, double, bool, string> consider)
    {
        MetricSample? worst = null;
        foreach (var s in samples)
        {
            if (!string.Equals(s.Metric, metric, StringComparison.Ordinal)) continue;
            bool anomalous = high ? baseline.IsHigh(s.Metric, s.Value) : baseline.IsLow(s.Metric, s.Value);
            if (!anomalous) continue;
            if (worst is null || (high ? s.Value > worst.Value : s.Value < worst.Value))
                worst = s;
        }
        if (worst is null) return;

        double proximityMs = _clock.TicksToMs(worst.TimestampQpc - stutter.TimestampQpc);
        consider(signalType, proximityMs, true,
            $"{label} {worst.Value:F1} ({(high ? "above" : "below")} adaptive baseline)");
    }

    private double ProximityMs(long signalQpc, long stutterStartQpc, long stutterEndQpc)
    {
        if (signalQpc >= stutterStartQpc && signalQpc <= stutterEndQpc) return 0;
        long delta = signalQpc < stutterStartQpc ? signalQpc - stutterStartQpc : signalQpc - stutterEndQpc;
        return _clock.TicksToMs(delta);
    }

    private double WindowDistanceMs(DpcIsrStat s, long stutterQpc)
    {
        if (stutterQpc >= s.WindowStartQpc && stutterQpc <= s.WindowEndQpc) return 0;
        long delta = stutterQpc < s.WindowStartQpc ? s.WindowStartQpc - stutterQpc : stutterQpc - s.WindowEndQpc;
        return _clock.TicksToMs(delta);
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max] + "…";
}
