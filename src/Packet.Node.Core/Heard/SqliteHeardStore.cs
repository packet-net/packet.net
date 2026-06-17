using System.Globalization;
using Dapper;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Packet.Node.Core.Heard;

/// <summary>
/// The SQLite-backed <see cref="IHeardStore"/>: persists the MHeard log to the same
/// consolidated <c>pdn.db</c> the users + routing table + capability cache live in. Raw SQL via
/// Dapper, mirroring <see cref="Capabilities.SqlitePeerCapabilityStore"/>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Schema</b> is created with <c>CREATE TABLE IF NOT EXISTS</c> — the meta-less approach the
/// refresh-token / capability stores use, so it doesn't fight the routing store over
/// <c>PRAGMA user_version</c>. One table, <c>heard_log</c>, keyed by the (port, callsign) pair.
/// </para>
/// <para>
/// <b>Resilient.</b> Every operation is wrapped: a schema/open failure logs and leaves the node
/// running (the log just stays in-memory); a read returns empty on fault, a write is swallowed, a
/// delete returns false / 0. Persistence can never take the node down. WAL mode, a fresh pooled
/// connection per call — the same discipline as the capability store — so it is safe to share
/// across threads.
/// </para>
/// </remarks>
public sealed partial class SqliteHeardStore : IHeardStore
{
    private const string SchemaSql = """
        CREATE TABLE IF NOT EXISTS heard_log (
            port_id         TEXT NOT NULL,
            callsign        TEXT NOT NULL,
            first_heard_utc TEXT NOT NULL,
            last_heard_utc  TEXT NOT NULL,
            count           INTEGER NOT NULL,
            PRIMARY KEY (port_id, callsign));
        """;

    private readonly string connectionString;
    private readonly ILogger<SqliteHeardStore> logger;

    /// <summary>Open (creating if absent) the heard store at <paramref name="dbPath"/> and ensure
    /// its schema. A schema/open failure is logged, not thrown — the node still boots, just without
    /// heard-log persistence.</summary>
    public SqliteHeardStore(string dbPath, ILogger<SqliteHeardStore>? logger = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dbPath);
        this.logger = logger ?? NullLogger<SqliteHeardStore>.Instance;
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
            conn.Execute("PRAGMA journal_mode=WAL;");
            conn.Execute(SchemaSql);
        }
        catch (SqliteException ex)
        {
            LogSchemaFailed(ex, connectionString);
        }
    }

    /// <inheritdoc/>
    public void Upsert(HeardEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        try
        {
            using var conn = Open();
            conn.Execute(
                "INSERT INTO heard_log (port_id, callsign, first_heard_utc, last_heard_utc, count) " +
                "VALUES (@p, @c, @first, @last, @count) " +
                "ON CONFLICT(port_id, callsign) DO UPDATE SET " +
                "first_heard_utc = @first, last_heard_utc = @last, count = @count;",
                new
                {
                    p = entry.PortId,
                    c = entry.Callsign,
                    first = Stamp(entry.FirstHeard),
                    last = Stamp(entry.LastHeard),
                    count = entry.Count,
                });
        }
        catch (SqliteException ex)
        {
            LogWriteFailed(ex, connectionString);
        }
    }

    /// <inheritdoc/>
    public IReadOnlyList<HeardEntry> All()
    {
        try
        {
            using var conn = Open();
            var rows = conn.Query<HeardRow>(
                "SELECT port_id AS PortId, callsign AS Callsign, first_heard_utc AS FirstHeardUtc, " +
                "last_heard_utc AS LastHeardUtc, count AS Count FROM heard_log;").ToList();
            return rows.Select(ToEntry).ToList();
        }
        catch (Exception ex) when (ex is SqliteException or FormatException)
        {
            LogReadFailed(ex, connectionString);
            return [];
        }
    }

    /// <inheritdoc/>
    public bool Clear(string portId, string callsign)
    {
        ArgumentNullException.ThrowIfNull(portId);
        ArgumentNullException.ThrowIfNull(callsign);
        try
        {
            using var conn = Open();
            int rows = conn.Execute(
                "DELETE FROM heard_log WHERE port_id = @p AND callsign = @c;",
                new { p = portId, c = callsign });
            return rows > 0;
        }
        catch (SqliteException ex)
        {
            LogWriteFailed(ex, connectionString);
            return false;
        }
    }

    /// <inheritdoc/>
    public int ClearAll()
    {
        try
        {
            using var conn = Open();
            return conn.Execute("DELETE FROM heard_log;");
        }
        catch (SqliteException ex)
        {
            LogWriteFailed(ex, connectionString);
            return 0;
        }
    }

    /// <inheritdoc/>
    public int Prune(DateTimeOffset olderThan, int maxPerPort)
    {
        try
        {
            using var conn = Open();
            int deleted = 0;

            // 1) Age-out: rows last heard before the cutoff.
            deleted += conn.Execute(
                "DELETE FROM heard_log WHERE last_heard_utc < @cutoff;",
                new { cutoff = Stamp(olderThan) });

            // 2) Per-port cap: for each port over the cap, delete the oldest-heard rows down to it.
            // Done with a correlated subquery that keeps the newest @max rows per port (the rowids
            // to retain) and deletes the rest. SQLite has no per-group LIMIT in DELETE, so this
            // ranks within the port by last_heard and drops anything outside the top @max.
            if (maxPerPort > 0)
            {
                deleted += conn.Execute(
                    """
                    DELETE FROM heard_log
                    WHERE rowid IN (
                        SELECT rowid FROM (
                            SELECT rowid,
                                   ROW_NUMBER() OVER (
                                       PARTITION BY port_id ORDER BY last_heard_utc DESC, rowid DESC
                                   ) AS rn
                            FROM heard_log)
                        WHERE rn > @max);
                    """,
                    new { max = maxPerPort });
            }

            return deleted;
        }
        catch (SqliteException ex)
        {
            LogWriteFailed(ex, connectionString);
            return 0;
        }
    }

    private static HeardEntry ToEntry(HeardRow row) => new(
        row.PortId,
        row.Callsign,
        ParseStamp(row.FirstHeardUtc),
        ParseStamp(row.LastHeardUtc),
        row.Count);

    private static string Stamp(DateTimeOffset value) => value.ToString("o", CultureInfo.InvariantCulture);

    private static DateTimeOffset ParseStamp(string value) =>
        DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);

    // Dapper row DTO (mutable so Dapper's setter mapping binds it).
    private sealed class HeardRow
    {
        public string PortId { get; set; } = string.Empty;
        public string Callsign { get; set; } = string.Empty;
        public string FirstHeardUtc { get; set; } = string.Empty;
        public string LastHeardUtc { get; set; } = string.Empty;
        public long Count { get; set; }
    }

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "Heard store: could not initialise the schema ({Db}); the heard log is in-memory only for this run.")]
    private partial void LogSchemaFailed(Exception ex, string db);

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "Heard store: a read failed ({Db}); treating as empty.")]
    private partial void LogReadFailed(Exception ex, string db);

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "Heard store: a write failed ({Db}); the change was not persisted.")]
    private partial void LogWriteFailed(Exception ex, string db);
}
