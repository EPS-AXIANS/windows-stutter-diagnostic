using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using StutterDiag.Core.Abstractions;
using StutterDiag.Core.Model;
using StutterDiag.Core.Time;
using StutterDiag.Core.Util;

namespace StutterDiag.Core.Storage;

/// <summary>
/// SQLite-backed <see cref="IEventStore"/>. WAL journalling, one guarded writer connection,
/// pooled reader connections. High-frequency <c>Append*</c> calls go through an
/// <see cref="AsyncBatcher{T}"/> so no monitor's hot path ever waits on disk.
/// </summary>
public sealed class SqliteEventStore : IEventStore
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    private readonly string _dbPath;
    private readonly string _connectionString;
    private readonly QpcClock _clock;
    private readonly SemaphoreSlim _writeLock = new(1, 1);

    private SqliteConnection? _write;
    private AsyncBatcher<MonitorEvent>? _eventBatcher;
    private AsyncBatcher<MetricSample>? _sampleBatcher;
    private AsyncBatcher<DpcIsrStat>? _dpcBatcher;
    private long _sessionId;

    public SqliteEventStore(string dbPath, QpcClock clock)
    {
        _dbPath = dbPath;
        _clock = clock;
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Private,
            Pooling = true
        }.ToString();
    }

    public async Task InitializeAsync(CancellationToken ct)
    {
        var dir = Path.GetDirectoryName(_dbPath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        _write = new SqliteConnection(_connectionString);
        await _write.OpenAsync(ct).ConfigureAwait(false);
        await ExecAsync(_write, "PRAGMA journal_mode=WAL;", ct).ConfigureAwait(false);
        await ExecAsync(_write, "PRAGMA synchronous=NORMAL;", ct).ConfigureAwait(false);
        await ExecAsync(_write, "PRAGMA busy_timeout=5000;", ct).ConfigureAwait(false);
        await ExecAsync(_write, "PRAGMA foreign_keys=OFF;", ct).ConfigureAwait(false);

        await MigrateAsync(_write, ct).ConfigureAwait(false);

        _eventBatcher = new AsyncBatcher<MonitorEvent>(FlushEventsAsync, batchSize: 512, interval: TimeSpan.FromSeconds(3));
        _sampleBatcher = new AsyncBatcher<MetricSample>(FlushSamplesAsync, batchSize: 512, interval: TimeSpan.FromSeconds(3));
        _dpcBatcher = new AsyncBatcher<DpcIsrStat>(FlushDpcAsync, batchSize: 256, interval: TimeSpan.FromSeconds(5));
    }

    private static async Task MigrateAsync(SqliteConnection cn, CancellationToken ct)
    {
        long version;
        await using (var cmd = cn.CreateCommand())
        {
            cmd.CommandText = "PRAGMA user_version;";
            version = Convert.ToInt64(await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false) ?? 0L);
        }

        for (int v = (int)version; v < SqliteSchema.CurrentVersion; v++)
        {
            await using var tx = (SqliteTransaction)await cn.BeginTransactionAsync(ct).ConfigureAwait(false);
            await using (var cmd = cn.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText = SqliteSchema.Migrations[v];
                await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }
            await using (var cmd = cn.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText = $"PRAGMA user_version={v + 1};";
                await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }
            await tx.CommitAsync(ct).ConfigureAwait(false);
        }
    }

    // ----------------------------------------------------------------- sessions

    public async Task<long> StartSessionAsync(MonitoringSession session, CancellationToken ct)
    {
        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await using var cmd = _write!.CreateCommand();
            cmd.CommandText = """
                INSERT INTO sessions (started_utc, ended_utc, label, tpm_type, tpm_basis, machine_fingerprint, config_json, mode)
                VALUES ($started, $ended, $label, $tpm, $basis, $fp, $cfg, $mode);
                SELECT last_insert_rowid();
                """;
            cmd.Parameters.AddWithValue("$started", Iso(session.StartedUtc));
            cmd.Parameters.AddWithValue("$ended", (object?)(session.EndedUtc is { } e ? Iso(e) : null) ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$label", session.Label);
            cmd.Parameters.AddWithValue("$tpm", (int)session.TpmTypeInferred);
            cmd.Parameters.AddWithValue("$basis", session.TpmBasis);
            cmd.Parameters.AddWithValue("$fp", session.MachineFingerprint);
            cmd.Parameters.AddWithValue("$cfg", session.ConfigJson);
            cmd.Parameters.AddWithValue("$mode", session.Mode);
            _sessionId = Convert.ToInt64(await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false));
            return _sessionId;
        }
        finally { _writeLock.Release(); }
    }

    public async Task EndSessionAsync(long sessionId, DateTime endedUtc, CancellationToken ct)
    {
        await FlushAsync(ct).ConfigureAwait(false);
        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await using var cmd = _write!.CreateCommand();
            cmd.CommandText = "UPDATE sessions SET ended_utc=$ended WHERE id=$id;";
            cmd.Parameters.AddWithValue("$ended", Iso(endedUtc));
            cmd.Parameters.AddWithValue("$id", sessionId);
            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
        finally { _writeLock.Release(); }
    }

    // ----------------------------------------------------------------- hot path

    public ValueTask AppendEventAsync(MonitorEvent e) { _eventBatcher!.Enqueue(e); return ValueTask.CompletedTask; }
    public ValueTask AppendSampleAsync(MetricSample s) { _sampleBatcher!.Enqueue(s); return ValueTask.CompletedTask; }
    public ValueTask AppendDpcIsrStatAsync(DpcIsrStat s) { _dpcBatcher!.Enqueue(s); return ValueTask.CompletedTask; }

    private async Task FlushEventsAsync(IReadOnlyList<MonitorEvent> batch, CancellationToken ct)
    {
        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await using var tx = (SqliteTransaction)await _write!.BeginTransactionAsync(ct).ConfigureAwait(false);
            await using var cmd = _write.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = """
                INSERT INTO events (session_id, ts_qpc, ts_utc, category, source, provider, event_id, severity, message, raw_xml, data_json)
                VALUES ($s,$q,$u,$c,$src,$p,$eid,$sev,$m,$xml,$data);
                """;
            var pS = cmd.Parameters.Add("$s", SqliteType.Integer);
            var pQ = cmd.Parameters.Add("$q", SqliteType.Integer);
            var pU = cmd.Parameters.Add("$u", SqliteType.Text);
            var pC = cmd.Parameters.Add("$c", SqliteType.Integer);
            var pSrc = cmd.Parameters.Add("$src", SqliteType.Text);
            var pP = cmd.Parameters.Add("$p", SqliteType.Text);
            var pEid = cmd.Parameters.Add("$eid", SqliteType.Integer);
            var pSev = cmd.Parameters.Add("$sev", SqliteType.Integer);
            var pM = cmd.Parameters.Add("$m", SqliteType.Text);
            var pXml = cmd.Parameters.Add("$xml", SqliteType.Text);
            var pData = cmd.Parameters.Add("$data", SqliteType.Text);

            foreach (var e in batch)
            {
                pS.Value = _sessionId;
                pQ.Value = e.TimestampQpc;
                pU.Value = Iso(e.TimestampUtc);
                pC.Value = (int)e.Category;
                pSrc.Value = e.Source;
                pP.Value = (object?)e.Provider ?? DBNull.Value;
                pEid.Value = (object?)e.EventId ?? DBNull.Value;
                pSev.Value = (int)e.Severity;
                pM.Value = e.Message;
                pXml.Value = (object?)e.RawXml ?? DBNull.Value;
                pData.Value = e.Data is null ? DBNull.Value : JsonSerializer.Serialize(e.Data, Json);
                await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }
            await tx.CommitAsync(ct).ConfigureAwait(false);
        }
        finally { _writeLock.Release(); }
    }

    private async Task FlushSamplesAsync(IReadOnlyList<MetricSample> batch, CancellationToken ct)
    {
        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await using var tx = (SqliteTransaction)await _write!.BeginTransactionAsync(ct).ConfigureAwait(false);
            await using var cmd = _write.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = "INSERT INTO samples (session_id, ts_qpc, metric, instance, value) VALUES ($s,$q,$m,$i,$v);";
            var pS = cmd.Parameters.Add("$s", SqliteType.Integer);
            var pQ = cmd.Parameters.Add("$q", SqliteType.Integer);
            var pM = cmd.Parameters.Add("$m", SqliteType.Text);
            var pI = cmd.Parameters.Add("$i", SqliteType.Text);
            var pV = cmd.Parameters.Add("$v", SqliteType.Real);
            foreach (var s in batch)
            {
                pS.Value = _sessionId; pQ.Value = s.TimestampQpc; pM.Value = s.Metric;
                pI.Value = s.Instance; pV.Value = s.Value;
                await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }
            await tx.CommitAsync(ct).ConfigureAwait(false);
        }
        finally { _writeLock.Release(); }
    }

    private async Task FlushDpcAsync(IReadOnlyList<DpcIsrStat> batch, CancellationToken ct)
    {
        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await using var tx = (SqliteTransaction)await _write!.BeginTransactionAsync(ct).ConfigureAwait(false);
            await using var cmd = _write.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = """
                INSERT INTO dpc_isr_stats (session_id, window_start_qpc, window_end_qpc, driver, kind, total_ms, count, max_ms)
                VALUES ($s,$ws,$we,$d,$k,$t,$c,$mx);
                """;
            var pS = cmd.Parameters.Add("$s", SqliteType.Integer);
            var pWs = cmd.Parameters.Add("$ws", SqliteType.Integer);
            var pWe = cmd.Parameters.Add("$we", SqliteType.Integer);
            var pD = cmd.Parameters.Add("$d", SqliteType.Text);
            var pK = cmd.Parameters.Add("$k", SqliteType.Text);
            var pT = cmd.Parameters.Add("$t", SqliteType.Real);
            var pC = cmd.Parameters.Add("$c", SqliteType.Integer);
            var pMx = cmd.Parameters.Add("$mx", SqliteType.Real);
            foreach (var s in batch)
            {
                pS.Value = _sessionId; pWs.Value = s.WindowStartQpc; pWe.Value = s.WindowEndQpc;
                pD.Value = s.Driver; pK.Value = s.Kind; pT.Value = s.TotalMs;
                pC.Value = s.Count; pMx.Value = s.MaxMs;
                await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }
            await tx.CommitAsync(ct).ConfigureAwait(false);
        }
        finally { _writeLock.Release(); }
    }

    // ----------------------------------------------------------------- low frequency writes

    public async Task AddStutterAsync(Stutter s, CancellationToken ct)
    {
        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await using var cmd = _write!.CreateCommand();
            cmd.CommandText = """
                INSERT INTO stutters (session_id, ts_qpc, ts_utc, duration_ms, severity, detector, confidence, corroborated_by, user_marked)
                VALUES ($s,$q,$u,$d,$sev,$det,$conf,$corr,$um);
                SELECT last_insert_rowid();
                """;
            cmd.Parameters.AddWithValue("$s", s.SessionId == 0 ? _sessionId : s.SessionId);
            cmd.Parameters.AddWithValue("$q", s.TimestampQpc);
            cmd.Parameters.AddWithValue("$u", Iso(s.TimestampUtc));
            cmd.Parameters.AddWithValue("$d", s.DurationMs);
            cmd.Parameters.AddWithValue("$sev", (int)s.Severity);
            cmd.Parameters.AddWithValue("$det", (int)s.Detector);
            cmd.Parameters.AddWithValue("$conf", (int)s.Confidence);
            cmd.Parameters.AddWithValue("$corr", string.Join(',', s.CorroboratedBy.Select(d => (int)d)));
            cmd.Parameters.AddWithValue("$um", s.UserMarked ? 1 : 0);
            _lastStutterId = Convert.ToInt64(await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false));
        }
        finally { _writeLock.Release(); }
    }

    private long _lastStutterId;

    /// <summary>Insert a stutter and return its new row id (used by the orchestrator to attach correlations).</summary>
    public async Task<long> AddStutterReturningIdAsync(Stutter s, CancellationToken ct)
    {
        await AddStutterAsync(s, ct).ConfigureAwait(false);
        return _lastStutterId;
    }

    public async Task AddCorrelationsAsync(long stutterId, IEnumerable<StutterCorrelation> correlations, CancellationToken ct)
    {
        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await using var tx = (SqliteTransaction)await _write!.BeginTransactionAsync(ct).ConfigureAwait(false);
            await using var cmd = _write.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = """
                INSERT INTO stutter_correlations (stutter_id, signal_type, proximity_ms, score, base_rate, detail)
                VALUES ($sid,$sig,$prox,$score,$br,$detail);
                """;
            var pSid = cmd.Parameters.Add("$sid", SqliteType.Integer);
            var pSig = cmd.Parameters.Add("$sig", SqliteType.Text);
            var pProx = cmd.Parameters.Add("$prox", SqliteType.Real);
            var pScore = cmd.Parameters.Add("$score", SqliteType.Integer);
            var pBr = cmd.Parameters.Add("$br", SqliteType.Real);
            var pDetail = cmd.Parameters.Add("$detail", SqliteType.Text);
            foreach (var c in correlations)
            {
                pSid.Value = stutterId; pSig.Value = c.SignalType; pProx.Value = c.ProximityMs;
                pScore.Value = (int)c.Score; pBr.Value = c.BaseRate; pDetail.Value = c.Detail;
                await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }
            await tx.CommitAsync(ct).ConfigureAwait(false);
        }
        finally { _writeLock.Release(); }
    }

    public async Task AddProcessSnapshotAsync(ProcessSnapshot snapshot, CancellationToken ct)
    {
        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await using var tx = (SqliteTransaction)await _write!.BeginTransactionAsync(ct).ConfigureAwait(false);
            long snapId;
            await using (var cmd = _write.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText = """
                    INSERT INTO process_snapshots (session_id, ts_qpc, ts_utc, trigger) VALUES ($s,$q,$u,$t);
                    SELECT last_insert_rowid();
                    """;
                cmd.Parameters.AddWithValue("$s", _sessionId);
                cmd.Parameters.AddWithValue("$q", snapshot.TimestampQpc);
                cmd.Parameters.AddWithValue("$u", Iso(snapshot.TimestampUtc));
                cmd.Parameters.AddWithValue("$t", (int)snapshot.Trigger);
                snapId = Convert.ToInt64(await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false));
            }
            await using (var cmd = _write.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText = """
                    INSERT INTO process_snapshot_rows
                    (snapshot_id, pid, name, cpu_pct, ws_bytes, priv_bytes, read_bps, write_bps, threads, handles, page_faults_delta, kernel_ms, user_ms, recently_started, activity_spike)
                    VALUES ($id,$pid,$n,$cpu,$ws,$pv,$rb,$wb,$th,$hd,$pf,$km,$um,$rs,$as);
                    """;
                var pId = cmd.Parameters.Add("$id", SqliteType.Integer);
                var pPid = cmd.Parameters.Add("$pid", SqliteType.Integer);
                var pN = cmd.Parameters.Add("$n", SqliteType.Text);
                var pCpu = cmd.Parameters.Add("$cpu", SqliteType.Real);
                var pWs = cmd.Parameters.Add("$ws", SqliteType.Integer);
                var pPv = cmd.Parameters.Add("$pv", SqliteType.Integer);
                var pRb = cmd.Parameters.Add("$rb", SqliteType.Integer);
                var pWb = cmd.Parameters.Add("$wb", SqliteType.Integer);
                var pTh = cmd.Parameters.Add("$th", SqliteType.Integer);
                var pHd = cmd.Parameters.Add("$hd", SqliteType.Integer);
                var pPf = cmd.Parameters.Add("$pf", SqliteType.Integer);
                var pKm = cmd.Parameters.Add("$km", SqliteType.Real);
                var pUm = cmd.Parameters.Add("$um", SqliteType.Real);
                var pRs = cmd.Parameters.Add("$rs", SqliteType.Integer);
                var pAs = cmd.Parameters.Add("$as", SqliteType.Integer);
                foreach (var r in snapshot.Rows)
                {
                    pId.Value = snapId; pPid.Value = r.Pid; pN.Value = r.Name; pCpu.Value = r.CpuPercent;
                    pWs.Value = r.WorkingSetBytes; pPv.Value = r.PrivateBytes; pRb.Value = r.ReadBytesPerSec;
                    pWb.Value = r.WriteBytesPerSec; pTh.Value = r.ThreadCount; pHd.Value = r.HandleCount;
                    pPf.Value = r.PageFaultsDelta; pKm.Value = r.KernelTimeMs; pUm.Value = r.UserTimeMs;
                    pRs.Value = r.RecentlyStarted ? 1 : 0; pAs.Value = r.ActivitySpike ? 1 : 0;
                    await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
                }
            }
            await tx.CommitAsync(ct).ConfigureAwait(false);
        }
        finally { _writeLock.Release(); }
    }

    public async Task SaveSystemInfoAsync(long sessionId, SystemInfo info, CancellationToken ct)
    {
        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await using var cmd = _write!.CreateCommand();
            cmd.CommandText = "INSERT OR REPLACE INTO system_info (session_id, json) VALUES ($s,$j);";
            cmd.Parameters.AddWithValue("$s", sessionId);
            cmd.Parameters.AddWithValue("$j", JsonSerializer.Serialize(info, Json));
            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
        finally { _writeLock.Release(); }
    }

    public async Task SaveDriversAsync(long sessionId, IEnumerable<DriverInfo> drivers, CancellationToken ct)
    {
        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await using var tx = (SqliteTransaction)await _write!.BeginTransactionAsync(ct).ConfigureAwait(false);
            await using var cmd = _write.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = """
                INSERT INTO drivers (session_id, name, version, date, vendor, device_class, path)
                VALUES ($s,$n,$v,$d,$vd,$c,$p);
                """;
            var pS = cmd.Parameters.Add("$s", SqliteType.Integer);
            var pN = cmd.Parameters.Add("$n", SqliteType.Text);
            var pV = cmd.Parameters.Add("$v", SqliteType.Text);
            var pD = cmd.Parameters.Add("$d", SqliteType.Text);
            var pVd = cmd.Parameters.Add("$vd", SqliteType.Text);
            var pC = cmd.Parameters.Add("$c", SqliteType.Text);
            var pP = cmd.Parameters.Add("$p", SqliteType.Text);
            foreach (var d in drivers)
            {
                pS.Value = sessionId; pN.Value = d.Name;
                pV.Value = (object?)d.Version ?? DBNull.Value;
                pD.Value = (object?)(d.Date is { } dt ? Iso(dt) : null) ?? DBNull.Value;
                pVd.Value = (object?)d.Vendor ?? DBNull.Value;
                pC.Value = (object?)d.DeviceClass ?? DBNull.Value;
                pP.Value = (object?)d.Path ?? DBNull.Value;
                await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }
            await tx.CommitAsync(ct).ConfigureAwait(false);
        }
        finally { _writeLock.Release(); }
    }

    public async Task RecordHealthAsync(long sessionId, string monitor, MonitorHealth health, CancellationToken ct)
    {
        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await using var cmd = _write!.CreateCommand();
            cmd.CommandText = "INSERT INTO health (session_id, ts_utc, monitor, status, note) VALUES ($s,$u,$m,$st,$n);";
            cmd.Parameters.AddWithValue("$s", sessionId);
            cmd.Parameters.AddWithValue("$u", Iso(_clock.UtcNow));
            cmd.Parameters.AddWithValue("$m", monitor);
            cmd.Parameters.AddWithValue("$st", (int)health.Status);
            cmd.Parameters.AddWithValue("$n", (object?)health.Note ?? DBNull.Value);
            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
        finally { _writeLock.Release(); }
    }

    public async Task FlushAsync(CancellationToken ct)
    {
        if (_eventBatcher is not null) await _eventBatcher.FlushAsync().ConfigureAwait(false);
        if (_sampleBatcher is not null) await _sampleBatcher.FlushAsync().ConfigureAwait(false);
        if (_dpcBatcher is not null) await _dpcBatcher.FlushAsync().ConfigureAwait(false);

        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await ExecAsync(_write!, "PRAGMA wal_checkpoint(PASSIVE);", ct).ConfigureAwait(false);
        }
        finally { _writeLock.Release(); }
    }

    // ----------------------------------------------------------------- reads

    public async Task<IReadOnlyList<MonitoringSession>> GetSessionsAsync(CancellationToken ct)
    {
        await using var cn = await OpenReaderAsync(ct).ConfigureAwait(false);
        await using var cmd = cn.CreateCommand();
        cmd.CommandText = "SELECT id, started_utc, ended_utc, label, tpm_type, tpm_basis, machine_fingerprint, config_json, mode FROM sessions ORDER BY id DESC;";
        var list = new List<MonitoringSession>();
        await using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await r.ReadAsync(ct).ConfigureAwait(false)) list.Add(ReadSession(r));
        return list;
    }

    public async Task<MonitoringSession?> GetSessionAsync(long sessionId, CancellationToken ct)
    {
        await using var cn = await OpenReaderAsync(ct).ConfigureAwait(false);
        await using var cmd = cn.CreateCommand();
        cmd.CommandText = "SELECT id, started_utc, ended_utc, label, tpm_type, tpm_basis, machine_fingerprint, config_json, mode FROM sessions WHERE id=$id;";
        cmd.Parameters.AddWithValue("$id", sessionId);
        await using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        return await r.ReadAsync(ct).ConfigureAwait(false) ? ReadSession(r) : null;
    }

    public async Task<IReadOnlyList<MonitorEvent>> GetEventsAsync(long sessionId, long fromQpc, long toQpc, CancellationToken ct)
    {
        await using var cn = await OpenReaderAsync(ct).ConfigureAwait(false);
        await using var cmd = cn.CreateCommand();
        cmd.CommandText = """
            SELECT ts_qpc, ts_utc, category, source, provider, event_id, severity, message, raw_xml, data_json
            FROM events WHERE session_id=$s AND ts_qpc BETWEEN $f AND $t ORDER BY ts_qpc;
            """;
        cmd.Parameters.AddWithValue("$s", sessionId);
        cmd.Parameters.AddWithValue("$f", fromQpc);
        cmd.Parameters.AddWithValue("$t", toQpc);
        var list = new List<MonitorEvent>();
        await using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await r.ReadAsync(ct).ConfigureAwait(false))
        {
            var dataJson = r.IsDBNull(9) ? null : r.GetString(9);
            list.Add(new MonitorEvent
            {
                TimestampQpc = r.GetInt64(0),
                TimestampUtc = ParseIso(r.GetString(1)),
                Category = (EventCategory)r.GetInt32(2),
                Source = r.GetString(3),
                Provider = r.IsDBNull(4) ? null : r.GetString(4),
                EventId = r.IsDBNull(5) ? null : r.GetInt32(5),
                Severity = (EventSeverity)r.GetInt32(6),
                Message = r.GetString(7),
                RawXml = r.IsDBNull(8) ? null : r.GetString(8),
                Data = dataJson is null ? null : JsonSerializer.Deserialize<Dictionary<string, string>>(dataJson, Json)
            });
        }
        return list;
    }

    public async Task<IReadOnlyList<MetricSample>> GetSamplesAsync(long sessionId, long fromQpc, long toQpc, CancellationToken ct)
    {
        await using var cn = await OpenReaderAsync(ct).ConfigureAwait(false);
        await using var cmd = cn.CreateCommand();
        cmd.CommandText = "SELECT ts_qpc, metric, instance, value FROM samples WHERE session_id=$s AND ts_qpc BETWEEN $f AND $t ORDER BY ts_qpc;";
        cmd.Parameters.AddWithValue("$s", sessionId);
        cmd.Parameters.AddWithValue("$f", fromQpc);
        cmd.Parameters.AddWithValue("$t", toQpc);
        var list = new List<MetricSample>();
        await using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await r.ReadAsync(ct).ConfigureAwait(false))
            list.Add(new MetricSample(r.GetInt64(0), r.GetString(1), r.GetString(2), r.GetDouble(3)));
        return list;
    }

    public async Task<IReadOnlyList<Stutter>> GetStuttersAsync(long sessionId, CancellationToken ct)
    {
        await using var cn = await OpenReaderAsync(ct).ConfigureAwait(false);
        await using var cmd = cn.CreateCommand();
        cmd.CommandText = """
            SELECT id, ts_qpc, ts_utc, duration_ms, severity, detector, confidence, corroborated_by, user_marked
            FROM stutters WHERE session_id=$s ORDER BY ts_qpc;
            """;
        cmd.Parameters.AddWithValue("$s", sessionId);
        var list = new List<Stutter>();
        await using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await r.ReadAsync(ct).ConfigureAwait(false))
        {
            var corr = r.GetString(7);
            list.Add(new Stutter
            {
                Id = r.GetInt64(0),
                SessionId = sessionId,
                TimestampQpc = r.GetInt64(1),
                TimestampUtc = ParseIso(r.GetString(2)),
                DurationMs = r.GetDouble(3),
                Severity = (StutterSeverity)r.GetInt32(4),
                Detector = (DetectorKind)r.GetInt32(5),
                Confidence = (DetectionConfidence)r.GetInt32(6),
                CorroboratedBy = string.IsNullOrEmpty(corr)
                    ? Array.Empty<DetectorKind>()
                    : corr.Split(',').Select(x => (DetectorKind)int.Parse(x, CultureInfo.InvariantCulture)).ToArray(),
                UserMarked = r.GetInt32(8) != 0
            });
        }
        return list;
    }

    public async Task<SessionCounts> GetSessionCountsAsync(long sessionId, CancellationToken ct)
    {
        await using var cn = await OpenReaderAsync(ct).ConfigureAwait(false);

        async Task<int> ScalarAsync(string sql, params (string Name, object Value)[] args)
        {
            await using var cmd = cn.CreateCommand();
            cmd.CommandText = sql;
            cmd.Parameters.AddWithValue("$s", sessionId);
            foreach (var (n, v) in args) cmd.Parameters.AddWithValue(n, v);
            var o = await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
            return o is null or DBNull ? 0 : Convert.ToInt32(o);
        }

        int stutters = await ScalarAsync("SELECT COUNT(*) FROM stutters WHERE session_id=$s;").ConfigureAwait(false);
        int major = await ScalarAsync(
            "SELECT COUNT(*) FROM stutters WHERE session_id=$s AND severity>=$sev;",
            ("$sev", (int)StutterSeverity.Major)).ConfigureAwait(false);
        int tpm = await ScalarAsync(
            "SELECT COUNT(*) FROM events WHERE session_id=$s AND category IN ($c1,$c2);",
            ("$c1", (int)EventCategory.Tpm), ("$c2", (int)EventCategory.Tbs)).ConfigureAwait(false);
        int whea = await ScalarAsync(
            "SELECT COUNT(*) FROM events WHERE session_id=$s AND category IN ($c1,$c2);",
            ("$c1", (int)EventCategory.Whea), ("$c2", (int)EventCategory.HardwareError)).ConfigureAwait(false);
        int dpcSpikes = await ScalarAsync(
            "SELECT COUNT(*) FROM dpc_isr_stats WHERE session_id=$s AND max_ms>=$t;",
            ("$t", SessionCounts.DpcSpikeThresholdMs)).ConfigureAwait(false);

        return new SessionCounts(sessionId, stutters, major, tpm, whea, dpcSpikes);
    }

    public async Task<IReadOnlyList<StutterCorrelation>> GetCorrelationsAsync(long stutterId, CancellationToken ct)
    {
        await using var cn = await OpenReaderAsync(ct).ConfigureAwait(false);
        await using var cmd = cn.CreateCommand();
        cmd.CommandText = "SELECT signal_type, proximity_ms, score, base_rate, detail FROM stutter_correlations WHERE stutter_id=$id ORDER BY score DESC;";
        cmd.Parameters.AddWithValue("$id", stutterId);
        var list = new List<StutterCorrelation>();
        await using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await r.ReadAsync(ct).ConfigureAwait(false))
            list.Add(new StutterCorrelation(stutterId, r.GetString(0), r.GetDouble(1), (CorrelationScore)r.GetInt32(2), r.GetDouble(3), r.GetString(4)));
        return list;
    }

    public async Task<IReadOnlyList<DpcIsrStat>> GetDpcIsrStatsAsync(long sessionId, long fromQpc, long toQpc, CancellationToken ct)
    {
        await using var cn = await OpenReaderAsync(ct).ConfigureAwait(false);
        await using var cmd = cn.CreateCommand();
        cmd.CommandText = """
            SELECT window_start_qpc, window_end_qpc, driver, kind, total_ms, count, max_ms
            FROM dpc_isr_stats WHERE session_id=$s AND window_start_qpc BETWEEN $f AND $t ORDER BY window_start_qpc;
            """;
        cmd.Parameters.AddWithValue("$s", sessionId);
        cmd.Parameters.AddWithValue("$f", fromQpc);
        cmd.Parameters.AddWithValue("$t", toQpc);
        var list = new List<DpcIsrStat>();
        await using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await r.ReadAsync(ct).ConfigureAwait(false))
            list.Add(new DpcIsrStat(r.GetInt64(0), r.GetInt64(1), r.GetString(2), r.GetString(3), r.GetDouble(4), r.GetInt64(5), r.GetDouble(6)));
        return list;
    }

    public async Task<IReadOnlyList<ProcessSnapshot>> GetProcessSnapshotsAsync(long sessionId, long fromQpc, long toQpc, CancellationToken ct)
    {
        await using var cn = await OpenReaderAsync(ct).ConfigureAwait(false);
        var snapshots = new List<(long Id, long Qpc, DateTime Utc, SnapshotTrigger Trigger)>();
        await using (var cmd = cn.CreateCommand())
        {
            cmd.CommandText = "SELECT id, ts_qpc, ts_utc, trigger FROM process_snapshots WHERE session_id=$s AND ts_qpc BETWEEN $f AND $t ORDER BY ts_qpc;";
            cmd.Parameters.AddWithValue("$s", sessionId);
            cmd.Parameters.AddWithValue("$f", fromQpc);
            cmd.Parameters.AddWithValue("$t", toQpc);
            await using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await r.ReadAsync(ct).ConfigureAwait(false))
                snapshots.Add((r.GetInt64(0), r.GetInt64(1), ParseIso(r.GetString(2)), (SnapshotTrigger)r.GetInt32(3)));
        }

        var result = new List<ProcessSnapshot>();
        foreach (var s in snapshots)
        {
            var rows = new List<ProcessSnapshotRow>();
            await using var cmd = cn.CreateCommand();
            cmd.CommandText = """
                SELECT pid, name, cpu_pct, ws_bytes, priv_bytes, read_bps, write_bps, threads, handles, page_faults_delta, kernel_ms, user_ms, recently_started, activity_spike
                FROM process_snapshot_rows WHERE snapshot_id=$id;
                """;
            cmd.Parameters.AddWithValue("$id", s.Id);
            await using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await r.ReadAsync(ct).ConfigureAwait(false))
                rows.Add(new ProcessSnapshotRow(
                    r.GetInt32(0), r.GetString(1), r.GetDouble(2), r.GetInt64(3), r.GetInt64(4),
                    r.GetInt64(5), r.GetInt64(6), r.GetInt32(7), r.GetInt32(8), r.GetInt64(9),
                    r.GetDouble(10), r.GetDouble(11), r.GetInt32(12) != 0, r.GetInt32(13) != 0));
            result.Add(new ProcessSnapshot(s.Qpc, s.Utc, s.Trigger, rows));
        }
        return result;
    }

    public async Task<SystemInfo?> GetSystemInfoAsync(long sessionId, CancellationToken ct)
    {
        await using var cn = await OpenReaderAsync(ct).ConfigureAwait(false);
        await using var cmd = cn.CreateCommand();
        cmd.CommandText = "SELECT json FROM system_info WHERE session_id=$s;";
        cmd.Parameters.AddWithValue("$s", sessionId);
        var json = await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false) as string;
        return json is null ? null : JsonSerializer.Deserialize<SystemInfo>(json, Json);
    }

    public async Task<IReadOnlyList<DriverInfo>> GetDriversAsync(long sessionId, CancellationToken ct)
    {
        await using var cn = await OpenReaderAsync(ct).ConfigureAwait(false);
        await using var cmd = cn.CreateCommand();
        cmd.CommandText = "SELECT name, version, date, vendor, device_class, path FROM drivers WHERE session_id=$s ORDER BY name;";
        cmd.Parameters.AddWithValue("$s", sessionId);
        var list = new List<DriverInfo>();
        await using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await r.ReadAsync(ct).ConfigureAwait(false))
            list.Add(new DriverInfo(
                r.GetString(0),
                r.IsDBNull(1) ? null : r.GetString(1),
                r.IsDBNull(2) ? null : ParseIso(r.GetString(2)),
                r.IsDBNull(3) ? null : r.GetString(3),
                r.IsDBNull(4) ? null : r.GetString(4),
                r.IsDBNull(5) ? null : r.GetString(5)));
        return list;
    }

    public async Task<long> GetDatabaseSizeBytesAsync(CancellationToken ct)
    {
        await Task.Yield();
        long total = 0;
        foreach (var p in new[] { _dbPath, _dbPath + "-wal", _dbPath + "-shm" })
            if (File.Exists(p)) total += new FileInfo(p).Length;
        return total;
    }

    public async Task<int> PruneSessionsAsync(DateTime olderThanUtc, CancellationToken ct)
    {
        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await using var tx = (SqliteTransaction)await _write!.BeginTransactionAsync(ct).ConfigureAwait(false);
            const string filter = "SELECT id FROM sessions WHERE ended_utc IS NOT NULL AND ended_utc < $cut";
            int affected;
            await using (var cmd = _write.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText = $"""
                    DELETE FROM stutter_correlations WHERE stutter_id IN (SELECT id FROM stutters WHERE session_id IN ({filter}));
                    DELETE FROM process_snapshot_rows WHERE snapshot_id IN (SELECT id FROM process_snapshots WHERE session_id IN ({filter}));
                    DELETE FROM stutters WHERE session_id IN ({filter});
                    DELETE FROM process_snapshots WHERE session_id IN ({filter});
                    DELETE FROM events WHERE session_id IN ({filter});
                    DELETE FROM samples WHERE session_id IN ({filter});
                    DELETE FROM dpc_isr_stats WHERE session_id IN ({filter});
                    DELETE FROM drivers WHERE session_id IN ({filter});
                    DELETE FROM system_info WHERE session_id IN ({filter});
                    DELETE FROM health WHERE session_id IN ({filter});
                    DELETE FROM sessions WHERE id IN ({filter});
                    """;
                cmd.Parameters.AddWithValue("$cut", Iso(olderThanUtc));
                affected = await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }
            await tx.CommitAsync(ct).ConfigureAwait(false);
            return affected;
        }
        finally { _writeLock.Release(); }
    }

    // ----------------------------------------------------------------- helpers

    private async Task<SqliteConnection> OpenReaderAsync(CancellationToken ct)
    {
        var cn = new SqliteConnection(_connectionString);
        await cn.OpenAsync(ct).ConfigureAwait(false);
        await ExecAsync(cn, "PRAGMA busy_timeout=5000;", ct).ConfigureAwait(false);
        return cn;
    }

    private static async Task ExecAsync(SqliteConnection cn, string sql, CancellationToken ct)
    {
        await using var cmd = cn.CreateCommand();
        cmd.CommandText = sql;
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    private static MonitoringSession ReadSession(SqliteDataReader r) => new()
    {
        Id = r.GetInt64(0),
        StartedUtc = ParseIso(r.GetString(1)),
        EndedUtc = r.IsDBNull(2) ? null : ParseIso(r.GetString(2)),
        Label = r.GetString(3),
        TpmTypeInferred = (TpmType)r.GetInt32(4),
        TpmBasis = r.GetString(5),
        MachineFingerprint = r.GetString(6),
        ConfigJson = r.GetString(7),
        Mode = r.GetString(8)
    };

    private static string Iso(DateTime dt) =>
        dt.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture);

    private static DateTime ParseIso(string s) =>
        DateTime.Parse(s, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind).ToUniversalTime();

    public async ValueTask DisposeAsync()
    {
        if (_eventBatcher is not null) await _eventBatcher.DisposeAsync().ConfigureAwait(false);
        if (_sampleBatcher is not null) await _sampleBatcher.DisposeAsync().ConfigureAwait(false);
        if (_dpcBatcher is not null) await _dpcBatcher.DisposeAsync().ConfigureAwait(false);
        if (_write is not null)
        {
            try { await ExecAsync(_write, "PRAGMA wal_checkpoint(TRUNCATE);", CancellationToken.None).ConfigureAwait(false); }
            catch { /* best effort */ }
            await _write.DisposeAsync().ConfigureAwait(false);
        }
        _writeLock.Dispose();
        SqliteConnection.ClearAllPools();
    }
}
