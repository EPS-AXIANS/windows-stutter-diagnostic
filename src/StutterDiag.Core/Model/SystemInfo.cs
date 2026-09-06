namespace StutterDiag.Core.Model;

/// <summary>A signed driver present on the system (from <c>Win32_PnPSignedDriver</c>).</summary>
public sealed record DriverInfo(
    string Name,
    string? Version,
    DateTime? Date,
    string? Vendor,
    string? DeviceClass,
    string? Path);

/// <summary>
/// One-shot description of the machine for the System page and the report header.
/// Every value is a string so "Unavailable" / "Not available on this Windows configuration"
/// can be stored verbatim wherever a real value cannot be obtained.
/// </summary>
public sealed record SystemInfo
{
    public IReadOnlyDictionary<string, string> Windows { get; init; } = Empty;
    public IReadOnlyDictionary<string, string> Cpu { get; init; } = Empty;
    public IReadOnlyList<IReadOnlyDictionary<string, string>> Gpus { get; init; } =
        Array.Empty<IReadOnlyDictionary<string, string>>();
    public IReadOnlyDictionary<string, string> Memory { get; init; } = Empty;
    public IReadOnlyDictionary<string, string> Motherboard { get; init; } = Empty;
    public IReadOnlyDictionary<string, string> Bios { get; init; } = Empty;
    public TpmInfo Tpm { get; init; } = new();

    /// <summary>Secure Boot, VBS, HVCI / Memory Integrity, Hyper-V, Core Isolation.</summary>
    public IReadOnlyDictionary<string, string> Security { get; init; } = Empty;

    public IReadOnlyList<DriverInfo> Drivers { get; init; } = Array.Empty<DriverInfo>();

    public const string Unavailable = "Unavailable";
    public const string NotOnThisConfig = "Not available on this Windows configuration";

    private static readonly IReadOnlyDictionary<string, string> Empty =
        new Dictionary<string, string>();
}
