using FluentAssertions;
using StutterDiag.Core.Time;
using StutterDiag.Monitors.Tpm;
using Xunit;

namespace StutterDiag.Monitors.Tests;

/// <summary>
/// The firmware-vs-discrete inference itself is covered by
/// <c>StutterDiag.Core.Tests.TpmInferenceHeuristicTests</c>. <see cref="TpmMonitor"/> has no
/// public/internal pure helpers: <c>DecodeManufacturerId</c> (uint -> 4-char ASCII),
/// <c>DecodeManufacturerName</c> and <c>MapInterfaceType</c> are all <c>private</c>, and every
/// field is populated from live WMI (<c>Win32_Tpm</c>, <c>Win32_PnPEntity</c>) or TBS
/// (<c>Tbsi_GetDeviceInfo</c>). Those paths need a real Windows TPM, so they are recorded here as
/// skipped rather than asserted against a CI runner's hardware.
/// </summary>
public sealed class TpmMonitorTests
{
    [Fact]
    public void Construction_does_not_touch_hardware()
    {
        var monitor = new TpmMonitor(new QpcClock());
        monitor.Name.Should().Be("Tpm");
    }

    [Fact(Skip = "requires Windows TPM: exercises live Win32_Tpm + Tbsi_GetDeviceInfo")]
    public void GetTpmInfo_populates_present_spec_and_manufacturer_from_wmi_and_tbs()
    {
    }

    [Fact(Skip = "requires Windows TPM: DecodeManufacturerId is private; needs a public seam to test uint->ASCII")]
    public void ManufacturerId_uint_decodes_to_four_char_ascii()
    {
        // e.g. 0x414D4400 -> "AMD", 0x49465800 -> "IFX"
    }

    [Fact(Skip = "requires Windows: LPC/SPI PnP enumeration via Win32_PnPEntity (SecurityDevices)")]
    public void ProbeDiscreteBus_detects_a_dedicated_tpm_on_the_lpc_or_spi_bus()
    {
    }

    [Fact(Skip = "requires Windows TPM: TPM-WMI / TBS EventLog channel subscriptions")]
    public void StartAsync_subscribes_to_available_tpm_tbs_event_channels()
    {
    }
}
