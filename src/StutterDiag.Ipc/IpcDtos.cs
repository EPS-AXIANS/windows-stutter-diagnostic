namespace StutterDiag.Ipc;

public sealed record MonitorHealthDto(string Name, string Status, string? Note);

public sealed record StatusDto
{
    public bool Monitoring { get; init; }
    public long SessionId { get; init; }
    public string? StartedUtcIso { get; init; }
    public double MonitoringSeconds { get; init; }
    public string Mode { get; init; } = "Standard";

    public int StuttersDetected { get; init; }
    public int MajorStutters { get; init; }
    public int TpmEvents { get; init; }
    public int WheaEvents { get; init; }
    public int DpcSpikes { get; init; }

    public string? LastEventUtcIso { get; init; }
    public string? LastEventText { get; init; }

    public bool IsElevated { get; init; }
    public IReadOnlyList<MonitorHealthDto> Monitors { get; init; } = Array.Empty<MonitorHealthDto>();

    /// <summary>Non-fatal notes for the UI, e.g. "Kernel ETW unavailable (requires administrator)".</summary>
    public IReadOnlyList<string> Warnings { get; init; } = Array.Empty<string>();
}

public sealed record RecentEventDto(string TimestampUtcIso, string Category, string Severity, string Source, string Message);

public sealed record RecentStutterDto(
    long Id, string TimestampUtcIso, double DurationMs, string Severity, string Detector,
    bool UserMarked, int CorrelationCount, string? TopCorrelation);

public sealed record MarkResultDto(bool Accepted, string TimestampUtcIso, string Message);

public sealed record SessionDto(
    long Id, string StartedUtcIso, string? EndedUtcIso, string Label, string Mode,
    string TpmType, int StutterCount,
    int MajorStutters = 0, int TpmEvents = 0, int WheaEvents = 0, int DpcSpikes = 0);

public sealed record ReportRequestDto
{
    public string Format { get; init; } = "Html";       // Html | Json | Csv | Zip
    public string OutputPath { get; init; } = "";
    public IReadOnlyList<long> SessionIds { get; init; } = Array.Empty<long>();
    public bool CompareMode { get; init; }
    public bool IncludeProcessNames { get; init; } = true;
    public bool IncludeUserNames { get; init; }
    public bool IncludeCommandLines { get; init; }
    public bool IncludeRawEventXml { get; init; } = true;
    public bool IncludeFullEventLog { get; init; } = true;
}

public sealed record TimelineEntryDto(
    string TimestampUtcIso, string Kind, string Label, double? DurationMs, long? StutterId = null);

/// <summary>Everything the GUI needs to render the detail panel for one stutter on the timeline.</summary>
public sealed record StutterWindowDto
{
    public long StutterId { get; init; }
    public string TimestampUtcIso { get; init; } = "";
    public double DurationMs { get; init; }
    public string Severity { get; init; } = "";
    public string Detector { get; init; } = "";
    public bool UserMarked { get; init; }
    public double WindowFromMs { get; init; }   // relative to the stutter, e.g. -5000
    public double WindowToMs { get; init; }     // e.g. +5000

    public IReadOnlyList<StutterCorrelationDto> Correlations { get; init; } = Array.Empty<StutterCorrelationDto>();
    public IReadOnlyList<StutterWindowEventDto> Events { get; init; } = Array.Empty<StutterWindowEventDto>();
    public IReadOnlyList<StutterWindowProcessDto> TopProcesses { get; init; } = Array.Empty<StutterWindowProcessDto>();

    public double? CpuAvgPct { get; init; }
    public double? CpuPeakPct { get; init; }
    public double? GpuAvgPct { get; init; }
    public double? DiskLatencySpikeMs { get; init; }
    public double? DpcPeakMs { get; init; }
    public string? DpcPeakDriver { get; init; }
    public bool WheaInWindow { get; init; }
    public bool KernelPowerInWindow { get; init; }
}

/// <summary>One temporal correlation, already scored (HIGH/MEDIUM/LOW). Never a causal claim.</summary>
public sealed record StutterCorrelationDto(
    string SignalType, string DisplayName, string Score, double ProximityMs, double BaseRatePercent, string Detail);

public sealed record StutterWindowEventDto(
    string TimestampUtcIso, string Category, string Severity, string Source, string Message);

public sealed record StutterWindowProcessDto(string Name, int Pid, double CpuPercent, long WorkingSetBytes);

/// <summary>
/// One line of the automatic diagnostic, surfaced to the Dashboard. Strictly split into
/// observation / hypothesis / not-proven — the tool never asserts causation.
/// </summary>
public sealed record RecentFindingDto(
    string Score, string SignalType, string DisplayName,
    string Observation, string Hypothesis, string NotProven,
    int CorrelatedStutters, int TotalStutters, double BaseRatePercent);
