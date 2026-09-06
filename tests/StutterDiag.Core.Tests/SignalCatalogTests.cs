using FluentAssertions;
using StutterDiag.Core.Correlation;
using StutterDiag.Core.Model;
using Xunit;

namespace StutterDiag.Core.Tests;

public sealed class SignalCatalogTests
{
    private static MonitorEvent Ev(EventCategory category, IReadOnlyDictionary<string, string>? data = null)
        => new() { Category = category, Source = "test", Message = "m", Data = data };

    [Theory]
    [InlineData(EventCategory.Tpm, SignalCatalog.TpmTbs)]
    [InlineData(EventCategory.Tbs, SignalCatalog.TpmTbs)]
    [InlineData(EventCategory.Whea, SignalCatalog.Whea)]
    [InlineData(EventCategory.HardwareError, SignalCatalog.HardwareError)]
    [InlineData(EventCategory.DiskLatency, SignalCatalog.DiskLatency)]
    [InlineData(EventCategory.HardFault, SignalCatalog.HardFault)]
    [InlineData(EventCategory.CpuFrequency, SignalCatalog.CpuFrequencyDrop)]
    [InlineData(EventCategory.ThermalThrottle, SignalCatalog.ThermalThrottle)]
    [InlineData(EventCategory.CState, SignalCatalog.CState)]
    [InlineData(EventCategory.PState, SignalCatalog.PowerState)]
    [InlineData(EventCategory.PowerState, SignalCatalog.PowerState)]
    [InlineData(EventCategory.KernelPower, SignalCatalog.KernelPower)]
    [InlineData(EventCategory.GpuDriver, SignalCatalog.GpuDriver)]
    [InlineData(EventCategory.Gpu, SignalCatalog.GpuDriver)]
    [InlineData(EventCategory.DriverLoad, SignalCatalog.DriverLoad)]
    [InlineData(EventCategory.Device, SignalCatalog.DeviceReset)]
    [InlineData(EventCategory.Pnp, SignalCatalog.DeviceReset)]
    [InlineData(EventCategory.Usb, SignalCatalog.DeviceReset)]
    [InlineData(EventCategory.Pcie, SignalCatalog.DeviceReset)]
    [InlineData(EventCategory.Audio, SignalCatalog.AudioGlitch)]
    [InlineData(EventCategory.Network, SignalCatalog.NetworkEvent)]
    [InlineData(EventCategory.Acpi, SignalCatalog.AcpiEvent)]
    public void Classify_maps_each_correlatable_category(EventCategory category, string expected)
        => SignalCatalog.Classify(Ev(category)).Should().Be(expected);

    [Theory]
    [InlineData(EventCategory.Stutter)]
    [InlineData(EventCategory.UserMark)]
    [InlineData(EventCategory.Cpu)]
    [InlineData(EventCategory.Disk)]
    [InlineData(EventCategory.Memory)]
    [InlineData(EventCategory.DeviceGuard)]
    [InlineData(EventCategory.EventLog)]
    [InlineData(EventCategory.SessionLifecycle)]
    [InlineData(EventCategory.Health)]
    [InlineData(EventCategory.Other)]
    public void Classify_returns_null_for_non_correlatable_categories(EventCategory category)
        => SignalCatalog.Classify(Ev(category)).Should().BeNull();

    [Fact]
    public void Dpc_and_isr_take_the_driver_from_the_event_data_or_fall_back_to_unresolved()
    {
        var withDriver = Ev(EventCategory.Dpc, new Dictionary<string, string> { ["driver"] = "nvlddmkm.sys" });
        SignalCatalog.Classify(withDriver).Should().Be("DPC:nvlddmkm.sys");

        SignalCatalog.Classify(Ev(EventCategory.Dpc)).Should().Be("DPC:unresolved");
        SignalCatalog.Classify(Ev(EventCategory.Isr, new Dictionary<string, string> { ["driver"] = "USBXHCI.SYS" }))
            .Should().Be("ISR:USBXHCI.SYS");
    }

    [Fact]
    public void Dpc_and_isr_key_formatting_is_stable()
    {
        SignalCatalog.Dpc("x.sys").Should().Be("DPC:x.sys");
        SignalCatalog.Isr("y.sys").Should().Be("ISR:y.sys");
    }

    [Fact]
    public void DisplayName_maps_known_keys_and_passes_unknown_ones_through()
    {
        SignalCatalog.DisplayName(SignalCatalog.TpmTbs).Should().Be("TPM / TBS events");
        SignalCatalog.DisplayName(SignalCatalog.Whea).Should().Be("WHEA hardware errors");
        SignalCatalog.DisplayName(SignalCatalog.Dpc("nvlddmkm.sys")).Should().Be("DPC in nvlddmkm.sys");
        SignalCatalog.DisplayName(SignalCatalog.Isr("USBXHCI.SYS")).Should().Be("ISR in USBXHCI.SYS");
        SignalCatalog.DisplayName("SomethingNew").Should().Be("SomethingNew");
    }

    [Fact]
    public void Every_named_signal_constant_has_a_non_empty_display_name()
    {
        foreach (var key in new[]
                 {
                     SignalCatalog.TpmTbs, SignalCatalog.Whea, SignalCatalog.HardwareError,
                     SignalCatalog.DiskLatency, SignalCatalog.HardFault, SignalCatalog.CpuFrequencyDrop,
                     SignalCatalog.ThermalThrottle, SignalCatalog.CState, SignalCatalog.PowerState,
                     SignalCatalog.KernelPower, SignalCatalog.GpuDriver, SignalCatalog.GpuPacketGap,
                     SignalCatalog.DeviceReset, SignalCatalog.DriverLoad, SignalCatalog.AudioGlitch,
                     SignalCatalog.NetworkEvent, SignalCatalog.AcpiEvent,
                 })
        {
            SignalCatalog.DisplayName(key).Should().NotBeNullOrWhiteSpace();
        }
    }
}
