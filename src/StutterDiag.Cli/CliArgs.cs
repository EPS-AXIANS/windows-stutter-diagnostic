namespace StutterDiag.Cli;

/// <summary>
/// Minimal hand-rolled argument reader: a leading verb, then <c>--key value</c> pairs and
/// bare <c>--flag</c> switches. Deliberately tiny — no external parser dependency.
/// </summary>
internal sealed class CliArgs
{
    private readonly Dictionary<string, string> _options = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _flags = new(StringComparer.OrdinalIgnoreCase);

    private CliArgs(string verb) => Verb = verb;

    public string Verb { get; }

    public static CliArgs Parse(string[] args)
    {
        if (args.Length == 0) return new CliArgs("");

        var parsed = new CliArgs(args[0].ToLowerInvariant());
        for (int i = 1; i < args.Length; i++)
        {
            string a = args[i];
            if (!a.StartsWith("--", StringComparison.Ordinal)) continue;

            string key = a[2..];
            if (i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal))
            {
                parsed._options[key] = args[++i];
            }
            else
            {
                parsed._flags.Add(key);
            }
        }
        return parsed;
    }

    public string? Get(string key) => _options.TryGetValue(key, out var v) ? v : null;

    public string Get(string key, string fallback) => _options.TryGetValue(key, out var v) ? v : fallback;

    public bool Flag(string key) => _flags.Contains(key);

    public IReadOnlyList<long> LongList(string key)
    {
        var raw = Get(key);
        if (string.IsNullOrWhiteSpace(raw)) return Array.Empty<long>();
        var result = new List<long>();
        foreach (var part in raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            if (long.TryParse(part, out var id)) result.Add(id);
        return result;
    }
}
