using FluentAssertions;
using NSubstitute;
using StutterDiag.Core.Abstractions;
using StutterDiag.Core.Config;
using StutterDiag.Core.Model;
using StutterDiag.Core.Retention;
using Xunit;

namespace StutterDiag.Core.Tests;

public sealed class RetentionManagerTests
{
    private static readonly DateTime Now = new(2026, 9, 6, 0, 0, 0, DateTimeKind.Utc);

    private static MonitoringSession Finished(long id, int startedDaysAgo, int endedDaysAgo) => new()
    {
        Id = id,
        StartedUtc = Now.AddDays(-startedDaysAgo),
        EndedUtc = Now.AddDays(-endedDaysAgo),
    };

    private static MonitoringSession Running(long id) => new()
    {
        Id = id,
        StartedUtc = Now.AddDays(-1),
        EndedUtc = null,
    };

    [Fact]
    public async Task Prunes_by_age_first_then_keeps_pruning_the_oldest_finished_session_until_under_the_cap()
    {
        var store = Substitute.For<IEventStore>();
        var options = new RetentionOptions { Days = 14, MaxDbSizeMb = 1 }; // cap = 1 MiB

        store.PruneSessionsAsync(Arg.Any<DateTime>(), Arg.Any<CancellationToken>())
             .Returns(3, 2, 0); // by-age call, then two size-driven calls

        store.GetDatabaseSizeBytesAsync(Arg.Any<CancellationToken>())
             .Returns(5_000_000L, 3_000_000L); // still over the cap twice, loop exits when prune returns 0

        store.GetSessionsAsync(Arg.Any<CancellationToken>())
             .Returns(new List<MonitoringSession> { Finished(1, 40, 39), Running(99) });

        var result = await new RetentionManager(store, options).RunAsync(Now, CancellationToken.None);

        result.PrunedByAge.Should().Be(3);
        result.PrunedBySize.Should().Be(2);
        result.Total.Should().Be(5);
    }

    [Fact]
    public async Task Stops_when_only_the_running_session_remains()
    {
        var store = Substitute.For<IEventStore>();
        var options = new RetentionOptions { Days = 14, MaxDbSizeMb = 1 };

        store.PruneSessionsAsync(Arg.Any<DateTime>(), Arg.Any<CancellationToken>()).Returns(0);
        store.GetDatabaseSizeBytesAsync(Arg.Any<CancellationToken>()).Returns(9_000_000L); // permanently over the cap
        store.GetSessionsAsync(Arg.Any<CancellationToken>())
             .Returns(new List<MonitoringSession> { Running(1) }); // nothing finished to drop

        var result = await new RetentionManager(store, options).RunAsync(Now, CancellationToken.None);

        result.PrunedByAge.Should().Be(0);
        result.PrunedBySize.Should().Be(0);
        // only the by-age prune ran; the size loop bailed out because there was no finished session
        await store.Received(1).PruneSessionsAsync(Arg.Any<DateTime>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task The_by_age_cutoff_is_now_minus_retention_days()
    {
        var store = Substitute.For<IEventStore>();
        var options = new RetentionOptions { Days = 30, MaxDbSizeMb = 1_000_000 }; // huge cap -> size loop never runs

        store.PruneSessionsAsync(Arg.Any<DateTime>(), Arg.Any<CancellationToken>()).Returns(0);
        store.GetDatabaseSizeBytesAsync(Arg.Any<CancellationToken>()).Returns(1L);

        await new RetentionManager(store, options).RunAsync(Now, CancellationToken.None);

        await store.Received().PruneSessionsAsync(Now.AddDays(-30), Arg.Any<CancellationToken>());
    }
}
