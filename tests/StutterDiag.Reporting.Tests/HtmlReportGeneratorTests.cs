using FluentAssertions;
using NSubstitute;
using StutterDiag.Core.Abstractions;
using StutterDiag.Core.Config;
using StutterDiag.Core.Diagnostics;
using StutterDiag.Core.Time;
using StutterDiag.Reporting;
using Xunit;

namespace StutterDiag.Reporting.Tests;

public sealed class HtmlReportGeneratorTests
{
    private static string RenderSample()
    {
        var loader = new ReportDataLoader(Substitute.For<IEventStore>(), new QpcClock(), AppConfigDefaults.Create());
        return new HtmlReportGenerator(loader).Render(TestModel.Build());
    }

    [Fact]
    public void Contains_the_causation_disclaimer_verbatim()
        => RenderSample().Should().Contain("Correlation does not prove causation.");

    [Fact]
    public void Is_fully_self_contained_with_no_external_http_references()
    {
        var html = RenderSample();
        html.Should().NotContain("http://");
        html.Should().NotContain("https://");
    }

    [Fact]
    public void Contains_no_causal_language()
        => DiagnosticHypothesisEngine.ContainsCausalLanguage(RenderSample()).Should().BeFalse();

    [Fact]
    public void Renders_the_diagnostic_section_as_observation_hypothesis_not_proven_and_no_evidence()
    {
        var html = RenderSample();
        html.Should().Contain("OBSERVATION");
        html.Should().Contain("HYPOTHESIS");
        html.Should().Contain("NOT PROVEN");
        html.Should().Contain("NO EVIDENCE"); // the always-reported WHEA signal never co-occurred
    }

    [Fact]
    public void Renders_the_correlation_ranking_line_in_the_canonical_format()
    {
        var html = RenderSample();
        html.Should().MatchRegex(@"1\.\s.*Correlated with\s+\d+/\d+ stutters");
        html.Should().Contain("Correlated with");
    }

    [Fact]
    public void Includes_a_high_scored_tpm_tbs_correlation()
    {
        var html = RenderSample();
        html.Should().Contain("TPM / TBS events");
        html.Should().Contain("[HIGH]");
        html.Should().Contain("badge high");
    }
}
