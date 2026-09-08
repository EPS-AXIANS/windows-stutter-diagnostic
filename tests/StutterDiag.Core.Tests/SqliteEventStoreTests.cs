using FluentAssertions;
using Microsoft.Data.Sqlite;
using StutterDiag.Core.Model;
using StutterDiag.Core.Storage;
using StutterDiag.Core.Time;
using Xunit;

namespace StutterDiag.Core.Tests;

/// <summary>
/// Exercises <see cref="SqliteEventStore"/> against a real temp-file database. The class itself
/// is the fixture: xUnit runs <see cref="InitializeAsync"/> / <see cref="DisposeAsync"/> per test,
/// so every test gets a fresh schema and the temp files are always cleaned up.
/// </summary>
public sealed class SqliteEventStoreTests : IAsyncLifetime
{
    private readonly string _dbPath;
    private readonly QpcClock _clock = new();
    private SqliteEventStore _store = null!;
    private long _sessionId;

    public SqliteEventStoreTests()
    {
        _dbPath = Path.GetTempFileName();
        File.Delete(_dbPath); // hand SQLite a fresh path to create
    }

    public async Task InitializeAsync()
    {
        _store = new SqliteEventStore(_dbPath, _clock);
        await _store.InitializeAsync(default);
        _sessionId = await _store.StartSessionAsync(new MonitoringSession
        {
            StartedUtc = DateTime.UtcNow,
            Label = "unit-test",
            TpmTypeInferred = TpmType.Firmware,
            TpmBasis = "test",
            Mode = "Standard",
        }, default);
    }

    public async Task DisposeAsync()
    {
        await _store.DisposeAsync();
        foreach (var p in new[] { _dbPath, _dbPath + "-wal", _dbPath + "-shm" })
        {
            try { if (File.Exists(p)) File.Delete(p); } catch { /* best effort */ }
        }
    }

    private SqliteConnection OpenRaw()
    {
        var cn = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = _dbPath,
            Mode = SqliteOpenMode.ReadWrite,
        }.ToString());
        cn.Open();
        return cn;
    }

    private long ScalarCount(string sql)
    {
        using var cn = OpenRaw();
        using var cmd = cn.CreateCommand();
        cmd.CommandText = sql;
        return Convert.ToInt64(cmd.ExecuteScalar());
    }

    [Fact]
    public void InitializeAsync_creates_the_schema_and_a_wal_file()
    {
        using (var cn = OpenRaw())
        using (var cmd = cn.CreateCommand())
        {
            cmd.CommandText = "SELECT name FROM sqlite_master WHERE type='table';";
            var tables = new List<string>();
            using var r = cmd.ExecuteReader();
            while (r.Read()) tables.Add(r.GetString(0));

            tables.Should().Contain(new[]
            {
                "sessions", "events", "samples", "dpc_isr_stats", "stutters", "stutter_correlations",
                "process_snapshots", "process_snapshot_rows", "system_info", "drivers", "health",
            });
        }

        File.Exists(_dbPath + "-wal").Should().BeTrue("WAL journalling is enabled");
    }

    [Fact]
    public async Task Round_trips_every_entity_after_a_flush()
    {
        // ---- events with a Data dictionary + RawXml ----
        long q = _clock.GetTimestamp();
        await _store.AppendEventAsync(new MonitorEvent
        {
            TimestampQpc = q,
            TimestampUtc = _clock.QpcToUtc(q),
            Category = EventCategory.Tpm,
            Source = "Tpm",
            Provider = "Microsoft-Windows-TPM-WMI",
            EventId = 1795,
            Severity = EventSeverity.Warning,
            Message = "TPM command",
            RawXml = "<Event><Data>x</Data></Event>",
            Data = new Dictionary<string, string> { ["driver"] = "tpm.sys", ["k"] = "v" },
        });

        // ---- samples ----
        await _store.AppendSampleAsync(new MetricSample(q + 1, "cpu.total.pct", "", 42.5));
        await _store.AppendSampleAsync(new MetricSample(q + 2, "sched.wakedelay.ms", "0", 61.0));

        // ---- dpc / isr stats ----
        await _store.AppendDpcIsrStatAsync(new DpcIsrStat(q, q + 1000, "nvlddmkm.sys", "DPC", 3.0, 12, 2.4));

        await _store.FlushAsync(default);

        var events = await _store.GetEventsAsync(_sessionId, long.MinValue, long.MaxValue, default);
        events.Should().ContainSingle();
        events[0].RawXml.Should().Be("<Event><Data>x</Data></Event>");
        events[0].Data.Should().NotBeNull();
        events[0].Data!["driver"].Should().Be("tpm.sys");
        events[0].EventId.Should().Be(1795);

        var samples = await _store.GetSamplesAsync(_sessionId, long.MinValue, long.MaxValue, default);
        samples.Should().HaveCount(2);
        samples.Should().Contain(s => s.Metric == "sched.wakedelay.ms" && s.Value == 61.0);

        var dpc = await _store.GetDpcIsrStatsAsync(_sessionId, long.MinValue, long.MaxValue, default);
        dpc.Should().ContainSingle();
        dpc[0].Driver.Should().Be("nvlddmkm.sys");
        dpc[0].Kind.Should().Be("DPC");
        dpc[0].Count.Should().Be(12);

        // ---- stutter + correlations ----
        long stutterId = await _store.AddStutterReturningIdAsync(new Stutter
        {
            SessionId = _sessionId,
            TimestampQpc = q + 5,
            TimestampUtc = _clock.QpcToUtc(q + 5),
            DurationMs = 120,
            Severity = StutterSeverity.Major,
            Detector = DetectorKind.Heartbeat,
            Confidence = DetectionConfidence.Medium,
            CorroboratedBy = new[] { DetectorKind.Heartbeat, DetectorKind.Frametime },
        }, default);
        stutterId.Should().BeGreaterThan(0);

        await _store.AddCorrelationsAsync(stutterId, new[]
        {
            new StutterCorrelation(stutterId, "TPM/TBS", 8.0, CorrelationScore.High, 0.10, "overlapping"),
        }, default);

        var stutters = await _store.GetStuttersAsync(_sessionId, default);
        stutters.Should().ContainSingle();
        stutters[0].CorroboratedBy.Should().BeEquivalentTo(new[] { DetectorKind.Heartbeat, DetectorKind.Frametime });

        var correlations = await _store.GetCorrelationsAsync(stutterId, default);
        correlations.Should().ContainSingle();
        correlations[0].SignalType.Should().Be("TPM/TBS");
        correlations[0].Score.Should().Be(CorrelationScore.High);

        // ---- process snapshot with rows ----
        await _store.AddProcessSnapshotAsync(new ProcessSnapshot(
            q + 6, _clock.QpcToUtc(q + 6), SnapshotTrigger.AutoStutter,
            new[]
            {
                new ProcessSnapshotRow(4321, "game.exe", 55.5, 100, 200, 10, 20, 8, 40, 3, 1.5, 2.5, true, true),
            }), default);

        var snaps = await _store.GetProcessSnapshotsAsync(_sessionId, long.MinValue, long.MaxValue, default);
        snaps.Should().ContainSingle();
        snaps[0].Trigger.Should().Be(SnapshotTrigger.AutoStutter);
        snaps[0].Rows.Should().ContainSingle();
        snaps[0].Rows[0].Name.Should().Be("game.exe");
        snaps[0].Rows[0].RecentlyStarted.Should().BeTrue();

        // ---- system info + drivers ----
        await _store.SaveSystemInfoAsync(_sessionId, new SystemInfo
        {
            Windows = new Dictionary<string, string> { ["Edition"] = "Windows 11 Pro" },
            Tpm = new TpmInfo { Present = true, InferredType = TpmType.Firmware, InferenceBasis = "fTPM" },
        }, default);

        await _store.SaveDriversAsync(_sessionId, new[]
        {
            new DriverInfo("nvlddmkm.sys", "31.0.15", new DateTime(2024, 3, 1, 0, 0, 0, DateTimeKind.Utc), "NVIDIA", "Display", @"C:\Windows\System32\drivers\nvlddmkm.sys"),
        }, default);

        var system = await _store.GetSystemInfoAsync(_sessionId, default);
        system.Should().NotBeNull();
        system!.Windows["Edition"].Should().Be("Windows 11 Pro");
        system.Tpm.InferredType.Should().Be(TpmType.Firmware);

        var drivers = await _store.GetDriversAsync(_sessionId, default);
        drivers.Should().ContainSingle();
        drivers[0].Name.Should().Be("nvlddmkm.sys");

        // ---- health ----
        await _store.RecordHealthAsync(_sessionId, "Cpu", MonitorHealth.Degraded("counter unavailable"), default);
        ScalarCount($"SELECT COUNT(*) FROM health WHERE session_id={_sessionId};").Should().Be(1);
    }

    [Fact]
    public async Task GetSessionCountsAsync_aggregates_stutters_tpm_whea_and_dpc_spikes()
    {
        long q = _clock.GetTimestamp();

        await _store.AppendEventAsync(new MonitorEvent { TimestampQpc = q, TimestampUtc = _clock.QpcToUtc(q), Category = EventCategory.Tpm, Message = "tpm" });
        await _store.AppendEventAsync(new MonitorEvent { TimestampQpc = q + 1, TimestampUtc = _clock.QpcToUtc(q + 1), Category = EventCategory.Tbs, Message = "tbs" });
        await _store.AppendEventAsync(new MonitorEvent { TimestampQpc = q + 2, TimestampUtc = _clock.QpcToUtc(q + 2), Category = EventCategory.Whea, Message = "whea" });
        await _store.AppendDpcIsrStatAsync(new DpcIsrStat(q, q + 100, "a.sys", "DPC", 5.0, 3, 3.1)); // spike (>=2)
        await _store.AppendDpcIsrStatAsync(new DpcIsrStat(q, q + 100, "b.sys", "ISR", 0.4, 9, 0.5)); // not a spike
        await _store.FlushAsync(default);

        await _store.AddStutterAsync(new Stutter
        {
            SessionId = _sessionId, TimestampQpc = q + 3, TimestampUtc = _clock.QpcToUtc(q + 3),
            DurationMs = 300, Severity = StutterSeverity.Severe, Detector = DetectorKind.Heartbeat,
        }, default);
        await _store.AddStutterAsync(new Stutter
        {
            SessionId = _sessionId, TimestampQpc = q + 4, TimestampUtc = _clock.QpcToUtc(q + 4),
            DurationMs = 60, Severity = StutterSeverity.Micro, Detector = DetectorKind.Heartbeat,
        }, default);

        var counts = await _store.GetSessionCountsAsync(_sessionId, default);

        counts.Stutters.Should().Be(2);
        counts.MajorStutters.Should().Be(1);   // Severe >= Major
        counts.TpmEvents.Should().Be(2);       // Tpm + Tbs
        counts.WheaEvents.Should().Be(1);
        counts.DpcSpikes.Should().Be(1);
    }

    [Fact]
    public async Task GetDatabaseSizeBytesAsync_is_greater_than_zero()
    {
        await _store.AppendEventAsync(new MonitorEvent { TimestampQpc = 1, Category = EventCategory.Other, Message = "x" });
        await _store.FlushAsync(default);

        (await _store.GetDatabaseSizeBytesAsync(default)).Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task PruneSessionsAsync_deletes_an_ended_old_session_and_its_children()
    {
        // A second, older session that we then close.
        long oldId = await _store.StartSessionAsync(new MonitoringSession
        {
            StartedUtc = DateTime.UtcNow.AddDays(-30),
            Label = "old",
        }, default);

        await _store.AppendEventAsync(new MonitorEvent
        {
            TimestampQpc = 10,
            TimestampUtc = DateTime.UtcNow.AddDays(-30),
            Category = EventCategory.Other,
            Message = "old event",
        });
        await _store.FlushAsync(default);
        ScalarCount($"SELECT COUNT(*) FROM events WHERE session_id={oldId};").Should().Be(1);

        await _store.EndSessionAsync(oldId, DateTime.UtcNow.AddDays(-29), default);

        int affected = await _store.PruneSessionsAsync(DateTime.UtcNow.AddDays(-14), default);
        affected.Should().BeGreaterThan(0);

        var sessions = await _store.GetSessionsAsync(default);
        sessions.Should().NotContain(s => s.Id == oldId);
        sessions.Should().Contain(s => s.Id == _sessionId, "the still-running session is untouched");
        ScalarCount($"SELECT COUNT(*) FROM events WHERE session_id={oldId};").Should().Be(0);
    }

    [Fact]
    public async Task An_open_session_survives_pruning_until_it_is_closed()
    {
        // A run a previous process crashed out of: old, and still ended_utc NULL.
        long crashedId = await _store.StartSessionAsync(new MonitoringSession
        {
            StartedUtc = DateTime.UtcNow.AddDays(-30),
            Label = "crashed",
            TpmTypeInferred = TpmType.Firmware,
            TpmBasis = "test",
            Mode = "Standard",
        }, default);

        // Age-based pruning cannot touch it while it is open -- this is the leak.
        await _store.PruneSessionsAsync(DateTime.UtcNow.AddDays(-14), default);
        (await _store.GetSessionsAsync(default)).Should().Contain(s => s.Id == crashedId,
            "an open session is invisible to retention, so it would accumulate forever");

        int closed = await _store.CloseOpenSessionsAsync(default);
        closed.Should().BeGreaterThanOrEqualTo(1);

        // Now that it is closed at its start instant, the same prune removes it.
        await _store.PruneSessionsAsync(DateTime.UtcNow.AddDays(-14), default);
        (await _store.GetSessionsAsync(default)).Should().NotContain(s => s.Id == crashedId);
    }

    [Fact]
    public async Task CloseOpenSessions_stamps_the_end_at_the_start_and_counts_only_open_ones()
    {
        // Isolate from the fixture's own open session so the count is deterministic.
        await _store.CloseOpenSessionsAsync(default);

        var started = DateTime.UtcNow.AddHours(-2);
        long id = await _store.StartSessionAsync(new MonitoringSession
        {
            StartedUtc = started,
            Label = "crashed",
            TpmTypeInferred = TpmType.Firmware,
            TpmBasis = "test",
            Mode = "Standard",
        }, default);

        int closed = await _store.CloseOpenSessionsAsync(default);
        closed.Should().Be(1, "only the one still-open session should be closed");

        var reread = (await _store.GetSessionsAsync(default)).Single(s => s.Id == id);
        reread.EndedUtc.Should().Be(reread.StartedUtc, "the end is stamped at the start instant");

        // Idempotent: a second pass finds nothing open.
        (await _store.CloseOpenSessionsAsync(default)).Should().Be(0);
    }
}
