using System.Runtime.Versioning;
using System.Text.RegularExpressions;
using StutterDiag.Core.Abstractions;
using StutterDiag.Core.Config;
using StutterDiag.Core.Model;
using StutterDiag.Core.Time;
using StutterDiag.Monitors.Common;

namespace StutterDiag.Monitors.Gpu;

/// <summary>
/// GPU engine utilisation and VRAM usage from the OS <c>GPU Engine</c> / <c>GPU Adapter Memory</c>
/// PDH counters — no vendor SDK required. Core clock, temperature and board power have no
/// vendor-neutral API (ARCHITECTURE §12); if optional <see cref="IGpuVendorProvider"/> instances
/// are supplied and load, those three metrics are emitted too, otherwise they are simply absent.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class GpuMonitor : MetricMonitorBase, IGpuMonitor
{
    private static readonly Regex EngTypeRx = new(@"engtype_([A-Za-z0-9]+)", RegexOptions.Compiled);
    private static readonly Regex PhysRx = new(@"phys_(\d+)", RegexOptions.Compiled);

    private readonly AppConfig _config;
    private readonly IReadOnlyList<IGpuVendorProvider> _vendorProviders;
    private readonly List<IGpuVendorProvider> _loadedProviders = new();

    private PerfCounterSampler? _sampler;
    private MultiInstanceCounter? _engineUtil;     // GPU Engine \ Utilization Percentage
    private MultiInstanceCounter? _adapterMem;     // GPU Adapter Memory \ Dedicated Usage
    private MultiInstanceCounter? _processMem;     // GPU Process Memory \ Dedicated Usage (fallback)
    private bool _firstTick = true;

    public GpuMonitor(AppConfig config, QpcClock clock, IReadOnlyList<IGpuVendorProvider>? vendorProviders = null)
        : base("Gpu", clock)
    {
        _config = config;
        _vendorProviders = vendorProviders ?? Array.Empty<IGpuVendorProvider>();
    }

    public override Task StartAsync(CancellationToken ct)
    {
        if (!OperatingSystem.IsWindows())
        {
            SetHealth(MonitorHealth.Unavailable("GPU monitor requires Windows performance counters"));
            return Task.CompletedTask;
        }

        try
        {
            _engineUtil = new MultiInstanceCounter("GPU Engine", "Utilization Percentage");
            _adapterMem = new MultiInstanceCounter("GPU Adapter Memory", "Dedicated Usage");
            _processMem = new MultiInstanceCounter("GPU Process Memory", "Dedicated Usage");

            if (!_engineUtil.Available && !_adapterMem.Available)
            {
                SetHealth(MonitorHealth.Unavailable(
                    _engineUtil.Unavailable ?? "GPU Engine / GPU Adapter Memory counters are not present (needs Windows 10 1709+)"));
                return Task.CompletedTask;
            }

            LoadVendorProviders();

            string? note = null;
            if (!_engineUtil.Available) note = "GPU Engine counter missing; only VRAM is reported";
            else if (!_adapterMem.Available) note = "GPU Adapter Memory counter missing; VRAM falls back to per-process sum";
            if (_loadedProviders.Count == 0 && AnyVendorSdkEnabled())
                note = Join(note, "no vendor GPU provider loaded: clock/temperature/power are unavailable");

            SetHealth(note is null ? MonitorHealth.Ok : MonitorHealth.Degraded(note));

            _sampler = new PerfCounterSampler(_config.Sampling.PerfCounterHz);
            _sampler.OnTick(Sample);
            _sampler.Start();
        }
        catch (Exception ex)
        {
            SetHealth(MonitorHealth.Failed(ex.Message));
        }
        return Task.CompletedTask;
    }

    public override async Task StopAsync(CancellationToken ct)
    {
        if (_sampler is not null) await _sampler.DisposeAsync().ConfigureAwait(false);
        _sampler = null;

        foreach (var p in _loadedProviders)
        {
            try { (p as IDisposable)?.Dispose(); } catch { /* ignore */ }
        }
        _loadedProviders.Clear();

        _engineUtil?.Dispose(); _adapterMem?.Dispose(); _processMem?.Dispose();
        _engineUtil = _adapterMem = _processMem = null;
    }

    private bool AnyVendorSdkEnabled()
        => _config.GpuVendorSdks.Nvml || _config.GpuVendorSdks.Adlx || _config.GpuVendorSdks.Igcl;

    private void LoadVendorProviders()
    {
        foreach (var p in _vendorProviders)
        {
            bool enabled = p.Vendor switch
            {
                GpuVendor.Nvidia => _config.GpuVendorSdks.Nvml,
                GpuVendor.Amd => _config.GpuVendorSdks.Adlx,
                GpuVendor.Intel => _config.GpuVendorSdks.Igcl,
                _ => false
            };
            if (!enabled) continue;

            try
            {
                if (p.TryLoad())
                {
                    _loadedProviders.Add(p);
                    RaiseEvent(NewEvent(EventCategory.Gpu, $"Vendor GPU provider loaded: {p.Name}.", EventSeverity.Info));
                }
            }
            catch (Exception ex)
            {
                RaiseEvent(NewEvent(EventCategory.Gpu, $"Vendor GPU provider {p.Name} failed to load ({ex.GetType().Name}).", EventSeverity.Info));
            }
        }
    }

    private void Sample()
    {
        long qpc = Clock.GetTimestamp();

        if (_firstTick)
        {
            _engineUtil?.Read();
            _adapterMem?.Read();
            _processMem?.Read();
            _firstTick = false;
            return;
        }

        EmitEngineUtilisation(qpc);
        EmitVram(qpc);
        EmitVendorMetrics(qpc);
    }

    private void EmitEngineUtilisation(long qpc)
    {
        if (_engineUtil is null) return;

        // Instances are per (pid, adapter, engine); collapse to one series per engine type.
        var byEngType = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        foreach (var r in _engineUtil.Read())
        {
            var m = EngTypeRx.Match(r.Instance);
            string engType = m.Success ? m.Groups[1].Value : "Unknown";
            byEngType.TryGetValue(engType, out double acc);
            byEngType[engType] = acc + r.Value;
        }

        double total = 0;
        foreach (var (engType, pct) in byEngType)
        {
            double clamped = Math.Min(pct, 100.0);
            RaiseSample(qpc, "gpu.engine.pct", engType, clamped);
            total = Math.Max(total, clamped); // busiest engine ~ overall GPU busy
        }
        if (byEngType.Count > 0) RaiseSample(qpc, "gpu.engine.pct", string.Empty, total);
    }

    private void EmitVram(long qpc)
    {
        if (_adapterMem is { Available: true })
        {
            var byPhys = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
            foreach (var r in _adapterMem.Read())
            {
                string phys = PhysId(r.Instance);
                byPhys.TryGetValue(phys, out double acc);
                byPhys[phys] = acc + r.Value;
            }
            foreach (var (phys, bytes) in byPhys)
                RaiseSample(qpc, "gpu.vram.usedmb", phys, bytes / (1024.0 * 1024.0));
            return;
        }

        if (_processMem is { Available: true })
        {
            var byPhys = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
            foreach (var r in _processMem.Read())
            {
                string phys = PhysId(r.Instance);
                byPhys.TryGetValue(phys, out double acc);
                byPhys[phys] = acc + r.Value;
            }
            foreach (var (phys, bytes) in byPhys)
                RaiseSample(qpc, "gpu.vram.usedmb", phys, bytes / (1024.0 * 1024.0));
        }
    }

    private void EmitVendorMetrics(long qpc)
    {
        foreach (var p in _loadedProviders)
        {
            GpuVendorReading? reading;
            try { reading = p.Read(); }
            catch { reading = null; }
            if (reading is null) continue;

            string instance = reading.AdapterName ?? p.Name;
            if (reading.CoreClockMhz is { } mhz) RaiseSample(qpc, "gpu.freq.mhz", instance, mhz);
            if (reading.TemperatureC is { } t) RaiseSample(qpc, "gpu.temp.c", instance, t);
            if (reading.PowerWatts is { } w) RaiseSample(qpc, "gpu.power.w", instance, w);
        }
    }

    private static string PhysId(string instance)
    {
        var m = PhysRx.Match(instance);
        return m.Success ? $"phys_{m.Groups[1].Value}" : instance;
    }

    private static string Join(string? a, string b) => string.IsNullOrEmpty(a) ? b : $"{a}; {b}";

    public override async ValueTask DisposeAsync()
    {
        await StopAsync(CancellationToken.None).ConfigureAwait(false);
        await base.DisposeAsync().ConfigureAwait(false);
    }
}
