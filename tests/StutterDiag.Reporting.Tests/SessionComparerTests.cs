using FluentAssertions;
using StutterDiag.Reporting;
using Xunit;

namespace StutterDiag.Reporting.Tests;

public sealed class SessionComparerTests
{
    private static (SessionReport a, SessionReport b) Pair()
    {
        var a = TestModel.Session("fTPM run");
        var bBase = TestModel.Session("dTPM run");
        var b = bBase with
        {
            Counts = bBase.Counts with { StuttersTotal = 5, TpmTbsEvents = 1, DpcSpikeCount = 6 },
        };
        return (a, b);
    }

    [Fact]
    public void The_first_row_carries_the_descriptive_only_header_note()
    {
        var (a, b) = Pair();
        var cmp = new SessionComparer().Compare(a, b);

        cmp.Rows[0].Metric.Should().Be("Comparison basis");
        cmp.Rows[0].ValueA.Should().Be(SessionComparer.HeaderNote);
        cmp.Rows[0].ValueB.Should().Be(SessionComparer.HeaderNote);
        SessionComparer.HeaderNote.Should().Contain("descriptive only");
    }

    [Fact]
    public void It_emits_a_row_for_every_documented_metric()
    {
        var (a, b) = Pair();
        var metrics = new SessionComparer().Compare(a, b).Rows.Select(r => r.Metric).ToList();

        metrics.Should().Contain(new[]
        {
            "Monitoring duration",
            "Stutters (total)",
            "Stutters per hour",
            "Major or worse stutters",
            "Mean stutter duration",
            "p95 stutter duration",
            "TPM / TBS events",
            "WHEA events",
            "DPC spike count",
            "Distinct driver anomalies",
            "Mean CPU %",
            "Mean GPU %",
            "Storage latency p95",
            "Power-state change count",
        });
        metrics.Should().Contain(m => m.StartsWith("Top correlation signal #"));
    }

    [Fact]
    public void Row_order_is_deterministic()
    {
        var (a, b) = Pair();
        var first = new SessionComparer().Compare(a, b).Rows.Select(r => r.Metric).ToList();
        var second = new SessionComparer().Compare(a, b).Rows.Select(r => r.Metric).ToList();

        second.Should().Equal(first);
    }

    [Fact]
    public void The_delta_column_is_a_plain_signed_difference()
    {
        var (a, b) = Pair();
        var rows = new SessionComparer().Compare(a, b).Rows;

        var stutters = rows.Single(r => r.Metric == "Stutters (total)");
        stutters.ValueA.Should().Be("3");
        stutters.ValueB.Should().Be("5");
        stutters.Delta.Should().Be("+2");

        rows.Single(r => r.Metric == "DPC spike count").Delta.Should().Be("+4");
    }

    [Fact]
    public void Compare_carries_both_sessions_through()
    {
        var (a, b) = Pair();
        var cmp = new SessionComparer().Compare(a, b);

        cmp.A.Label.Should().Be("fTPM run");
        cmp.B.Label.Should().Be("dTPM run");
    }
}
