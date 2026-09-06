using System.Text.Json;
using Microsoft.Extensions.Configuration;

namespace StutterDiag.Core.Config;

/// <summary>Loads / saves <see cref="AppConfig"/> as JSON, merging with defaults and validating.</summary>
public static class ConfigLoader
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    /// <summary>Bind from an <see cref="IConfiguration"/> (the service's appsettings pipeline).</summary>
    public static (AppConfig Config, IReadOnlyList<string> Warnings) FromConfiguration(IConfiguration configuration)
    {
        var cfg = AppConfigDefaults.Create();
        configuration.GetSection(AppConfig.SectionName).Bind(cfg);
        var warnings = ConfigValidator.Validate(cfg);
        AppConfigDefaults.ExpandPaths(cfg);
        return (cfg, warnings);
    }

    /// <summary>Read a standalone JSON file whose root is the <see cref="AppConfig"/> shape.</summary>
    public static (AppConfig Config, IReadOnlyList<string> Warnings) FromJsonFile(string path)
    {
        if (!File.Exists(path))
        {
            var def = AppConfigDefaults.Create();
            AppConfigDefaults.ExpandPaths(def);
            return (def, new[] { $"Config file '{path}' not found; using defaults." });
        }

        AppConfig cfg;
        try
        {
            cfg = JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(path), JsonOptions)
                  ?? AppConfigDefaults.Create();
        }
        catch (JsonException ex)
        {
            var def = AppConfigDefaults.Create();
            AppConfigDefaults.ExpandPaths(def);
            return (def, new[] { $"Config file '{path}' is invalid ({ex.Message}); using defaults." });
        }

        var warnings = ConfigValidator.Validate(cfg);
        AppConfigDefaults.ExpandPaths(cfg);
        return (cfg, warnings);
    }

    public static void Save(AppConfig config, string path)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        File.WriteAllText(path, JsonSerializer.Serialize(config, JsonOptions));
    }

    public static string ToJson(AppConfig config) => JsonSerializer.Serialize(config, JsonOptions);

    public static AppConfig FromJson(string json) =>
        JsonSerializer.Deserialize<AppConfig>(json, JsonOptions) ?? AppConfigDefaults.Create();
}
