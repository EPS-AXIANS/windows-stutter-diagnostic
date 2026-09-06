using System.Text.Json;

namespace StutterDiag.Gui.Models;

/// <summary>
/// A tolerant projection of the <c>SystemInfo</c> JSON returned by
/// <c>IStutterDiagControl.GetSystemInfoJsonAsync</c>. Everything is parsed defensively:
/// missing sections become empty, and any value that is absent is surfaced as
/// <c>"Unavailable"</c> (never invented) per docs/ARCHITECTURE.md §12.
/// </summary>
public sealed class SystemInfoView
{
    public IReadOnlyList<KeyValueRow> Windows { get; init; } = Array.Empty<KeyValueRow>();
    public IReadOnlyList<KeyValueRow> Cpu { get; init; } = Array.Empty<KeyValueRow>();
    public IReadOnlyList<IReadOnlyList<KeyValueRow>> Gpus { get; init; } = Array.Empty<IReadOnlyList<KeyValueRow>>();
    public IReadOnlyList<KeyValueRow> Memory { get; init; } = Array.Empty<KeyValueRow>();
    public IReadOnlyList<KeyValueRow> Motherboard { get; init; } = Array.Empty<KeyValueRow>();
    public IReadOnlyList<KeyValueRow> Bios { get; init; } = Array.Empty<KeyValueRow>();
    public IReadOnlyList<KeyValueRow> Security { get; init; } = Array.Empty<KeyValueRow>();
    public IReadOnlyList<DriverRow> Drivers { get; init; } = Array.Empty<DriverRow>();

    // TPM (shown with an explicit "inferred" label; InferenceBasis verbatim).
    public string TpmInferredType { get; init; } = "Unavailable";
    public string TpmInferenceBasis { get; init; } = "Unavailable";
    public IReadOnlyList<KeyValueRow> TpmDetails { get; init; } = Array.Empty<KeyValueRow>();

    public static SystemInfoView Empty { get; } = new();

    public static SystemInfoView Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return Empty;

        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            var tpm = GetProp(root, "tpm");
            var tpmDetails = new List<KeyValueRow>();
            AddIf(tpmDetails, "Present", Bool(tpm, "present"));
            AddIf(tpmDetails, "Spec version", Str(tpm, "specVersion"));
            AddIf(tpmDetails, "Manufacturer ID", Str(tpm, "manufacturerId"));
            AddIf(tpmDetails, "Manufacturer name", Str(tpm, "manufacturerName"));
            AddIf(tpmDetails, "Manufacturer version", Str(tpm, "manufacturerVersion"));
            AddIf(tpmDetails, "Interface type", Str(tpm, "interfaceType"));
            AddIf(tpmDetails, "Physical presence version", Str(tpm, "physicalPresenceVersion"));
            AddIf(tpmDetails, "Enabled", Bool(tpm, "isEnabled"));
            AddIf(tpmDetails, "Activated", Bool(tpm, "isActivated"));
            AddIf(tpmDetails, "Owned", Bool(tpm, "isOwned"));
            AddIf(tpmDetails, "Source", Str(tpm, "source"));
            foreach (var note in StrList(tpm, "notes"))
                tpmDetails.Add(new KeyValueRow("Note", note));

            return new SystemInfoView
            {
                Windows = Dict(root, "windows"),
                Cpu = Dict(root, "cpu"),
                Gpus = DictList(root, "gpus"),
                Memory = Dict(root, "memory"),
                Motherboard = Dict(root, "motherboard"),
                Bios = Dict(root, "bios"),
                Security = Dict(root, "security"),
                Drivers = Drivers(root, "drivers"),
                TpmInferredType = Str(tpm, "inferredType") is { Length: > 0 } t ? t : "Unavailable",
                TpmInferenceBasis = Str(tpm, "inferenceBasis") is { Length: > 0 } b ? b : "Unavailable",
                TpmDetails = tpmDetails
            };
        }
        catch (JsonException ex)
        {
            Services.GuiLog.Error("SystemInfo JSON parse failed", ex);
            return Empty;
        }
    }

    // ---- helpers ------------------------------------------------------------------------

    private static JsonElement GetProp(JsonElement parent, string name) =>
        parent.ValueKind == JsonValueKind.Object && parent.TryGetProperty(name, out var v)
            ? v
            : default;

    private static IReadOnlyList<KeyValueRow> Dict(JsonElement root, string name)
    {
        var el = GetProp(root, name);
        if (el.ValueKind != JsonValueKind.Object) return Array.Empty<KeyValueRow>();
        var rows = new List<KeyValueRow>();
        foreach (var p in el.EnumerateObject())
            rows.Add(new KeyValueRow(Prettify(p.Name), Scalar(p.Value)));
        return rows;
    }

    private static IReadOnlyList<IReadOnlyList<KeyValueRow>> DictList(JsonElement root, string name)
    {
        var el = GetProp(root, name);
        if (el.ValueKind != JsonValueKind.Array) return Array.Empty<IReadOnlyList<KeyValueRow>>();
        var outer = new List<IReadOnlyList<KeyValueRow>>();
        foreach (var item in el.EnumerateArray())
        {
            var rows = new List<KeyValueRow>();
            if (item.ValueKind == JsonValueKind.Object)
                foreach (var p in item.EnumerateObject())
                    rows.Add(new KeyValueRow(Prettify(p.Name), Scalar(p.Value)));
            outer.Add(rows);
        }
        return outer;
    }

    private static IReadOnlyList<DriverRow> Drivers(JsonElement root, string name)
    {
        var el = GetProp(root, name);
        if (el.ValueKind != JsonValueKind.Array) return Array.Empty<DriverRow>();
        var list = new List<DriverRow>();
        foreach (var d in el.EnumerateArray())
        {
            list.Add(new DriverRow(
                Str(d, "name") ?? "Unavailable",
                Str(d, "version") ?? "Unavailable",
                Str(d, "date") ?? "Unavailable",
                Str(d, "vendor") ?? "Unavailable",
                Str(d, "deviceClass") ?? "Unavailable",
                Str(d, "path") ?? "Unavailable"));
        }
        return list;
    }

    private static string? Str(JsonElement parent, string name)
    {
        var el = GetProp(parent, name);
        return el.ValueKind switch
        {
            JsonValueKind.String => el.GetString(),
            JsonValueKind.Number => el.GetRawText(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            _ => null
        };
    }

    private static string? Bool(JsonElement parent, string name)
    {
        var el = GetProp(parent, name);
        return el.ValueKind switch
        {
            JsonValueKind.True => "Yes",
            JsonValueKind.False => "No",
            JsonValueKind.String => el.GetString(),
            _ => null
        };
    }

    private static IEnumerable<string> StrList(JsonElement parent, string name)
    {
        var el = GetProp(parent, name);
        if (el.ValueKind != JsonValueKind.Array) yield break;
        foreach (var item in el.EnumerateArray())
            if (item.ValueKind == JsonValueKind.String && item.GetString() is { Length: > 0 } s)
                yield return s;
    }

    private static string Scalar(JsonElement el) => el.ValueKind switch
    {
        JsonValueKind.String => el.GetString() ?? "Unavailable",
        JsonValueKind.Number => el.GetRawText(),
        JsonValueKind.True => "Yes",
        JsonValueKind.False => "No",
        JsonValueKind.Null or JsonValueKind.Undefined => "Unavailable",
        _ => el.GetRawText()
    };

    private static void AddIf(List<KeyValueRow> rows, string key, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value)) rows.Add(new KeyValueRow(key, value!));
    }

    private static string Prettify(string camelOrPascal)
    {
        if (string.IsNullOrEmpty(camelOrPascal)) return camelOrPascal;
        var sb = new System.Text.StringBuilder(camelOrPascal.Length + 4);
        for (int i = 0; i < camelOrPascal.Length; i++)
        {
            char c = camelOrPascal[i];
            if (i == 0) { sb.Append(char.ToUpperInvariant(c)); continue; }
            if (char.IsUpper(c) && !char.IsUpper(camelOrPascal[i - 1])) sb.Append(' ');
            sb.Append(c);
        }
        return sb.ToString();
    }
}
