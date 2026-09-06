using FluentAssertions;
using StutterDiag.Core.Model;
using StutterDiag.Core.Tpm;
using Xunit;

namespace StutterDiag.Core.Tests;

public sealed class TpmInferenceHeuristicTests
{
    [Theory]
    [InlineData("AMD")]
    [InlineData("INTC")]
    [InlineData("MSFT")]
    public void A_cpu_or_soc_vendor_id_infers_a_firmware_tpm(string vendorId)
    {
        var r = TpmInferenceHeuristic.Infer(
            present: true, manufacturerName: null, manufacturerIdRaw: vendorId,
            interfaceType: null, discreteDeviceOnLpcOrSpiBus: false);

        r.Type.Should().Be(TpmType.Firmware);
        r.Basis.Should().NotBeNullOrWhiteSpace();
    }

    [Theory]
    [InlineData("IFX")]
    [InlineData("STM")]
    [InlineData("NTC")]
    public void A_discrete_package_vendor_id_infers_a_discrete_tpm(string vendorId)
    {
        var r = TpmInferenceHeuristic.Infer(
            present: true, manufacturerName: null, manufacturerIdRaw: vendorId,
            interfaceType: null, discreteDeviceOnLpcOrSpiBus: false);

        r.Type.Should().Be(TpmType.Discrete);
        r.Basis.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public void A_discrete_device_on_the_lpc_or_spi_bus_wins_over_a_firmware_vendor_id()
    {
        var r = TpmInferenceHeuristic.Infer(
            present: true, manufacturerName: "AMD", manufacturerIdRaw: "AMD",
            interfaceType: "CRB", discreteDeviceOnLpcOrSpiBus: true);

        r.Type.Should().Be(TpmType.Discrete);
        r.Basis.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public void An_unknown_vendor_with_no_bus_device_is_undetermined()
    {
        var r = TpmInferenceHeuristic.Infer(
            present: true, manufacturerName: null, manufacturerIdRaw: "ACME",
            interfaceType: "TIS", discreteDeviceOnLpcOrSpiBus: false);

        r.Type.Should().Be(TpmType.Undetermined);
        r.Basis.Should().NotBeNullOrWhiteSpace();
        r.Basis.Should().Contain("TIS");
    }

    [Fact]
    public void Not_present_is_never_undetermined_but_not_detected()
    {
        var r = TpmInferenceHeuristic.Infer(
            present: false, manufacturerName: "AMD", manufacturerIdRaw: "AMD",
            interfaceType: "TIS", discreteDeviceOnLpcOrSpiBus: true);

        r.Type.Should().Be(TpmType.NotDetected);
        r.Basis.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public void Basis_is_always_populated_including_the_ambiguous_null_input_case()
    {
        var r = TpmInferenceHeuristic.Infer(true, null, null, null, null);
        r.Basis.Should().NotBeNullOrWhiteSpace();
        r.Type.Should().Be(TpmType.Undetermined);
    }
}
