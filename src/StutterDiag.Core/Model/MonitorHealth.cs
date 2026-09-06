namespace StutterDiag.Core.Model;

/// <summary>
/// Health of a monitor. A monitor that loses a capability (e.g. no admin for kernel ETW)
/// reports <see cref="HealthStatus.Unavailable"/> or <see cref="HealthStatus.Degraded"/>
/// instead of throwing, so the orchestrator keeps running everything else.
/// </summary>
public sealed record MonitorHealth(HealthStatus Status, string? Note = null)
{
    public static readonly MonitorHealth Ok = new(HealthStatus.Ok);

    public static MonitorHealth Unavailable(string why) => new(HealthStatus.Unavailable, why);

    public static MonitorHealth Degraded(string why) => new(HealthStatus.Degraded, why);

    public static MonitorHealth Failed(string why) => new(HealthStatus.Failed, why);

    public bool IsUsable => Status is HealthStatus.Ok or HealthStatus.Degraded;

    public override string ToString() => Note is null ? Status.ToString() : $"{Status} ({Note})";
}
