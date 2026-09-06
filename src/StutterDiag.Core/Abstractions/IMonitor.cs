using StutterDiag.Core.Model;

namespace StutterDiag.Core.Abstractions;

/// <summary>
/// Base contract for every background collector. Implementations must be independent:
/// a failure sets <see cref="Health"/> and emits a <see cref="EventCategory.Health"/>
/// event, but never throws out of <see cref="StartAsync"/> for a missing capability and
/// never affects other monitors.
/// </summary>
public interface IMonitor : IAsyncDisposable
{
    /// <summary>Short stable identifier, e.g. "Cpu", "Etw.Dpc", "EventLog".</summary>
    string Name { get; }

    MonitorHealth Health { get; }

    Task StartAsync(CancellationToken ct);

    Task StopAsync(CancellationToken ct);

    /// <summary>Raised for every discrete observation.</summary>
    event EventHandler<MonitorEvent>? EventCaptured;

    /// <summary>Raised whenever <see cref="Health"/> changes.</summary>
    event EventHandler<MonitorHealth>? HealthChanged;
}

/// <summary>A monitor that also emits numeric time-series samples.</summary>
public interface IMetricSource
{
    event EventHandler<MetricSample>? SampleCaptured;
}
