using System.Text.Json;
using FluentAssertions;
using StutterDiag.Core.Model;
using StutterDiag.Reporting;
using Xunit;

namespace StutterDiag.Reporting.Tests;

public sealed class JsonReportGeneratorTests
{
    [Fact]
    public void Serialize_produces_parseable_json()
    {
        var json = JsonReportGenerator.Serialize(TestModel.Build(ReportFormat.Json));

        var act = () => JsonDocument.Parse(json);
        act.Should().NotThrow();
    }

    [Fact]
    public void Enums_are_written_as_camelCase_strings()
    {
        var json = JsonReportGenerator.Serialize(TestModel.Build(ReportFormat.Json));
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        var format = root.GetProperty("meta").GetProperty("format");
        format.ValueKind.Should().Be(JsonValueKind.String);
        format.GetString().Should().Be("json");

        var score = root.GetProperty("primary").GetProperty("correlationRanking")[0].GetProperty("score");
        score.ValueKind.Should().Be(JsonValueKind.String);
        score.GetString().Should().Be("high");
    }

    [Fact]
    public void An_infinite_lift_from_a_zero_base_rate_serialises_as_the_string_Infinity_without_throwing()
    {
        string json = "";
        var act = () => json = JsonReportGenerator.Serialize(TestModel.Build(ReportFormat.Json));

        act.Should().NotThrow();
        json.Should().Contain("\"Infinity\"");

        using var doc = JsonDocument.Parse(json);
        var findings = doc.RootElement.GetProperty("primary").GetProperty("findings");
        findings.EnumerateArray()
            .Any(f => f.GetProperty("lift").ValueKind == JsonValueKind.String
                      && f.GetProperty("lift").GetString() == "Infinity")
            .Should().BeTrue();
    }

    [Fact]
    public void The_document_exposes_the_expected_top_level_shape()
    {
        var json = JsonReportGenerator.Serialize(TestModel.Build(ReportFormat.Json));
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        root.TryGetProperty("meta", out _).Should().BeTrue();
        root.GetProperty("primary").GetProperty("session").GetProperty("label").GetString().Should().Be("fTPM run");
        root.GetProperty("primary").GetProperty("stutters").GetArrayLength().Should().Be(2);
        root.GetProperty("primary").GetProperty("findings").GetArrayLength().Should().BeGreaterThan(0);
        root.GetProperty("secondary").ValueKind.Should().Be(JsonValueKind.Null);
    }
}
