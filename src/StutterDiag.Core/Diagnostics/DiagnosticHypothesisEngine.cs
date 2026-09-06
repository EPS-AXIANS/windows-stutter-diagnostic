using StutterDiag.Core.Correlation;
using StutterDiag.Core.Model;

namespace StutterDiag.Core.Diagnostics;

/// <summary>
/// Turns per-stutter <see cref="StutterCorrelation"/>s into a session-level list of
/// <see cref="DiagnosticFinding"/>s. Output is deliberately constrained to
/// observation / hypothesis / not-proven language: no finding may assert causation.
/// <see cref="ContainsCausalLanguage"/> backs the unit test that enforces this.
/// </summary>
public sealed class DiagnosticHypothesisEngine
{
    private static readonly string[] ForbiddenPhrases =
    {
        "caused", "caused by", "is responsible for", "was responsible for",
        "because of the", "due to the tpm", "proves", "confirmed cause", "root cause is"
    };

    public IReadOnlyList<DiagnosticFinding> Analyze(
        IReadOnlyList<Stutter> stutters,
        IReadOnlyList<StutterCorrelation> allCorrelations,
        IBaseRateProvider baseRates,
        IReadOnlyCollection<string>? alwaysReport = null)
    {
        int total = stutters.Count;
        var findings = new List<DiagnosticFinding>();

        var bySignal = allCorrelations
            .GroupBy(c => c.SignalType, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.Ordinal);

        foreach (var (signalType, list) in bySignal)
        {
            int correlated = list.Select(c => c.StutterId).Distinct().Count();
            var maxScore = list.Max(c => c.Score);
            double minProx = list.Min(c => Math.Abs(c.ProximityMs));
            double baseRatePct = baseRates.GetBaseRate(signalType) * 100.0;

            var score = RankFinding(maxScore, total == 0 ? 0 : (double)correlated / total);
            findings.Add(BuildFinding(signalType, score, correlated, total, minProx, baseRatePct));
        }

        // Signals we always want an explicit line for, even when they never co-occurred.
        if (alwaysReport is not null)
        {
            foreach (var signalType in alwaysReport)
            {
                if (bySignal.ContainsKey(signalType)) continue;
                findings.Add(BuildFinding(signalType, CorrelationScore.NoEvidence, 0, total,
                    double.NaN, baseRates.GetBaseRate(signalType) * 100.0));
            }
        }

        return findings
            .OrderByDescending(f => f.Score)
            .ThenByDescending(f => f.CorrelatedStutters)
            .ToList();
    }

    private static CorrelationScore RankFinding(CorrelationScore maxPerStutter, double fraction)
    {
        // The session-level score never exceeds the best single-stutter proximity score,
        // but a signal that only grazed one stutter out of many is demoted.
        if (maxPerStutter == CorrelationScore.NoEvidence) return CorrelationScore.NoEvidence;
        if (fraction < 0.05) return CorrelationScore.Low;
        if (maxPerStutter == CorrelationScore.High && fraction >= 0.15) return CorrelationScore.High;
        if (maxPerStutter >= CorrelationScore.Medium && fraction >= 0.10) return CorrelationScore.Medium;
        return CorrelationScore.Low;
    }

    private static DiagnosticFinding BuildFinding(
        string signalType, CorrelationScore score, int correlated, int total, double minProximityMs, double baseRatePct)
    {
        string name = SignalCatalog.DisplayName(signalType);

        if (score == CorrelationScore.NoEvidence || correlated == 0)
        {
            return new DiagnosticFinding(
                CorrelationScore.NoEvidence, signalType,
                Observation: $"No {name.ToLowerInvariant()} were detected within any stutter window.",
                Hypothesis: "None. This signal shows no temporal association with the recorded stutters.",
                NotProven: "Absence of evidence here is not proof the signal is irrelevant on other systems or workloads.",
                CorrelatedStutters: 0, TotalStutters: total, BaseRatePercent: baseRatePct);
        }

        string proxText = double.IsNaN(minProximityMs)
            ? "within the stutter window"
            : minProximityMs <= 0.5
                ? "overlapping the stutter interval"
                : $"as close as {minProximityMs:F0} ms to a stutter";

        string baseText = baseRatePct > 0
            ? $" The same signal appears in about {baseRatePct:F0}% of random non-stutter windows."
            : "";

        return new DiagnosticFinding(
            score, signalType,
            Observation:
                $"{name} were observed {proxText}, co-occurring with {correlated} of {total} recorded stutters.{baseText}",
            Hypothesis:
                $"{name} may be temporally correlated with a subset of the stutters. Whether the two share an "
                + "underlying trigger, or merely coincide under load, cannot be determined from this data alone.",
            NotProven:
                $"That {name.ToLowerInvariant()} produce the stutters. This tool measures timing coincidence, not mechanism.",
            CorrelatedStutters: correlated, TotalStutters: total, BaseRatePercent: baseRatePct);
    }

    /// <summary>Test hook: true if any forbidden causal phrasing is present (case-insensitive).</summary>
    public static bool ContainsCausalLanguage(string text)
    {
        foreach (var phrase in ForbiddenPhrases)
            if (text.Contains(phrase, StringComparison.OrdinalIgnoreCase))
                return true;
        return false;
    }
}
