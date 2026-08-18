using Dapper;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Packet.Node.Core.Storage;

namespace Packet.Node.Core.Capabilities;

/// <summary>
/// The SQLite-backed <see cref="IPeerCapabilityStore"/>: persists the learned per-peer
/// AX.25 capability cache to the same consolidated <c>pdn.db</c> the users + routing
/// table live in. Raw SQL via Dapper, mirroring <see cref="Auth.SqliteRefreshTokenStore"/>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Schema</b> is created with <c>CREATE TABLE IF NOT EXISTS</c> - the same meta-less
/// approach the refresh-token store uses, so it doesn't fight the routing store over
/// <c>PRAGMA user_version</c>. One table, <c>peer_capability</c>, keyed by the
/// (port, peer) pair, because capability is per-link.
/// </para>
/// <para>
/// <b>Resilient.</b> Every operation is wrapped: a schema/open failure logs and leaves
/// the node running (the cache just stays in-memory); a lookup returns null / empty on
/// fault, a write is swallowed, a delete returns false / 0. Persistence can never take
/// the node down. WAL mode, a fresh pooled connection per call - the same discipline as
/// the refresh-token store - so the store is safe to share across threads.
/// </para>
/// </remarks>
public sealed partial class SqlitePeerCapabilityStore : IPeerCapabilityStore
{
    private const string SchemaSql = """
        CREATE TABLE IF NOT EXISTS peer_capability (
            port_id           TEXT NOT NULL,
            peer              TEXT NOT NULL,
            supports_extended INTEGER NULL,
            supports_srej_xid INTEGER NULL,
            last_probed_utc   TEXT NOT NULL,
            last_refused_utc  TEXT NULL,
            PRIMARY KEY (port_id, peer));
        """;

    private readonly string connectionString;
    private readonly ILogger<SqlitePeerCapabilityStore> logger;

    /// <summary>Open (creating if absent) the capability store at <paramref name="dbPath"/>
    /// and ensure its schema. A schema/open failure is logged, not thrown - the node still
    /// boots, just without capability persistence.</summary>
    public SqlitePeerCapabilityStore(string dbPath, ILogger<SqlitePeerCapabilityStore>? logger = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dbPath);
        this.logger = logger ?? NullLogger<SqlitePeerCapabilityStore>.Instance;
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
    public void Upsert(PeerCapabilityRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        try
        {
            using var conn = Open();
            conn.Execute(
                "INSERT INTO peer_capability " +
                "(port_id, peer, supports_extended, supports_srej_xid, last_probed_utc, last_refused_utc) " +
                "VALUES (@p, @c, @ext, @srej, @probed, @refused) " +
                "ON CONFLICT(port_id, peer) DO UPDATE SET " +
                "supports_extended = @ext, supports_srej_xid = @srej, " +
                "last_probed_utc = @probed, last_refused_utc = @refused;",
                new
                {
                    p = record.PortId,
                    c = record.Peer,
                    ext = ToInt(record.SupportsExtended),
                    srej = ToInt(record.SupportsSrejViaXid),
                    probed = SqliteStamps.Stamp(record.LastProbed),
                    refused = record.LastRefused is { } r ? SqliteStamps.Stamp(r) : null,
                });
        }
        catch (SqliteException ex)
        {
            LogWriteFailed(ex, connectionString);
        }
    }

    /// <inheritdoc/>
    public PeerCapabilityRecord? Find(string portId, string peer)
    {
        ArgumentNullException.ThrowIfNull(portId);
        ArgumentNullException.ThrowIfNull(peer);
        try
        {
            using var conn = Open();
            var row = conn.QuerySingleOrDefault<CapabilityRow>(
                "SELECT port_id AS PortId, peer AS Peer, supports_extended AS SupportsExtended, " +
                "supports_srej_xid AS SupportsSrejViaXid, last_probed_utc AS LastProbedUtc, " +
                "last_refused_utc AS LastRefusedUtc " +
                "FROM peer_capability WHERE port_id = @p AND peer = @c;",
                new { p = portId, c = peer });
            return row is null ? null : ToRecord(row);
        }
        catch (Exception ex) when (ex is SqliteException or FormatException)
        {
            LogReadFailed(ex, connectionString);
            return null;
        }
    }

    /// <inheritdoc/>
    public IReadOnlyList<PeerCapabilityRecord> All()
    {
        try
        {
            using var conn = Open();
            var rows = conn.Query<CapabilityRow>(
                "SELECT port_id AS PortId, peer AS Peer, supports_extended AS SupportsExtended, " +
                "supports_srej_xid AS SupportsSrejViaXid, last_probed_utc AS LastProbedUtc, " +
                "last_refused_utc AS LastRefusedUtc " +
                "FROM peer_capability;").ToList();
            return rows.Select(ToRecord).ToList();
        }
        catch (Exception ex) when (ex is SqliteException or FormatException)
        {
            LogReadFailed(ex, connectionString);
            return [];
        }
    }

    /// <inheritdoc/>
    public bool Clear(string portId, string peer)
    {
        ArgumentNullException.ThrowIfNull(portId);
        ArgumentNullException.ThrowIfNull(peer);
        try
        {
            using var conn = Open();
            int rows = conn.Execute(
                "DELETE FROM peer_capability WHERE port_id = @p AND peer = @c;",
                new { p = portId, c = peer });
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
            return conn.Execute("DELETE FROM peer_capability;");
        }
        catch (SqliteException ex)
        {
            LogWriteFailed(ex, connectionString);
            return 0;
        }
    }

    private static PeerCapabilityRecord ToRecord(CapabilityRow row) => new(
        row.PortId,
        row.Peer,
        ToBool(row.SupportsExtended),
        ToBool(row.SupportsSrejViaXid),
        SqliteStamps.ParseStamp(row.LastProbedUtc),
        string.IsNullOrEmpty(row.LastRefusedUtc) ? null : SqliteStamps.ParseStamp(row.LastRefusedUtc));

    // bool? <-> nullable INTEGER: null stays null; true/false map to 1/0.
    private static long? ToInt(bool? value) => value is { } b ? (b ? 1L : 0L) : null;

    private static bool? ToBool(long? value) => value is { } v ? v != 0 : null;

    // Dapper row DTO (mutable so Dapper's setter mapping binds it).
    private sealed class CapabilityRow
    {
        public string PortId { get; set; } = string.Empty;
        public string Peer { get; set; } = string.Empty;
        public long? SupportsExtended { get; set; }
        public long? SupportsSrejViaXid { get; set; }
        public string LastProbedUtc { get; set; } = string.Empty;
        public string? LastRefusedUtc { get; set; }
    }

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "Peer-capability store: could not initialise the schema ({Db}); the capability cache is in-memory only for this run.")]
    private partial void LogSchemaFailed(Exception ex, string db);

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "Peer-capability store: a read failed ({Db}); treating as absent.")]
    private partial void LogReadFailed(Exception ex, string db);

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "Peer-capability store: a write failed ({Db}); the change was not persisted.")]
    private partial void LogWriteFailed(Exception ex, string db);
}
