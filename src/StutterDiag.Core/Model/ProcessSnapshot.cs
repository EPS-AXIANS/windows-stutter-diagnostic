namespace StutterDiag.Core.Model;

/// <summary>One process row inside a <see cref="ProcessSnapshot"/>.</summary>
public sealed record ProcessSnapshotRow(
    int Pid,
    string Name,
    double CpuPercent,
    long WorkingSetBytes,
    long PrivateBytes,
    long ReadBytesPerSec,
    long WriteBytesPerSec,
    int ThreadCount,
    int HandleCount,
    long PageFaultsDelta,
    double KernelTimeMs,
    double UserTimeMs,
    bool RecentlyStarted,
    bool ActivitySpike);

/// <summary>
/// A point-in-time capture of the active processes and their resource use. Taken
/// periodically at low resolution and at high resolution immediately around a stutter.
/// </summary>
public sealed record ProcessSnapshot(
    long TimestampQpc,
    DateTime TimestampUtc,
    SnapshotTrigger Trigger,
    IReadOnlyList<ProcessSnapshotRow> Rows);
