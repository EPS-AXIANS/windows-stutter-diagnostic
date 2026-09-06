namespace StutterDiag.Reporting;

/// <summary>
/// Resolves a <see cref="Core.Model.ReportRequest.OutputPath"/> (which may name a file or a
/// directory) to concrete output paths for each generator.
/// </summary>
internal static class ReportPaths
{
    /// <summary>File output: use the path as given, or <paramref name="defaultName"/> inside it if it is a directory.</summary>
    public static string ResolveFile(string outputPath, string defaultName)
    {
        string full = Require(outputPath);
        return Directory.Exists(full) || EndsWithSeparator(outputPath)
            ? Path.Combine(full, defaultName)
            : full;
    }

    /// <summary>
    /// CSV set: returns the directory to write the sibling files into and the path for the
    /// primary <c>stutters.csv</c> (the given file when a file was supplied, else inside the dir).
    /// </summary>
    public static (string Directory, string Primary) ResolveCsvSet(string outputPath)
    {
        string full = Require(outputPath);
        if (Directory.Exists(full) || EndsWithSeparator(outputPath))
            return (full, Path.Combine(full, "stutters.csv"));

        string dir = Path.GetDirectoryName(full) ?? System.IO.Directory.GetCurrentDirectory();
        return (dir, full);
    }

    /// <summary>Zip output: a supplied <c>.zip</c> path as-is, a directory gets <c>report.zip</c>, otherwise append <c>.zip</c>.</summary>
    public static string ResolveZip(string outputPath)
    {
        string full = Require(outputPath);
        if (Directory.Exists(full) || EndsWithSeparator(outputPath))
            return Path.Combine(full, "report.zip");
        return full.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) ? full : full + ".zip";
    }

    private static string Require(string outputPath)
    {
        if (string.IsNullOrWhiteSpace(outputPath))
            throw new ArgumentException("ReportRequest.OutputPath is required.", nameof(outputPath));
        return Path.GetFullPath(outputPath);
    }

    private static bool EndsWithSeparator(string p)
        => p.Length > 0 && (p[^1] == Path.DirectorySeparatorChar || p[^1] == Path.AltDirectorySeparatorChar);
}
