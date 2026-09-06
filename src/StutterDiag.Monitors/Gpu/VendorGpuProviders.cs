using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace StutterDiag.Monitors.Gpu;

/// <summary>GPU silicon vendor a <see cref="IGpuVendorProvider"/> targets.</summary>
public enum GpuVendor
{
    Nvidia,
    Amd,
    Intel
}

/// <summary>
/// One instantaneous vendor-SDK reading. Every field is nullable: a provider reports only
/// what its SDK actually returned on this call and leaves the rest null — never a placeholder.
/// </summary>
public sealed record GpuVendorReading(
    string? AdapterName,
    double? CoreClockMhz,
    double? TemperatureC,
    double? PowerWatts);

/// <summary>
/// Optional adapter over a vendor telemetry library (NVML / ADLX / IGCL), loaded dynamically
/// so the tool has no build- or run-time dependency on any of them. If <see cref="TryLoad"/>
/// returns false the provider stays disabled and its metrics are absent (reported "Unavailable").
/// </summary>
public interface IGpuVendorProvider
{
    /// <summary>Short name for logs/events, e.g. "NVML".</summary>
    string Name { get; }

    GpuVendor Vendor { get; }

    /// <summary>Attempt to load the native library and initialise it. Must not throw; returns false on any failure.</summary>
    bool TryLoad();

    /// <summary>Read current clock/temperature/power for the primary adapter, or null if unavailable this call.</summary>
    GpuVendorReading? Read();
}

/// <summary>
/// NVIDIA Management Library adapter. Binds the stable NVML C ABI at runtime via
/// <see cref="NativeLibrary"/>; no <c>nvml.dll</c> at build time. Exports and enum values are
/// from the public NVML headers and should be re-checked against the installed driver's
/// <c>nvml.h</c> at integration time.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class NvmlGpuVendorProvider : IGpuVendorProvider, IDisposable
{
    private const int NvmlSuccess = 0;
    private const int NvmlClockGraphics = 0;
    private const int NvmlTemperatureGpu = 0;

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int NvmlInitV2();
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int NvmlShutdown();
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int NvmlGetHandleByIndexV2(uint index, out IntPtr device);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int NvmlGetName(IntPtr device, byte[] name, uint length);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int NvmlGetClockInfo(IntPtr device, int type, out uint clockMhz);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int NvmlGetTemperature(IntPtr device, int sensor, out uint tempC);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int NvmlGetPowerUsage(IntPtr device, out uint milliwatts);

    private IntPtr _lib;
    private IntPtr _device;
    private bool _initialised;

    private NvmlShutdown? _shutdown;
    private NvmlGetClockInfo? _getClock;
    private NvmlGetTemperature? _getTemp;
    private NvmlGetPowerUsage? _getPower;
    private string? _adapterName;

    public string Name => "NVML";
    public GpuVendor Vendor => GpuVendor.Nvidia;

    public bool TryLoad()
    {
        if (!OperatingSystem.IsWindows()) return false;
        try
        {
            if (!NativeLibrary.TryLoad("nvml.dll", out _lib)
                && !NativeLibrary.TryLoad("nvml", typeof(NvmlGpuVendorProvider).Assembly, null, out _lib))
                return false;

            var init = GetExport<NvmlInitV2>("nvmlInit_v2");
            _shutdown = GetExport<NvmlShutdown>("nvmlShutdown");
            var getHandle = GetExport<NvmlGetHandleByIndexV2>("nvmlDeviceGetHandleByIndex_v2");
            var getName = GetExport<NvmlGetName>("nvmlDeviceGetName");
            _getClock = GetExport<NvmlGetClockInfo>("nvmlDeviceGetClockInfo");
            _getTemp = GetExport<NvmlGetTemperature>("nvmlDeviceGetTemperature");
            _getPower = GetExport<NvmlGetPowerUsage>("nvmlDeviceGetPowerUsage");

            if (init is null || getHandle is null) return false;
            if (init() != NvmlSuccess) return false;
            _initialised = true;

            if (getHandle(0, out _device) != NvmlSuccess || _device == IntPtr.Zero)
                return false;

            if (getName is not null)
            {
                var buf = new byte[96];
                if (getName(_device, buf, (uint)buf.Length) == NvmlSuccess)
                    _adapterName = System.Text.Encoding.ASCII.GetString(buf).TrimEnd('\0', ' ');
            }
            return true;
        }
        catch
        {
            return false;
        }
    }

    public GpuVendorReading? Read()
    {
        if (!_initialised || _device == IntPtr.Zero) return null;
        try
        {
            double? clock = _getClock is not null && _getClock(_device, NvmlClockGraphics, out uint mhz) == NvmlSuccess ? mhz : null;
            double? temp = _getTemp is not null && _getTemp(_device, NvmlTemperatureGpu, out uint t) == NvmlSuccess ? t : null;
            double? power = _getPower is not null && _getPower(_device, out uint mw) == NvmlSuccess ? mw / 1000.0 : null;
            if (clock is null && temp is null && power is null) return null;
            return new GpuVendorReading(_adapterName, clock, temp, power);
        }
        catch
        {
            return null;
        }
    }

    private T? GetExport<T>(string name) where T : Delegate
    {
        if (NativeLibrary.TryGetExport(_lib, name, out IntPtr p) && p != IntPtr.Zero)
            return Marshal.GetDelegateForFunctionPointer<T>(p);
        return null;
    }

    public void Dispose()
    {
        try { if (_initialised) _shutdown?.Invoke(); } catch { /* ignore */ }
        _initialised = false;
        if (_lib != IntPtr.Zero)
        {
            try { NativeLibrary.Free(_lib); } catch { /* ignore */ }
            _lib = IntPtr.Zero;
        }
    }
}

/// <summary>
/// AMD ADLX adapter placeholder. ADLX is a versioned C++/COM-style interface that cannot be
/// bound by a handful of flat <c>NativeLibrary</c> exports, so this provider intentionally
/// does not activate: clock/temperature/power for AMD GPUs stay "Unavailable" until a proper
/// ADLX binding is added. It still probes for the runtime so the reason can be logged.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class AdlxGpuVendorProvider : IGpuVendorProvider
{
    public string Name => "ADLX";
    public GpuVendor Vendor => GpuVendor.Amd;

    /// <summary>Set after <see cref="TryLoad"/> if the ADLX runtime DLL is present on the machine.</summary>
    public bool RuntimePresent { get; private set; }

    public bool TryLoad()
    {
        if (!OperatingSystem.IsWindows()) return false;
        try
        {
            foreach (var dll in new[] { "amdadlx64.dll", "atiadlxx.dll" })
            {
                if (NativeLibrary.TryLoad(dll, out IntPtr h))
                {
                    RuntimePresent = true;
                    NativeLibrary.Free(h);
                    break;
                }
            }
        }
        catch { /* ignore */ }

        // Not activated: no flat-export binding implemented.
        return false;
    }

    public GpuVendorReading? Read() => null;
}

/// <summary>
/// Intel IGCL (Intel Graphics Control Library) adapter placeholder. Like ADLX, IGCL exposes a
/// dispatch-table API that needs a real binding; until then Intel GPU clock/temperature/power
/// remain "Unavailable". Probes for the loader DLL only.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class IgclGpuVendorProvider : IGpuVendorProvider
{
    public string Name => "IGCL";
    public GpuVendor Vendor => GpuVendor.Intel;

    /// <summary>Set after <see cref="TryLoad"/> if the IGCL loader DLL is present on the machine.</summary>
    public bool RuntimePresent { get; private set; }

    public bool TryLoad()
    {
        if (!OperatingSystem.IsWindows()) return false;
        try
        {
            if (NativeLibrary.TryLoad("ControlLib.dll", out IntPtr h) || NativeLibrary.TryLoad("igcl_dll.dll", out h))
            {
                RuntimePresent = true;
                NativeLibrary.Free(h);
            }
        }
        catch { /* ignore */ }

        return false;
    }

    public GpuVendorReading? Read() => null;
}
