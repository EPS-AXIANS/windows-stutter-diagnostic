namespace StutterDiag.Core.Model;

/// <summary>
/// A single point-in-time observation from any monitor. Ordering and correlation always
/// use <see cref="TimestampQpc"/> (the one monotonic axis); <see cref="TimestampUtc"/> is
/// for display and cross-session comparison only.
/// </summary>
public sealed record MonitorEvent
{
    /// <summary>QueryPerformanceCounter ticks on the shared axis (see <c>QpcClock</c>).</summary>
    public long TimestampQpc { get; init; }

    /// <summary>Wall-clock UTC, derived from <see cref="TimestampQpc"/> via the QPC anchor.</summary>
    public DateTime TimestampUtc { get; init; }

    public EventCategory Category { get; init; }

    /// <summary>Short monitor / subsystem name, e.g. "Heartbeat", "Etw.Dpc", "EventLog".</summary>
    public string Source { get; init; } = "";

    /// <summary>ETW or Event Log provider name, when the event originated from one.</summary>
    public string? Provider { get; init; }

    public int? EventId { get; init; }

    public EventSeverity Severity { get; init; } = EventSeverity.Info;

    public string Message { get; init; } = "";

    /// <summary>Raw event XML when available (Event Log records, some ETW). May be redacted on export.</summary>
    public string? RawXml { get; init; }

    /// <summary>Structured key/value payload (ETW fields, Event Log EventData).</summary>
    public IReadOnlyDictionary<string, string>? Data { get; init; }

    public MonitorEvent WithData(string key, string value)
    {
        var d = Data is null
            ? new Dictionary<string, string>()
            : new Dictionary<string, string>(Data);
        d[key] = value;
        return this with { Data = d };
    }
}
