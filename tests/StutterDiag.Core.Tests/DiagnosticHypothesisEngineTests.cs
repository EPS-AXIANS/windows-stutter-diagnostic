using FluentAssertions;
using StutterDiag.Core.Correlation;
using StutterDiag.Core.Diagnostics;
using StutterDiag.Core.Model;
using Xunit;

namespace StutterDiag.Core.Tests;

public sealed class DiagnosticHypothesisEngineTests
{
    private sealed class FakeBaseRates : IBaseRateProvider
    {
        private readonly Dictionary<string, double> _rates;
        public FakeBaseRates(Dictionary<string, double>? rates = null) => _rates = rates ?? new();
        public double GetBaseRate(string signalType) => _rates.TryGetValue(signalType, out var r) ? r : 0.0;
    }

    private static Stutter S(long id) => new() { Id = id, TimestampQpc = id * 1000 };

    private static StutterCorrelation C(long stutterId, string signal, CorrelationScore score, double proximityMs = 5)
        => new(stutterId, signal, proximityMs, score, BaseRate: 0.0, Detail: $"{signal} near stutter {stutterId}");

    [Fact]
    public void Aggregation_counts_distinct_correlated_stutters_across_the_session()
    {
        var stutters = new[] { S(1), S(2), S(3) };
        var correlations = new[]
        {
            C(1, SignalCatalog.TpmTbs, CorrelationScore.High),
            C(2, SignalCatalog.TpmTbs, CorrelationScore.Medium),
            C(2, SignalCatalog.TpmTbs, CorrelationScore.High),   // same stutter, must not double-count
            C(1, SignalCatalog.DiskLatency, CorrelationScore.Low),
        };

        var findings = new DiagnosticHypothesisEngine().Analyze(stutters, correlations, new FakeBaseRates());

        var tpm = findings.Single(f => f.SignalType == SignalCatalog.TpmTbs);
        tpm.CorrelatedStutters.Should().Be(2);
        tpm.TotalStutters.Should().Be(3);
        tpm.Score.Should().Be(CorrelationScore.High); // best per-stutter High, fraction 2/3 >= 0.15

        var disk = findings.Single(f => f.SignalType == SignalCatalog.DiskLatency);
        disk.CorrelatedStutters.Should().Be(1);
    }

    [Fact]
    public void No_evidence_line_is_emitted_for_an_alwaysReport_signal_that_never_co_occurs()
    {
        var stutters = new[] { S(1), S(2) };
        var correlations = new[] { C(1, SignalCatalog.DiskLatency, CorrelationScore.High) };

        var findings = new DiagnosticHypothesisEngine().Analyze(
            stutters, correlations, new FakeBaseRates(),
            alwaysReport: new[] { SignalCatalog.TpmTbs, SignalCatalog.Whea });

        var tpm = findings.Single(f => f.SignalType == SignalCatalog.TpmTbs);
        tpm.Score.Should().Be(CorrelationScore.NoEvidence);
        tpm.CorrelatedStutters.Should().Be(0);
        tpm.TotalStutters.Should().Be(2);
        tpm.Observation.Should().Contain("No").And.Contain("stutter window");

        findings.Should().Contain(f => f.SignalType == SignalCatalog.Whea && f.Score == CorrelationScore.NoEvidence);
    }

    [Fact]
    public void Output_never_contains_causal_language()
    {
        var stutters = Enumerable.Range(1, 10).Select(i => S(i)).ToArray();
        var correlations = new List<StutterCorrelation>
        {
            C(1, SignalCatalog.TpmTbs, CorrelationScore.High, proximityMs: 0),
            C(2, SignalCatalog.TpmTbs, CorrelationScore.Medium, proximityMs: 40),
            C(3, SignalCatalog.Whea, CorrelationScore.Low, proximityMs: 900),
            C(4, SignalCatalog.Dpc("nvlddmkm.sys"), CorrelationScore.High, proximityMs: 3),
            C(5, SignalCatalog.DiskLatency, CorrelationScore.Medium, proximityMs: 120),
            C(6, SignalCatalog.CpuFrequencyDrop, CorrelationScore.Low, proximityMs: 700),
            C(7, SignalCatalog.KernelPower, CorrelationScore.NoEvidence, proximityMs: 5000),
        };

        var findings = new DiagnosticHypothesisEngine().Analyze(
            stutters, correlations, new FakeBaseRates(new() { [SignalCatalog.TpmTbs] = 0.2, [SignalCatalog.Whea] = 0.0 }),
            alwaysReport: new[] { SignalCatalog.TpmTbs, SignalCatalog.Whea, SignalCatalog.HardwareError });

        findings.Should().NotBeEmpty();

        var text = string.Concat(findings.Select(f => f.Observation + "\n" + f.Hypothesis + "\n" + f.NotProven + "\n"));

        DiagnosticHypothesisEngine.ContainsCausalLanguage(text).Should().BeFalse();

        var lower = text.ToLowerInvariant();
        lower.Should().NotContain("caused");
        lower.Should().NotContain("because of the");
        lower.Should().NotContain("proves");
    }

    [Fact]
    public void Findings_are_ordered_by_score_then_by_correlated_count()
    {
        var stutters = Enumerable.Range(1, 20).Select(i => S(i)).ToArray();
        var correlations = new List<StutterCorrelation>();
        for (int i = 1; i <= 10; i++) correlations.Add(C(i, SignalCatalog.Dpc("nvlddmkm.sys"), CorrelationScore.High));
        for (int i = 1; i <= 3; i++) correlations.Add(C(i, SignalCatalog.DiskLatency, CorrelationScore.High));

        var findings = new DiagnosticHypothesisEngine().Analyze(stutters, correlations, new FakeBaseRates());

        findings.Should().BeInDescendingOrder(f => f.Score);
        findings[0].SignalType.Should().Be(SignalCatalog.Dpc("nvlddmkm.sys"));
    }
}
