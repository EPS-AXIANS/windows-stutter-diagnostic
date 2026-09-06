using System.Management;
using System.Runtime.Versioning;
using StutterDiag.Core.Abstractions;
using StutterDiag.Core.Config;
using StutterDiag.Core.Model;
using StutterDiag.Core.Time;
using StutterDiag.Monitors.Common;

namespace StutterDiag.Monitors.Cpu;

/// <summary>
/// CPU utilisation, frequency, effective-frequency and idle/parking time from the
/// <c>Processor Information</c> PDH counter set, plus a best-effort ACPI thermal-zone
/// temperature via WMI. Per-core temperature, package power and RAPL are deliberately not
/// attempted: there is no user-mode API for them and this tool does not ship a ring-0 driver
/// (ARCHITECTURE §12).
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class CpuMonitor : MetricMonitorBase, ICpuMonitor
{
    private readonly AppConfig _config;

    private PerfCounterSampler? _sampler;
    private MultiInstanceCounter? _procTime;      // % Processor Time
    private MultiInstanceCounter? _procPerf;      // % Processor Performance (turbo-aware, can exceed 100)
    private MultiInstanceCounter? _procFreq;      // Processor Frequency (MHz, nominal/base)
    private MultiInstanceCounter? _c1;            // % C1 Time
    private MultiInstanceCounter? _c2;            // % C2 Time
    private MultiInstanceCounter? _c3;            // % C3 Time
    private MultiInstanceCounter? _idle;          // % Idle Time
    private MultiInstanceCounter? _parking;       // Parking Status (0 running / 1 parked)

    private bool _firstTick = true;
    private int _tick;
    private bool _tempUnavailableNoted;
    private const int TempEveryNTicks = 10;

    public CpuMonitor(AppConfig config, QpcClock clock) : base("Cpu", clock)
    {
        _config = config;
    }

    public override Task StartAsync(CancellationToken ct)
    {
        if (!OperatingSystem.IsWindows())
        {
            SetHealth(MonitorHealth.Unavailable("CPU monitor requires Windows performance counters"));
            return Task.CompletedTask;
        }

        try
        {
            _procTime = new MultiInstanceCounter("Processor Information", "% Processor Time");
            _procPerf = new MultiInstanceCounter("Processor Information", "% Processor Performance");
            _procFreq = new MultiInstanceCounter("Processor Information", "Processor Frequency");
            _c1 = new MultiInstanceCounter("Processor Information", "% C1 Time");
            _c2 = new MultiInstanceCounter("Processor Information", "% C2 Time");
            _c3 = new MultiInstanceCounter("Processor Information", "% C3 Time");
            _idle = new MultiInstanceCounter("Processor Information", "% Idle Time");
            _parking = new MultiInstanceCounter("Processor Information", "Parking Status");

            if (_procTime.Available is false && _procPerf.Available is false)
            {
                SetHealth(MonitorHealth.Unavailable(
                    _procTime.Unavailable ?? "Processor Information counter set is not present"));
                return Task.CompletedTask;
            }

            var degraded = CollectMissing();
            SetHealth(degraded is null ? MonitorHealth.Ok : MonitorHealth.Degraded(degraded));

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
        DisposeCounters();
    }

    private string? CollectMissing()
    {
        var missing = new List<string>();
        foreach (var (name, c) in new[]
        {
            ("% Processor Performance", _procPerf), ("Processor Frequency", _procFreq),
            ("% C1 Time", _c1), ("% Idle Time", _idle), ("Parking Status", _parking)
        })
        {
            if (c is { Available: false }) missing.Add(name);
        }
        return missing.Count == 0 ? null : $"counters unavailable: {string.Join(", ", missing)}";
    }

    private void Sample()
    {
        long qpc = Clock.GetTimestamp();

        // First read of a rate/average PDH counter always yields 0 — skip it.
        if (_firstTick)
        {
            _procTime?.Read(); _procPerf?.Read(); _procFreq?.Read();
            _c1?.Read(); _c2?.Read(); _c3?.Read(); _idle?.Read(); _parking?.Read();
            _firstTick = false;
            return;
        }

        EmitUtilisation(qpc);
        EmitFrequency(qpc);
        EmitCStateAndParking(qpc);

        if (++_tick % TempEveryNTicks == 0) SampleTemperature(qpc);
    }

    private void EmitUtilisation(long qpc)
    {
        if (_procTime is null) return;
        foreach (var r in _procTime.Read())
        {
            if (IsTotal(r.Instance)) RaiseSample(qpc, "cpu.total.pct", string.Empty, r.Value);
            else RaiseSample(qpc, "cpu.core.pct", CoreId(r.Instance), r.Value);
        }
    }

    private void EmitFrequency(long qpc)
    {
        // Nominal/base frequency per instance (MHz). Does not change; used as the effective-freq base.
        var baseByInstance = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        if (_procFreq is not null)
        {
            foreach (var r in _procFreq.Read())
            {
                baseByInstance[r.Instance] = r.Value;
                if (IsTotal(r.Instance)) RaiseSample(qpc, "cpu.freq.mhz", string.Empty, r.Value);
                else RaiseSample(qpc, "cpu.freq.mhz", CoreId(r.Instance), r.Value);
            }
        }

        if (_procPerf is null) return;
        foreach (var r in _procPerf.Read())
        {
            // % Processor Performance is relative to the nominal frequency and can exceed 100 under turbo.
            if (!baseByInstance.TryGetValue(r.Instance, out double baseMhz) || baseMhz <= 0) continue;
            double effective = baseMhz * r.Value / 100.0;
            string instance = IsTotal(r.Instance) ? string.Empty : CoreId(r.Instance);
            RaiseSample(qpc, "cpu.freq.effective.mhz", instance, effective);
        }
    }

    private void EmitCStateAndParking(long qpc)
    {
        if (_c1 is not null)
        {
            foreach (var r in _c1.Read())
            {
                string instance = IsTotal(r.Instance) ? string.Empty : CoreId(r.Instance);
                RaiseSample(qpc, "cpu.cstate.c1.pct", instance, r.Value);
            }
        }
        // C2/C3 are read (kept warm, exposed for future metrics) but only C1 is in the metric contract.
        _c2?.Read();
        _c3?.Read();

        if (_idle is not null)
        {
            foreach (var r in _idle.Read())
            {
                if (IsTotal(r.Instance)) RaiseSample(qpc, "cpu.idle.pct", string.Empty, r.Value);
            }
        }

        if (_parking is not null)
        {
            foreach (var r in _parking.Read())
            {
                if (IsTotal(r.Instance)) continue; // parking is per-core only
                RaiseSample(qpc, "cpu.parked", CoreId(r.Instance), r.Value >= 0.5 ? 1.0 : 0.0);
            }
        }
    }

    /// <summary>
    /// ACPI thermal-zone temperature via WMI. This is a platform thermal zone, not the CPU die
    /// (which has no user-mode API), so it is emitted only when a zone exists and labelled as
    /// an ACPI reading. Absence is expected on many machines and is not a health failure.
    /// </summary>
    private void SampleTemperature(long qpc)
    {
        if (_tempUnavailableNoted) return;
        try
        {
            using var searcher = new ManagementObjectSearcher(
                new ManagementScope(@"\\.\root\WMI"),
                new ObjectQuery("SELECT InstanceName, CurrentTemperature FROM MSAcpi_ThermalZoneTemperature"));

            bool any = false;
            foreach (ManagementBaseObject mo in searcher.Get())
            {
                using (mo)
                {
                    object? raw = mo["CurrentTemperature"];
                    if (raw is null) continue;
                    double tenthKelvin = Convert.ToDouble(raw);
                    if (tenthKelvin <= 0) continue;
                    double celsius = tenthKelvin / 10.0 - 273.15;
                    string zone = mo["InstanceName"] as string ?? "ThermalZone";
                    RaiseSample(qpc, "cpu.temp.c", zone, Math.Round(celsius, 2));
                    any = true;
                }
            }

            if (!any)
            {
                _tempUnavailableNoted = true;
                RaiseEvent(NewEvent(EventCategory.Cpu,
                    "CPU/ACPI thermal-zone temperature unavailable (MSAcpi_ThermalZoneTemperature exposes no zone): reporting nothing.",
                    EventSeverity.Info));
            }
        }
        catch (Exception ex)
        {
            _tempUnavailableNoted = true;
            RaiseEvent(NewEvent(EventCategory.Cpu,
                $"CPU/ACPI thermal-zone temperature unavailable ({ex.GetType().Name}): reporting nothing.",
                EventSeverity.Info));
        }
    }

    // "Processor Information" instances look like "0,3" (node,core) with "_Total" and "0,_Total" rollups.
    private static bool IsTotal(string instance)
        => instance.Equals("_Total", StringComparison.OrdinalIgnoreCase)
           || instance.EndsWith(",_Total", StringComparison.OrdinalIgnoreCase);

    private static string CoreId(string instance) => instance; // keep raw "node,core" — stable and unambiguous

    private void DisposeCounters()
    {
        _procTime?.Dispose(); _procPerf?.Dispose(); _procFreq?.Dispose();
        _c1?.Dispose(); _c2?.Dispose(); _c3?.Dispose(); _idle?.Dispose(); _parking?.Dispose();
        _procTime = _procPerf = _procFreq = _c1 = _c2 = _c3 = _idle = _parking = null;
    }

    public override async ValueTask DisposeAsync()
    {
        await StopAsync(CancellationToken.None).ConfigureAwait(false);
        await base.DisposeAsync().ConfigureAwait(false);
    }
}
