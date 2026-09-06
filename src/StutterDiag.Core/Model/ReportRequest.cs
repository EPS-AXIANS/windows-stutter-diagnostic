namespace StutterDiag.Core.Model;

/// <summary>What the user chooses to include in an export (nothing is sent anywhere).</summary>
public sealed record RedactionOptions
{
    public bool IncludeProcessNames { get; init; } = true;
    public bool IncludeUserNames { get; init; } = false;
    public bool IncludeCommandLines { get; init; } = false;
    public bool IncludeRawEventXml { get; init; } = true;
    public bool IncludeFullEventLog { get; init; } = true;
}

/// <summary>Parameters for a report generation call.</summary>
public sealed record ReportRequest
{
    /// <summary>Output file for html/json/csv; output directory (or .zip path) for zip.</summary>
    public required string OutputPath { get; init; }

    /// <summary>One session for a normal report; exactly two for <see cref="CompareMode"/>.</summary>
    public required IReadOnlyList<long> SessionIds { get; init; }

    public ReportFormat Format { get; init; }

    public bool CompareMode { get; init; }

    public RedactionOptions Redaction { get; init; } = new();
}
