namespace StutterDiag.Core.Model;

/// <summary>
/// Per-driver DPC or ISR activity aggregated over a time window. Produced by the ETW
/// kernel session; the module name is resolved from the routine address via image loads
/// (<c>"unresolved"</c> when it cannot be mapped).
/// </summary>
public sealed record DpcIsrStat(
    long WindowStartQpc,
    long WindowEndQpc,
    string Driver,
    string Kind,          // "DPC" | "ISR"
    double TotalMs,
    long Count,
    double MaxMs);
