using FluentAssertions;
using StutterDiag.Core.Config;
using Xunit;

namespace StutterDiag.Core.Tests;

public sealed class ConfigValidatorTests
{
    [Fact]
    public void A_fully_default_config_produces_no_warnings()
        => ConfigValidator.Validate(AppConfigDefaults.Create()).Should().BeEmpty();

    [Fact]
    public void Out_of_range_values_are_clamped_and_reported()
    {
        var c = AppConfigDefaults.Create();
        c.Correlation.MadK = 999;                 // max 20
        c.Heartbeat.ProbeIntervalMs = 5_000;      // max 100
        c.Sampling.PerfCounterHz = 0.001;         // min 0.2
        c.Retention.Days = 0;                     // min 1

        var warnings = ConfigValidator.Validate(c);

        c.Correlation.MadK.Should().Be(20);
        c.Heartbeat.ProbeIntervalMs.Should().Be(100);
        c.Sampling.PerfCounterHz.Should().Be(0.2);
        c.Retention.Days.Should().Be(1);

        warnings.Should().Contain(w => w.Contains("Correlation.MadK"));
        warnings.Should().Contain(w => w.Contains("Heartbeat.ProbeIntervalMs"));
        warnings.Should().Contain(w => w.Contains("Sampling.PerfCounterHz"));
    }

    [Fact]
    public void Non_increasing_stutter_thresholds_are_reset_to_defaults_with_a_warning()
    {
        var c = AppConfigDefaults.Create();
        c.StutterThresholds.MajorMs = 40;   // now micro(50) is not < major(40)
        c.StutterThresholds.SevereMs = 45;

        var warnings = ConfigValidator.Validate(c);

        warnings.Should().Contain(w => w.Contains("strictly increasing"));
        c.StutterThresholds.MicroStutterMs.Should().Be(50);
        c.StutterThresholds.MajorMs.Should().Be(100);
        c.StutterThresholds.SevereMs.Should().Be(250);
        c.StutterThresholds.CriticalMs.Should().Be(1000);
    }

    [Fact]
    public void An_empty_ipc_pipe_name_is_restored()
    {
        var c = AppConfigDefaults.Create();
        c.Service.IpcPipeName = "   ";

        var warnings = ConfigValidator.Validate(c);

        c.Service.IpcPipeName.Should().Be("StutterDiag.Service");
        warnings.Should().Contain(w => w.Contains("IpcPipeName"));
    }
}
