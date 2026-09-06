namespace StutterDiag.Core.Config;

/// <summary>Factory for a fully-populated <see cref="AppConfig"/> (all property defaults are already the intended values).</summary>
public static class AppConfigDefaults
{
    public static AppConfig Create() => new();

    /// <summary>Expand <c>%ProgramData%</c> etc. in the storage paths.</summary>
    public static void ExpandPaths(AppConfig config)
    {
        config.Storage.DatabasePath = Environment.ExpandEnvironmentVariables(config.Storage.DatabasePath);
        config.Storage.LogDirectory = Environment.ExpandEnvironmentVariables(config.Storage.LogDirectory);
    }
}
