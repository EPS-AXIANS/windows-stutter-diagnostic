using StutterDiag.Core.Model;
using StutterDiag.Core.Time;

namespace StutterDiag.Monitors.Common;

/// <summary>
/// Boilerplate shared by every <see cref="StutterDiag.Core.Abstractions.IMonitor"/> in this
/// assembly: health tracking, the two required events, and helpers that stamp the QPC axis.
/// A capability failure calls <see cref="SetHealth"/> (which also raises a
/// <see cref="EventCategory.Health"/> event) and returns; it never throws out of
/// <c>StartAsync</c>.
/// </summary>
public abstract class MonitorBase : StutterDiag.Core.Abstractions.IMonitor
{
    private MonitorHealth _health = MonitorHealth.Ok;

    protected MonitorBase(string name, QpcClock clock)
    {
        Name = name;
        Clock = clock;
    }

    /// <summary>Short stable identifier, e.g. "Cpu", "Gpu.DriverEvents", "EventLog".</summary>
    public string Name { get; }

    public MonitorHealth Health => _health;

    protected QpcClock Clock { get; }

    public event EventHandler<MonitorEvent>? EventCaptured;
    public event EventHandler<MonitorHealth>? HealthChanged;

    public abstract Task StartAsync(CancellationToken ct);
    public abstract Task StopAsync(CancellationToken ct);

    /// <summary>Update health; no-op when unchanged. Raises <see cref="HealthChanged"/> and a Health event.</summary>
    protected void SetHealth(MonitorHealth health)
    {
        if (_health == health) return; // record value-equality
        _health = health;

        try { HealthChanged?.Invoke(this, health); } catch { /* subscriber faults never break a monitor */ }

        long qpc = Clock.GetTimestamp();
        RaiseEvent(new MonitorEvent
        {
            TimestampQpc = qpc,
            TimestampUtc = Clock.QpcToUtc(qpc),
            Category = EventCategory.Health,
            Source = Name,
            Severity = health.Status switch
            {
                HealthStatus.Ok => EventSeverity.Info,
                HealthStatus.Degraded => EventSeverity.Warning,
                HealthStatus.Unavailable => EventSeverity.Warning,
                _ => EventSeverity.Error
            },
            Message = $"{Name}: {health}"
        });
    }

    /// <summary>Publish a discrete observation on the unified event stream.</summary>
    protected void RaiseEvent(MonitorEvent e)
    {
        try { EventCaptured?.Invoke(this, e); } catch { /* isolation: a slow/broken sink never faults the monitor */ }
    }

    /// <summary>Build an event on the QPC axis. Pass <paramref name="qpc"/> when the source has its own timestamp.</summary>
    protected MonitorEvent NewEvent(
        EventCategory category,
        string message,
        EventSeverity severity = EventSeverity.Info,
        long? qpc = null,
        string? provider = null,
        int? eventId = null,
        string? rawXml = null,
        IReadOnlyDictionary<string, string>? data = null)
    {
        long t = qpc ?? Clock.GetTimestamp();
        return new MonitorEvent
        {
            TimestampQpc = t,
            TimestampUtc = Clock.QpcToUtc(t),
            Category = category,
            Source = Name,
            Provider = provider,
            EventId = eventId,
            Severity = severity,
            Message = message,
            RawXml = rawXml,
            Data = data
        };
    }

    public virtual ValueTask DisposeAsync()
    {
        GC.SuppressFinalize(this);
        return ValueTask.CompletedTask;
    }
}

/// <summary>A <see cref="MonitorBase"/> that also emits <see cref="MetricSample"/> time-series.</summary>
public abstract class MetricMonitorBase : MonitorBase, StutterDiag.Core.Abstractions.IMetricSource
{
    protected MetricMonitorBase(string name, QpcClock clock) : base(name, clock) { }

    public event EventHandler<MetricSample>? SampleCaptured;

    protected void RaiseSample(MetricSample s)
    {
        try { SampleCaptured?.Invoke(this, s); } catch { /* isolation */ }
    }

    protected void RaiseSample(long qpc, string metric, string instance, double value)
        => RaiseSample(new MetricSample(qpc, metric, instance, value));
}
