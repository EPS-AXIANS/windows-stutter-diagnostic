using System.Text.Json;
using System.Text.Json.Serialization;

namespace StutterDiag.Ipc;

/// <summary>Wire envelope for a request. One JSON object per line.</summary>
public sealed record IpcRequest
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N");
    public string Method { get; init; } = "";
    /// <summary>Method arguments as a raw JSON object; shape depends on <see cref="Method"/>.</summary>
    public JsonElement? Params { get; init; }
}

/// <summary>Wire envelope for a response. One JSON object per line.</summary>
public sealed record IpcResponse
{
    public string Id { get; init; } = "";
    public bool Ok { get; init; }
    public JsonElement? Result { get; init; }
    public string? Error { get; init; }

    public static IpcResponse Success(string id, object? result) => new()
    {
        Id = id,
        Ok = true,
        Result = result is null ? null : JsonSerializer.SerializeToElement(result, IpcJson.Options)
    };

    public static IpcResponse Failure(string id, string error) => new() { Id = id, Ok = false, Error = error };
}

/// <summary>Shared JSON options: camelCase, enums as strings, lenient numbers.</summary>
public static class IpcJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
        Converters = { new JsonStringEnumConverter() },
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public static T? Deserialize<T>(JsonElement? element) =>
        element is null ? default : element.Value.Deserialize<T>(Options);

    public static string ToLine(object value) =>
        JsonSerializer.Serialize(value, value.GetType(), Options).ReplaceLineEndings(" ");
}
