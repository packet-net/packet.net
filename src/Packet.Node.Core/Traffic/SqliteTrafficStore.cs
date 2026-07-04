using System.Globalization;
using Dapper;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Packet.Node.Core.Api;

namespace Packet.Node.Core.Traffic;

/// <summary>
/// The SQLite-backed traffic log: one row per AX.25 frame the node sends or
/// receives, persisted to its own database file — deliberately <b>separate</b>
/// from <c>pdn.db</c>, so a huge or corrupt frame log can never threaten node
/// state. Raw SQL via Dapper, mirroring <see cref="Auth.SqliteUserStore"/> /
/// <see cref="NetRom.SqliteNetRomRoutingStore"/>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Schema</b> is created with <c>CREATE TABLE IF NOT EXISTS</c> on open. The
/// database runs WAL (concurrent writer + readers) and incremental auto-vacuum,
/// so the size-cap prune (<see cref="PruneToSize"/>) actually returns freed pages
/// and <see cref="DatabaseSizeBytes"/> is an enforceable bound rather than a
/// high-water mark. Timestamps are persisted as UTC-normalised ISO-8601 strings
/// (<c>"o"</c>), the house store convention — fixed-width and lexicographically
/// chronological, so the range queries and the age prune compare correctly.
/// </para>
/// <para>
/// <b>Resilient.</b> Every operation is wrapped: a schema/open failure logs and
/// leaves the node running (frames simply aren't persisted), a write returns
/// false, a read returns empty, a prune returns 0. The traffic log can never
/// take the node down.
/// </para>
/// </remarks>
public sealed partial class SqliteTrafficStore
{
    /// <summary>Per-frame cap on the persisted raw-bytes BLOB. A frame's decoded
    /// columns are always stored in full; only the raw dump is truncated (a max-size
    /// AX.25 frame with a negotiated large N1 could otherwise bloat the log).</summary>
    public const int RawCapBytes = 2048;

    private const string SchemaSql = """
        CREATE TABLE IF NOT EXISTS traffic (
            id        INTEGER PRIMARY KEY AUTOINCREMENT,
            ts_utc    TEXT NOT NULL,
            port      TEXT NOT NULL,
            direction TEXT NOT NULL,
            source    TEXT NOT NULL,
            dest      TEXT NOT NULL,
            kind      TEXT NOT NULL,
            ns        INTEGER,
            nr        INTEGER,
            pf        INTEGER NOT NULL,
            control   INTEGER NOT NULL,
            pid       INTEGER,
            info_len  INTEGER NOT NULL,
            raw       BLOB NOT NULL,
            rssi_dbm        REAL,
            snr_db          REAL,
            noise_floor_dbm REAL);
        CREATE INDEX IF NOT EXISTS ix_traffic_ts ON traffic (ts_utc);
        CREATE INDEX IF NOT EXISTS ix_traffic_port_ts ON traffic (port, ts_utc);
        """;

    // Additive nullable radio-signal columns, added to a traffic table created before they existed.
    // The store is meta-less (CREATE TABLE IF NOT EXISTS, no PRAGMA user_version), so an existing db
    // needs each column added explicitly — the SqliteRefreshTokenStore pattern. A fresh db already
    // has them from SchemaSql, so the ADD is skipped.
    private static readonly string[] RadioColumns = ["rssi_dbm", "snr_db", "noise_floor_dbm"];

    private const string SelectColumns =
        "id AS Id, ts_utc AS TsUtc, port AS Port, direction AS Direction, source AS Source, " +
        "dest AS Dest, kind AS Kind, ns AS Ns, nr AS Nr, pf AS Pf, control AS Control, " +
        "pid AS Pid, info_len AS InfoLen, raw AS Raw, " +
        "rssi_dbm AS RssiDbm, snr_db AS SnrDb, noise_floor_dbm AS NoiseFloorDbm";

    private readonly string connectionString;
    private readonly ILogger<SqliteTrafficStore> logger;

    /// <summary>Open (creating if absent) the traffic log at <paramref name="dbPath"/>
    /// and ensure its schema. A schema/open failure is logged, not thrown — the node
    /// still boots, just without a persisted traffic log.</summary>
    public SqliteTrafficStore(string dbPath, ILogger<SqliteTrafficStore>? logger = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dbPath);
        this.logger = logger ?? NullLogger<SqliteTrafficStore>.Instance;
        connectionString = new SqliteConnectionStringBuilder { DataSource = dbPath }.ToString();
        EnsureSchema();
    }

    private SqliteConnection Open()
    {
        var conn = new SqliteConnection(connectionString);
        conn.Open();
        return conn;
    }

    private void EnsureSchema()
    {
        try
        {
            using var conn = Open();
            // auto_vacuum must be selected before the first table exists (it is a
            // no-op on a populated db without a full VACUUM); incremental mode lets
            // the size-cap prune hand freed pages back via incremental_vacuum.
            conn.Execute("PRAGMA auto_vacuum=INCREMENTAL;");
            conn.Execute("PRAGMA journal_mode=WAL;");
            conn.Execute(SchemaSql);

            var existing = conn.Query<string>("SELECT name FROM pragma_table_info('traffic');")
                .ToHashSet(StringComparer.Ordinal);
            foreach (var col in RadioColumns)
            {
                if (!existing.Contains(col))
                {
                    conn.Execute($"ALTER TABLE traffic ADD COLUMN {col} REAL;");
                }
            }
        }
        catch (SqliteException ex)
        {
            LogSchemaFailed(ex, connectionString);
        }
    }

    /// <summary>Insert a batch of frames in one transaction. Returns false (logged)
    /// on fault — the batch is then lost, never retried: the log is a diagnostic
    /// aid and must stay fire-and-forget.</summary>
    public bool Append(IReadOnlyList<TrafficRecord> batch)
    {
        ArgumentNullException.ThrowIfNull(batch);
        if (batch.Count == 0)
        {
            return true;
        }
        try
        {
            using var conn = Open();
            using var tx = conn.BeginTransaction();
            foreach (var r in batch)
            {
                conn.Execute(
                    "INSERT INTO traffic (ts_utc, port, direction, source, dest, kind, ns, nr, pf, control, pid, info_len, raw, rssi_dbm, snr_db, noise_floor_dbm) " +
                    "VALUES (@ts, @port, @dir, @src, @dst, @kind, @ns, @nr, @pf, @ctl, @pid, @len, @raw, @rssi, @snr, @noise);",
                    new
                    {
                        ts = Stamp(r.TimestampUtc),
                        port = r.PortId,
                        dir = r.Direction,
                        src = r.Source,
                        dst = r.Dest,
                        kind = r.Kind,
                        ns = r.Ns,
                        nr = r.Nr,
                        pf = r.Pf,
                        ctl = r.Control,
                        pid = r.Pid,
                        len = r.InfoLength,
                        raw = r.Raw,
                        rssi = r.RssiDbm is { } rs ? (double?)rs : null,
                        snr = r.SnrDb is { } sn ? (double?)sn : null,
                        noise = r.NoiseFloorDbm is { } nf ? (double?)nf : null,
                    },
                    transaction: tx);
            }
            tx.Commit();
            return true;
        }
        catch (SqliteException ex)
        {
            LogWriteFailed(ex, connectionString);
            return false;
        }
    }

    /// <summary>Recent frames, newest-first, optionally filtered by port and a UTC
    /// time range (inclusive). Empty (logged) on fault.</summary>
    public IReadOnlyList<TrafficFrame> Query(
        string? portId, DateTimeOffset? sinceUtc, DateTimeOffset? untilUtc, int limit)
    {
        if (limit <= 0)
        {
            return [];
        }
        try
        {
            using var conn = Open();
            var rows = conn.Query<Row>(
                $"SELECT {SelectColumns} FROM traffic " +
                "WHERE (@port IS NULL OR port = @port) " +
                "AND (@since IS NULL OR ts_utc >= @since) " +
                "AND (@until IS NULL OR ts_utc <= @until) " +
                "ORDER BY id DESC LIMIT @limit;",
                new
                {
                    port = portId,
                    since = sinceUtc is { } s ? Stamp(s) : null,
                    until = untilUtc is { } u ? Stamp(u) : null,
                    limit,
                });
            return rows.Select(ToFrame).ToArray();
        }
        catch (Exception ex) when (ex is SqliteException or FormatException)
        {
            LogReadFailed(ex, connectionString);
            return [];
        }
    }

    /// <summary>Delete rows whose timestamp is older than <paramref name="cutoffUtc"/>.
    /// Returns the number of rows pruned (0 on fault, logged).</summary>
    public int PruneOlderThan(DateTimeOffset cutoffUtc)
    {
        try
        {
            using var conn = Open();
            int rows = conn.Execute("DELETE FROM traffic WHERE ts_utc < @c;", new { c = Stamp(cutoffUtc) });
            if (rows > 0)
            {
                conn.Execute("PRAGMA incremental_vacuum;");
            }
            return rows;
        }
        catch (SqliteException ex)
        {
            LogWriteFailed(ex, connectionString);
            return 0;
        }
    }

    /// <summary>Delete the oldest rows in batches until the database fits inside
    /// <paramref name="maxBytes"/> (freed pages are returned via incremental
    /// vacuum, so the bound is real). Returns the number of rows pruned.</summary>
    public int PruneToSize(long maxBytes)
    {
        try
        {
            using var conn = Open();
            int deleted = 0;
            while (true)
            {
                long size = SizeBytes(conn);
                if (size <= maxBytes)
                {
                    break;
                }
                long count = conn.ExecuteScalar<long>("SELECT COUNT(*) FROM traffic;");
                if (count == 0)
                {
                    break;  // nothing left to delete — the empty schema is as small as it gets.
                }
                // Estimate how many oldest rows must go from the average row weight.
                // size/count overestimates per-row (it includes schema overhead), so
                // n slightly undershoots and the loop converges from above without
                // ever over-deleting a small table to nothing.
                long perRow = Math.Max(1, size / count);
                long n = Math.Clamp((size - maxBytes) / perRow + 1, 1, count);
                int deletedNow = conn.Execute(
                    "DELETE FROM traffic WHERE id IN (SELECT id FROM traffic ORDER BY id LIMIT @n);",
                    new { n });
                if (deletedNow == 0)
                {
                    break;
                }
                deleted += deletedNow;
                conn.Execute("PRAGMA incremental_vacuum;");
            }
            return deleted;
        }
        catch (SqliteException ex)
        {
            LogWriteFailed(ex, connectionString);
            return 0;
        }
    }

    /// <summary>The database's logical size in bytes (page_count × page_size) — the
    /// quantity <see cref="PruneToSize"/> bounds. 0 on fault (logged).</summary>
    public long DatabaseSizeBytes()
    {
        try
        {
            using var conn = Open();
            return SizeBytes(conn);
        }
        catch (SqliteException ex)
        {
            LogReadFailed(ex, connectionString);
            return 0;
        }
    }

    /// <summary>Total persisted rows (tests + diagnostics). 0 on fault (logged).</summary>
    public long Count()
    {
        try
        {
            using var conn = Open();
            return conn.ExecuteScalar<long>("SELECT COUNT(*) FROM traffic;");
        }
        catch (SqliteException ex)
        {
            LogReadFailed(ex, connectionString);
            return 0;
        }
    }

    private static long SizeBytes(SqliteConnection conn)
        => conn.ExecuteScalar<long>("SELECT page_count * page_size FROM pragma_page_count(), pragma_page_size();");

    private static TrafficFrame ToFrame(Row row)
    {
        var bytes = row.Raw ?? [];
        var raw = new int[bytes.Length];
        for (int i = 0; i < bytes.Length; i++)
        {
            raw[i] = bytes[i];
        }
        return new TrafficFrame(
            Id: row.Id,
            Timestamp: ParseStamp(row.TsUtc),
            PortId: row.Port,
            Direction: row.Direction,
            Source: row.Source,
            Dest: row.Dest,
            Kind: row.Kind,
            Ns: (int?)row.Ns,
            Nr: (int?)row.Nr,
            Pf: (int)row.Pf,
            Control: (int)row.Control,
            Pid: (int?)row.Pid,
            InfoLength: (int)row.InfoLen,
            Raw: raw,
            // REAL columns read back as double; narrow to the float the 0.1 dB source used so JSON
            // renders -95.3, not -95.30000305.
            RssiDbm: row.RssiDbm is { } rs ? (float?)rs : null,
            SnrDb: row.SnrDb is { } sn ? (float?)sn : null,
            NoiseFloorDbm: row.NoiseFloorDbm is { } nf ? (float?)nf : null);
    }

    private static string Stamp(DateTimeOffset value)
        => value.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture);

    private static DateTimeOffset ParseStamp(string value)
        => DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);

    // Dapper row DTO (mutable so Dapper's setter mapping binds it).
    private sealed class Row
    {
        public long Id { get; set; }
        public string TsUtc { get; set; } = string.Empty;
        public string Port { get; set; } = string.Empty;
        public string Direction { get; set; } = string.Empty;
        public string Source { get; set; } = string.Empty;
        public string Dest { get; set; } = string.Empty;
        public string Kind { get; set; } = string.Empty;
        public long? Ns { get; set; }
        public long? Nr { get; set; }
        public long Pf { get; set; }
        public long Control { get; set; }
        public long? Pid { get; set; }
        public long InfoLen { get; set; }
        public byte[]? Raw { get; set; }
        public double? RssiDbm { get; set; }
        public double? SnrDb { get; set; }
        public double? NoiseFloorDbm { get; set; }
    }

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "Traffic log: could not initialise the schema ({Db}); frames will not be persisted this run.")]
    private partial void LogSchemaFailed(Exception ex, string db);

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "Traffic log: a read failed ({Db}); returning empty.")]
    private partial void LogReadFailed(Exception ex, string db);

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "Traffic log: a write failed ({Db}); the batch was not persisted.")]
    private partial void LogWriteFailed(Exception ex, string db);
}

/// <summary>
/// One frame as inserted into the traffic log — the persistence projection of a
/// <see cref="MonitorEvent"/> (see <see cref="From"/>).
/// </summary>
public sealed record TrafficRecord(
    DateTimeOffset TimestampUtc,
    string PortId,
    string Direction,        // "rx" | "tx"
    string Source,
    string Dest,
    string Kind,             // "SABM" | "UA" | "DISC" | "DM" | "I" | "RR" | … (MonitorEvent.Type)
    int? Ns,                 // I-frames only
    int? Nr,                 // I + S frames
    int Pf,                  // poll/final, 0|1
    int Control,             // first control octet
    int? Pid,                // protocol id byte, I/UI frames only
    int InfoLength,          // full info-field length (not capped)
    byte[] Raw,              // raw frame bytes, capped at SqliteTrafficStore.RawCapBytes
    float? RssiDbm = null,   // per-frame RSSI (dBm) from the radio control channel; RX + radio only
    float? SnrDb = null,     // RSSI over the tracked noise floor (dB)
    float? NoiseFloorDbm = null) // tracked channel-idle noise floor (dBm)
{
    /// <summary>
    /// Project a traced <see cref="MonitorEvent"/> (the single decode the node
    /// already performs for the live monitor) into the log's row shape: the
    /// monitor's <c>"in"/"out"</c> becomes the log's <c>"rx"/"tx"</c>, the
    /// <c>"0xCF"</c>-style PID string becomes the numeric byte, and the raw dump
    /// is capped at <see cref="SqliteTrafficStore.RawCapBytes"/>.
    /// </summary>
    public static TrafficRecord From(MonitorEvent evt)
    {
        ArgumentNullException.ThrowIfNull(evt);

        int rawLength = Math.Min(evt.Raw.Count, SqliteTrafficStore.RawCapBytes);
        var raw = new byte[rawLength];
        for (int i = 0; i < rawLength; i++)
        {
            raw[i] = (byte)evt.Raw[i];
        }

        // MonitorEventFactory formats the PID as "0x" + two hex digits; parse it
        // back rather than re-deriving the offset from the raw bytes (which would
        // be a second decode).
        int? pid = evt.Pid is { Length: > 2 } p
            ? int.Parse(p.AsSpan(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture)
            : null;

        return new TrafficRecord(
            TimestampUtc: evt.Timestamp.ToUniversalTime(),
            PortId: evt.PortId,
            Direction: evt.Direction == "in" ? "rx" : "tx",
            Source: evt.Source,
            Dest: evt.Dest,
            Kind: evt.Type,
            Ns: evt.Ns,
            Nr: evt.Nr,
            Pf: evt.Pf,
            Control: evt.Control,
            Pid: pid,
            InfoLength: evt.InfoLength,
            Raw: raw,
            RssiDbm: evt.RssiDbm,
            SnrDb: evt.SnrDb,
            NoiseFloorDbm: evt.NoiseFloorDbm);
    }
}

/// <summary>
/// One frame as read back from the traffic log — the <c>GET /api/v1/traffic</c>
/// row shape (System.Text.Json's web defaults camel-case the properties). Like
/// <see cref="MonitorEvent"/>, <see cref="Raw"/> is widened to ints so the bytes
/// reach the wire as a JSON number array.
/// </summary>
public sealed record TrafficFrame(
    long Id,
    DateTimeOffset Timestamp,
    string PortId,
    string Direction,        // "rx" | "tx"
    string Source,
    string Dest,
    string Kind,
    int? Ns,
    int? Nr,
    int Pf,
    int Control,
    int? Pid,
    int InfoLength,
    IReadOnlyList<int> Raw,
    float? RssiDbm = null,
    float? SnrDb = null,
    float? NoiseFloorDbm = null);
