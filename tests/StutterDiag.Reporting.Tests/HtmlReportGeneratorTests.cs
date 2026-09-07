using FluentAssertions;
using NSubstitute;
using StutterDiag.Core.Abstractions;
using StutterDiag.Core.Config;
using StutterDiag.Core.Diagnostics;
using StutterDiag.Core.Model;
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

    // Driver names, WHEA descriptions and monitor notes all come off the measured machine, and
    // the finished report is meant to be sent to someone else. The template escapes them today;
    // these tests keep it that way. TestModel leaves Drivers empty, so this is also the only
    // coverage of the drivers table.
    private const string Payload = "<script>alert('xss')</script>";

    private static string RenderWithHostileMachineData()
    {
        var session = TestModel.Session() with
        {
            Drivers = new[]
            {
                new DriverInfo(Payload, Payload, new DateTime(2026, 1, 2, 0, 0, 0, DateTimeKind.Utc), Payload, Payload, Payload),
            },
            Whea = new[]
            {
                new WheaRow(DateTime.UnixEpoch, 1_050_000_000, Payload, Payload, Payload, null),
            },
            Health = new HealthSummary(
                new[] { new MonitorHealthRow(Payload, Payload, Payload) }, 0, Payload),
        };

        var loader = new ReportDataLoader(Substitute.For<IEventStore>(), new QpcClock(), AppConfigDefaults.Create());
        return new HtmlReportGenerator(loader).Render(TestModel.Build() with { Primary = session });
    }

    [Fact]
    public void Escapes_machine_data_so_the_report_cannot_carry_injected_markup()
    {
        var html = RenderWithHostileMachineData();

        // The payload must never reach the document as live markup...
        html.Should().NotContain("<script>alert");
        html.Should().NotContain("</script>alert");
        // ...it has to appear escaped instead.
        html.Should().Contain("&lt;script&gt;alert");
    }

    [Fact]
    public void Renders_the_drivers_table_when_drivers_are_present()
    {
        RenderWithHostileMachineData().Should().Contain("Drivers (1)");
    }
}
