namespace StutterDiag.Core.Model;

/// <summary>
/// A decoded Windows Hardware Error Architecture record (from the
/// <c>Microsoft-Windows-WHEA-Logger</c> ETW provider or the matching System log entries).
/// </summary>
public sealed record WheaError(
    long TimestampQpc,
    DateTime TimestampUtc,
    string ErrorSource,   // Processor | Cache | Tlb | Bus | Pcie | Memory | Nmi | MachineCheck | Platform | Unknown
    string Severity,      // Corrected | Recoverable | Fatal | Informational | Unknown
    string Description,
    string? RawXml)
{
    /// <summary>Convert to the unified event stream for storage/correlation.</summary>
    public MonitorEvent ToMonitorEvent() => new()
    {
        TimestampQpc = TimestampQpc,
        TimestampUtc = TimestampUtc,
        Category = EventCategory.Whea,
        Source = "WHEA-Logger",
        Provider = "Microsoft-Windows-WHEA-Logger",
        Severity = Severity is "Fatal" or "Recoverable" ? EventSeverity.Critical : EventSeverity.Warning,
        Message = $"WHEA {Severity} {ErrorSource}: {Description}",
        RawXml = RawXml,
        Data = new Dictionary<string, string>
        {
            ["errorSource"] = ErrorSource,
            ["severity"] = Severity
        }
    };
}
