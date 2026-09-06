using System.Diagnostics;
using System.Runtime.Versioning;

namespace StutterDiag.Monitors.Common;

/// <summary>
/// A single-timer scheduler for PDH performance-counter reads. Each perf-counter monitor
/// registers a tick callback; the sampler fires them all on one <see cref="Timer"/> at
/// <c>Sampling.PerfCounterHz</c>. A callback that throws is caught and swallowed here —
/// individual <see cref="MultiInstanceCounter"/> groups downgrade their own health — so one
/// bad counter never stops the others.
/// </summary>
public sealed class PerfCounterSampler : IAsyncDisposable
{
    private readonly TimeSpan _period;
    private readonly List<Action> _callbacks = new();
    private readonly object _gate = new();
    private Timer? _timer;
    private int _inTick;

    public PerfCounterSampler(double hz)
    {
        double clamped = hz is <= 0 or double.NaN ? 1.0 : Math.Min(hz, 20.0);
        _period = TimeSpan.FromSeconds(1.0 / clamped);
    }

    /// <summary>The interval between ticks (already clamped to a sane range).</summary>
    public TimeSpan Period => _period;

    /// <summary>Register a callback invoked on every tick. Safe to call before or after <see cref="Start"/>.</summary>
    public void OnTick(Action callback)
    {
        lock (_gate) _callbacks.Add(callback);
    }

    public void Start()
    {
        lock (_gate)
        {
            _timer ??= new Timer(_ => Tick(), null, _period, _period);
        }
    }

    private void Tick()
    {
        // Never let two ticks overlap: a slow WMI/PDH read must not pile up threads.
        if (Interlocked.Exchange(ref _inTick, 1) == 1) return;
        try
        {
            Action[] snapshot;
            lock (_gate) snapshot = _callbacks.ToArray();
            foreach (var cb in snapshot)
            {
                try { cb(); }
                catch { /* the counter group owns its own health reporting */ }
            }
        }
        finally
        {
            Interlocked.Exchange(ref _inTick, 0);
        }
    }

    public async ValueTask DisposeAsync()
    {
        Timer? t;
        lock (_gate) { t = _timer; _timer = null; }
        if (t is not null) await t.DisposeAsync().ConfigureAwait(false);
    }
}

/// <summary>One reading from a <see cref="MultiInstanceCounter"/>.</summary>
public readonly record struct CounterReading(string Instance, double Value);

/// <summary>
/// Safe wrapper around a single <c>(category, counter)</c> that may have many instances.
/// Missing category, missing instance, access-denied and instances that vanish between the
/// enumeration and the read are all handled without throwing: the reading is simply absent
/// and <see cref="Unavailable"/> carries the reason.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class MultiInstanceCounter : IDisposable
{
    private readonly string _category;
    private readonly string _counter;
    private readonly Dictionary<string, PerformanceCounter> _counters = new(StringComparer.OrdinalIgnoreCase);
    private PerformanceCounterCategory? _cat;

    public MultiInstanceCounter(string category, string counter)
    {
        _category = category;
        _counter = counter;
        TryInit();
    }

    /// <summary>True once the category and counter were found. May flip to false if a later read faults.</summary>
    public bool Available { get; private set; }

    /// <summary>Human-readable reason the counter is not available, or null.</summary>
    public string? Unavailable { get; private set; }

    private void TryInit()
    {
        try
        {
            if (!OperatingSystem.IsWindows())
            {
                Unavailable = "not running on Windows";
                return;
            }
            if (!PerformanceCounterCategory.Exists(_category))
            {
                Unavailable = $"performance counter category '{_category}' is not present";
                return;
            }
            _cat = new PerformanceCounterCategory(_category);
            if (!PerformanceCounterCategory.CounterExists(_counter, _category))
            {
                Unavailable = $"counter '{_category}\\{_counter}' is not present";
                return;
            }
            Available = true;
        }
        catch (Exception ex)
        {
            Unavailable = ex.Message;
            Available = false;
        }
    }

    /// <summary>
    /// Read every instance. Rate/average counters need two samples, so the very first call
    /// after construction typically yields 0 for those — callers should discard the first tick.
    /// </summary>
    public IReadOnlyList<CounterReading> Read()
    {
        if (!Available || _cat is null) return Array.Empty<CounterReading>();

        var result = new List<CounterReading>();
        try
        {
            string[] instances;
            try { instances = _cat.GetInstanceNames(); }
            catch { instances = Array.Empty<string>(); }

            if (instances.Length == 0)
            {
                // Single-instance category (e.g. "Memory").
                var pc = GetCounter(string.Empty);
                if (pc is not null && TryNext(pc, out double v))
                    result.Add(new CounterReading(string.Empty, v));
            }
            else
            {
                foreach (string inst in instances)
                {
                    var pc = GetCounter(inst);
                    if (pc is not null && TryNext(pc, out double v))
                        result.Add(new CounterReading(inst, v));
                }
            }
        }
        catch (Exception ex)
        {
            Unavailable = ex.Message;
            Available = false;
        }
        return result;
    }

    private static bool TryNext(PerformanceCounter pc, out double value)
    {
        try
        {
            value = pc.NextValue();
            return true;
        }
        catch (InvalidOperationException)
        {
            // Instance disappeared between enumeration and read.
            value = 0;
            return false;
        }
        catch (Exception)
        {
            value = 0;
            return false;
        }
    }

    private PerformanceCounter? GetCounter(string instance)
    {
        if (_counters.TryGetValue(instance, out var existing)) return existing;
        try
        {
            var pc = instance.Length == 0
                ? new PerformanceCounter(_category, _counter, readOnly: true)
                : new PerformanceCounter(_category, _counter, instance, readOnly: true);
            _counters[instance] = pc;
            return pc;
        }
        catch (Exception)
        {
            return null;
        }
    }

    public void Dispose()
    {
        foreach (var pc in _counters.Values)
        {
            try { pc.Dispose(); } catch { /* ignore */ }
        }
        _counters.Clear();
    }
}
