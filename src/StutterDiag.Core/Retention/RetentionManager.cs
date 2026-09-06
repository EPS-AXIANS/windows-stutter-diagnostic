using StutterDiag.Core.Abstractions;
using StutterDiag.Core.Config;

namespace StutterDiag.Core.Retention;

/// <summary>
/// Enforces the retention policy: drops finished sessions older than <c>Retention.Days</c>,
/// and if the database is still over <c>Retention.MaxDbSizeMb</c> keeps dropping the oldest
/// finished sessions until it is under the cap (or only the current run remains).
/// </summary>
public sealed class RetentionManager
{
    private readonly IEventStore _store;
    private readonly RetentionOptions _options;

    public RetentionManager(IEventStore store, RetentionOptions options)
    {
        _store = store;
        _options = options;
    }

    public async Task<RetentionResult> RunAsync(DateTime utcNow, CancellationToken ct)
    {
        int prunedByAge = await _store.PruneSessionsAsync(utcNow.AddDays(-_options.Days), ct).ConfigureAwait(false);

        int prunedBySize = 0;
        long maxBytes = (long)_options.MaxDbSizeMb * 1024 * 1024;
        while (await _store.GetDatabaseSizeBytesAsync(ct).ConfigureAwait(false) > maxBytes)
        {
            var sessions = await _store.GetSessionsAsync(ct).ConfigureAwait(false);
            var oldestFinished = sessions
                .Where(s => s.EndedUtc is not null)
                .OrderBy(s => s.StartedUtc)
                .FirstOrDefault();
            if (oldestFinished is null) break;

            // Prune everything up to and including that session's end instant.
            int n = await _store.PruneSessionsAsync(oldestFinished.EndedUtc!.Value.AddSeconds(1), ct).ConfigureAwait(false);
            if (n == 0) break;
            prunedBySize += n;
        }

        return new RetentionResult(prunedByAge, prunedBySize);
    }
}

public sealed record RetentionResult(int PrunedByAge, int PrunedBySize)
{
    public int Total => PrunedByAge + PrunedBySize;
}
