namespace StutterDiag.Core.Model;

/// <summary>
/// Best-effort TPM description. Windows does not expose "firmware vs discrete" directly,
/// so <see cref="InferredType"/> is a heuristic and <see cref="InferenceBasis"/> always
/// states, in plain language, what that heuristic was based on. Any field that could not
/// be read is left null / <see cref="TpmType.Undetermined"/> — never guessed.
/// </summary>
public sealed record TpmInfo
{
    public TpmType InferredType { get; init; } = TpmType.Undetermined;

    /// <summary>Human-readable justification for <see cref="InferredType"/>. Always populated.</summary>
    public string InferenceBasis { get; init; } = "No TPM information was available.";

    public bool Present { get; init; }
    public string? SpecVersion { get; init; }             // e.g. "2.0, 0, 1.38"
    public string? ManufacturerId { get; init; }          // raw ("AMD", "IFX ", numeric)
    public string? ManufacturerName { get; init; }        // decoded ("AMD", "Infineon", "Intel", ...)
    public string? ManufacturerVersion { get; init; }
    public string? InterfaceType { get; init; }           // "TIS" | "CRB" | null
    public string? PhysicalPresenceVersion { get; init; }
    public bool? IsEnabled { get; init; }
    public bool? IsActivated { get; init; }
    public bool? IsOwned { get; init; }

    /// <summary>Where the data came from: "Win32_Tpm+TBS", "TBS", "unavailable".</summary>
    public string Source { get; init; } = "unavailable";

    public IReadOnlyList<string> Notes { get; init; } = Array.Empty<string>();

    public static TpmInfo NotDetected(string basis) => new()
    {
        InferredType = TpmType.NotDetected,
        InferenceBasis = basis,
        Present = false,
        Source = "unavailable"
    };
}
