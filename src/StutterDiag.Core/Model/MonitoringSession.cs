namespace StutterDiag.Core.Model;

/// <summary>
/// One continuous monitoring run. The <see cref="TpmTypeInferred"/> / <see cref="TpmBasis"/>
/// snapshot is copied in so an A/B comparison (e.g. fTPM run vs dTPM run) can label each side.
/// </summary>
public sealed record MonitoringSession
{
    public long Id { get; init; }
    public DateTime StartedUtc { get; init; }
    public DateTime? EndedUtc { get; init; }
    public string Label { get; init; } = "";
    public TpmType TpmTypeInferred { get; init; }
    public string TpmBasis { get; init; } = "";

    /// <summary>Stable, non-identifying hash of hardware ids — lets the UI group runs from the same box.</summary>
    public string MachineFingerprint { get; init; } = "";

    public string ConfigJson { get; init; } = "{}";
    public string Mode { get; init; } = "Standard";   // "Standard" | "Gaming"

    public TimeSpan Duration =>
        (EndedUtc ?? DateTime.UtcNow) - StartedUtc;
}
