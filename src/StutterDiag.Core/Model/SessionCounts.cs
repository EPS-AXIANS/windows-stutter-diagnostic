namespace StutterDiag.Core.Model;

/// <summary>
/// Cheap per-session aggregate counts for list / comparison views, computed with GROUP BY
/// scalar queries rather than by loading rows. <see cref="DpcSpikes"/> counts DPC/ISR windows
/// whose peak reached <see cref="DpcSpikeThresholdMs"/>.
/// </summary>
public sealed record SessionCounts(
    long SessionId,
    int Stutters,
    int MajorStutters,
    int TpmEvents,
    int WheaEvents,
    int DpcSpikes)
{
    public const double DpcSpikeThresholdMs = 2.0;

    public static SessionCounts Empty(long sessionId) => new(sessionId, 0, 0, 0, 0, 0);
}
