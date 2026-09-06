namespace StutterDiag.Core.Storage;

/// <summary>
/// SQLite schema, versioned via <c>PRAGMA user_version</c>. Add a new entry to
/// <see cref="Migrations"/> for every forward change; never edit an applied one.
/// </summary>
internal static class SqliteSchema
{
    public const int CurrentVersion = 1;

    public static readonly IReadOnlyList<string> Migrations = new[]
    {
        // ---- v1 : initial schema ----
        """
        CREATE TABLE sessions (
            id                  INTEGER PRIMARY KEY AUTOINCREMENT,
            started_utc         TEXT NOT NULL,
            ended_utc           TEXT NULL,
            label               TEXT NOT NULL DEFAULT '',
            tpm_type            INTEGER NOT NULL DEFAULT 1,
            tpm_basis           TEXT NOT NULL DEFAULT '',
            machine_fingerprint TEXT NOT NULL DEFAULT '',
            config_json         TEXT NOT NULL DEFAULT '{}',
            mode                TEXT NOT NULL DEFAULT 'Standard'
        );

        CREATE TABLE events (
            id          INTEGER PRIMARY KEY AUTOINCREMENT,
            session_id  INTEGER NOT NULL,
            ts_qpc      INTEGER NOT NULL,
            ts_utc      TEXT NOT NULL,
            category    INTEGER NOT NULL,
            source      TEXT NOT NULL,
            provider    TEXT NULL,
            event_id    INTEGER NULL,
            severity    INTEGER NOT NULL,
            message     TEXT NOT NULL,
            raw_xml     TEXT NULL,
            data_json   TEXT NULL
        );
        CREATE INDEX ix_events_session_ts ON events (session_id, ts_qpc);
        CREATE INDEX ix_events_session_cat ON events (session_id, category, ts_qpc);

        CREATE TABLE samples (
            session_id  INTEGER NOT NULL,
            ts_qpc      INTEGER NOT NULL,
            metric      TEXT NOT NULL,
            instance    TEXT NOT NULL DEFAULT '',
            value       REAL NOT NULL
        );
        CREATE INDEX ix_samples_session_metric_ts ON samples (session_id, metric, ts_qpc);

        CREATE TABLE dpc_isr_stats (
            session_id       INTEGER NOT NULL,
            window_start_qpc INTEGER NOT NULL,
            window_end_qpc   INTEGER NOT NULL,
            driver           TEXT NOT NULL,
            kind             TEXT NOT NULL,
            total_ms         REAL NOT NULL,
            count            INTEGER NOT NULL,
            max_ms           REAL NOT NULL
        );
        CREATE INDEX ix_dpc_session_ts ON dpc_isr_stats (session_id, window_start_qpc);

        CREATE TABLE stutters (
            id              INTEGER PRIMARY KEY AUTOINCREMENT,
            session_id      INTEGER NOT NULL,
            ts_qpc          INTEGER NOT NULL,
            ts_utc          TEXT NOT NULL,
            duration_ms     REAL NOT NULL,
            severity        INTEGER NOT NULL,
            detector        INTEGER NOT NULL,
            confidence      INTEGER NOT NULL,
            corroborated_by TEXT NOT NULL DEFAULT '',
            user_marked     INTEGER NOT NULL DEFAULT 0
        );
        CREATE INDEX ix_stutters_session_ts ON stutters (session_id, ts_qpc);

        CREATE TABLE stutter_correlations (
            stutter_id   INTEGER NOT NULL,
            signal_type  TEXT NOT NULL,
            proximity_ms REAL NOT NULL,
            score        INTEGER NOT NULL,
            base_rate    REAL NOT NULL,
            detail       TEXT NOT NULL DEFAULT ''
        );
        CREATE INDEX ix_correlations_stutter ON stutter_correlations (stutter_id);

        CREATE TABLE process_snapshots (
            id         INTEGER PRIMARY KEY AUTOINCREMENT,
            session_id INTEGER NOT NULL,
            ts_qpc     INTEGER NOT NULL,
            ts_utc     TEXT NOT NULL,
            trigger    INTEGER NOT NULL
        );
        CREATE INDEX ix_snapshots_session_ts ON process_snapshots (session_id, ts_qpc);

        CREATE TABLE process_snapshot_rows (
            snapshot_id        INTEGER NOT NULL,
            pid                INTEGER NOT NULL,
            name               TEXT NOT NULL,
            cpu_pct            REAL NOT NULL,
            ws_bytes           INTEGER NOT NULL,
            priv_bytes         INTEGER NOT NULL,
            read_bps           INTEGER NOT NULL,
            write_bps          INTEGER NOT NULL,
            threads            INTEGER NOT NULL,
            handles            INTEGER NOT NULL,
            page_faults_delta  INTEGER NOT NULL,
            kernel_ms          REAL NOT NULL,
            user_ms            REAL NOT NULL,
            recently_started   INTEGER NOT NULL,
            activity_spike     INTEGER NOT NULL
        );
        CREATE INDEX ix_snapshot_rows_snapshot ON process_snapshot_rows (snapshot_id);

        CREATE TABLE system_info (
            session_id INTEGER PRIMARY KEY,
            json       TEXT NOT NULL
        );

        CREATE TABLE drivers (
            session_id   INTEGER NOT NULL,
            name         TEXT NOT NULL,
            version      TEXT NULL,
            date         TEXT NULL,
            vendor       TEXT NULL,
            device_class TEXT NULL,
            path         TEXT NULL
        );
        CREATE INDEX ix_drivers_session ON drivers (session_id);

        CREATE TABLE health (
            session_id INTEGER NOT NULL,
            ts_utc     TEXT NOT NULL,
            monitor    TEXT NOT NULL,
            status     INTEGER NOT NULL,
            note       TEXT NULL
        );
        CREATE INDEX ix_health_session ON health (session_id);
        """
    };
}
