namespace StutterDiag.Service;

/// <summary>
/// Carries the configuration-validation warnings produced at process start (by
/// <c>ConfigLoader.FromConfiguration</c>) into DI, so the orchestrator can surface them in
/// its status snapshot without re-reading configuration.
/// </summary>
public sealed record StartupWarnings(IReadOnlyList<string> Items)
{
    public static readonly StartupWarnings None = new(Array.Empty<string>());
}
