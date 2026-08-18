using Dapper;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Packet.Core;
using Packet.NetRom.Routing;
using Packet.Node.Core.Storage;

namespace Packet.Node.Core.NetRom;

/// <summary>
/// The SQLite-backed <see cref="INetRomRoutingStore"/>: persists the learned NET/ROM
/// routing table to a <c>pdn.db</c> file (raw SQL via Dapper - no EF, per the node
/// host's persistence decision) so a restart restores the topology instead of going
/// blind until the next NODES broadcast.
/// </summary>
/// <remarks>
/// <para>
/// <b>pdn.db</b> is the node's consolidated operational store; this slice persists the
/// routing table (the <c>neighbour</c> / <c>destination</c> / <c>route</c> tables plus
/// a <c>meta</c> row holding the save timestamp). Config and packet-capture join later
/// (config when the web editor exists; capture in its own DB).
/// </para>
/// <para>
/// <b>Resilient.</b> Every database operation is wrapped: a schema/open failure logs and
/// leaves the node running in-memory; <see cref="Load"/> returns <c>null</c> on any
/// fault; <see cref="Save"/> swallows + logs. Persistence can never take the node down.
/// The connection runs in WAL mode and a fresh connection is opened per call (cheap;
/// Microsoft.Data.Sqlite pools them), so the timer / dispose / debounce callers never
/// contend on a shared connection.
/// </para>
/// </remarks>
public sealed partial class SqliteNetRomRoutingStore : INetRomRoutingStore
{
    // v2 (#725): a neighbour is keyed (port_id, callsign), and a route carries the port of the
    // adjacency it runs over. v1's callsign-only keys physically could not hold the same station
    // twice, so the schema and the in-memory key HAD to move together - with v1's schema under a
    // PC4 table every Save throws a UNIQUE violation, which this class swallows and logs
    // (persistence silently stops).
    private const int SchemaVersion = 2;
    private const string SavedAtKey = "saved_at_utc";

    private const string SchemaSql = """
        CREATE TABLE IF NOT EXISTS neighbour (
            port_id        TEXT NOT NULL,
            callsign       TEXT NOT NULL,
            alias          TEXT NOT NULL,
            path_quality   INTEGER NOT NULL,
            last_heard_utc TEXT NOT NULL,
            PRIMARY KEY (port_id, callsign));
        CREATE TABLE IF NOT EXISTS destination (
            callsign TEXT PRIMARY KEY,
            alias    TEXT NOT NULL);
        CREATE TABLE IF NOT EXISTS route (
            dest_callsign TEXT NOT NULL,
            port_id       TEXT NOT NULL,
            via_neighbour TEXT NOT NULL,
            quality       INTEGER NOT NULL,
            obsolescence  INTEGER NOT NULL,
            PRIMARY KEY (dest_callsign, port_id, via_neighbour));
        CREATE TABLE IF NOT EXISTS meta (
            key   TEXT PRIMARY KEY,
            value TEXT NOT NULL);
        """;

    // The v1 -> v2 migration. EnsureSchema is a version STAMP, not a runner, and
    // CREATE TABLE IF NOT EXISTS no-ops on an existing table - so bumping the version alone would
    // leave v1's callsign-PK tables in place wearing a v2 stamp. Drop and recreate: this table is
    // a CACHE, fully re-learnt within one NODESINTERVAL (60 s at GB7RDG), on a node that has just
    // restarted anyway. The same argument the code already makes for not persisting INP3 metrics
    // at all (NetRomRoutingModel.cs). `meta` is dropped too so the saved-at stamp cannot outlive
    // the rows it described.
    private const string DropSql = """
        DROP TABLE IF EXISTS route;
        DROP TABLE IF EXISTS destination;
        DROP TABLE IF EXISTS neighbour;
        DROP TABLE IF EXISTS meta;
        """;

    private readonly string connectionString;
    private readonly ILogger<SqliteNetRomRoutingStore> logger;

    /// <summary>Open (creating if absent) the store at <paramref name="dbPath"/> and
    /// ensure its schema. A schema/open failure is logged, not thrown - the node still
    /// boots, just without persistence.</summary>
    public SqliteNetRomRoutingStore(string dbPath, ILogger<SqliteNetRomRoutingStore>? logger = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dbPath);
        this.logger = logger ?? NullLogger<SqliteNetRomRoutingStore>.Instance;
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
            var version = conn.ExecuteScalar<long>("PRAGMA user_version;");
            if (version < SchemaVersion)
            {
                // Drop first: on a fresh file these are no-ops, and on a v1 file they are the
                // migration (see DropSql). Both statements plus the stamp run in one transaction
                // so a half-migrated database cannot survive a crash here.
                using var tx = conn.BeginTransaction();
                conn.Execute(DropSql, transaction: tx);
                conn.Execute(SchemaSql, transaction: tx);
                conn.Execute($"PRAGMA user_version={SchemaVersion};", transaction: tx);
                tx.Commit();
                if (version > 0)
                {
                    LogSchemaMigrated(version, SchemaVersion);
                }
            }
        }
        catch (SqliteException ex)
        {
            LogSchemaFailed(ex, connectionString);
        }
    }

    /// <inheritdoc/>
    public PersistedRouting? Load()
    {
        try
        {
            using var conn = Open();

            var savedRaw = conn.ExecuteScalar<string?>(
                "SELECT value FROM meta WHERE key = @k;", new { k = SavedAtKey });
            if (savedRaw is null)
            {
                return null;   // never saved
            }
            var savedAt = SqliteStamps.ParseStamp(savedRaw);

            var neighbourRows = conn.Query<NeighbourRow>(
                "SELECT callsign AS Callsign, alias AS Alias, port_id AS PortId, " +
                "path_quality AS PathQuality, last_heard_utc AS LastHeardUtc FROM neighbour;").ToList();
            var destRows = conn.Query<DestRow>(
                "SELECT callsign AS Callsign, alias AS Alias FROM destination;").ToList();
            var routeRows = conn.Query<RouteRow>(
                "SELECT dest_callsign AS DestCallsign, port_id AS PortId, via_neighbour AS ViaNeighbour, " +
                "quality AS Quality, obsolescence AS Obsolescence FROM route;").ToList();

            var neighbours = new List<NetRomNeighbour>(neighbourRows.Count);
            foreach (var n in neighbourRows)
            {
                if (Callsign.TryParse(n.Callsign, out var call))
                {
                    neighbours.Add(new NetRomNeighbour(
                        call, n.Alias, n.PortId, (byte)n.PathQuality, SqliteStamps.ParseStamp(n.LastHeardUtc)));
                }
            }

            var routesByDest = routeRows
                .GroupBy(r => r.DestCallsign, StringComparer.Ordinal)
                .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.Ordinal);

            var dests = new List<NetRomDestination>(destRows.Count);
            foreach (var d in destRows)
            {
                if (!Callsign.TryParse(d.Callsign, out var destCall))
                {
                    continue;
                }
                var routes = new List<NetRomRoute>();
                if (routesByDest.TryGetValue(d.Callsign, out var rs))
                {
                    foreach (var r in rs)
                    {
                        if (Callsign.TryParse(r.ViaNeighbour, out var via))
                        {
                            routes.Add(new NetRomRoute(via, r.PortId, (byte)r.Quality, (int)r.Obsolescence));
                        }
                    }
                }
                dests.Add(new NetRomDestination(destCall, d.Alias, routes));
            }

            var snapshot = new NetRomRoutingSnapshot(dests, neighbours, savedAt);
            return new PersistedRouting(snapshot, savedAt);
        }
        catch (Exception ex) when (ex is SqliteException or FormatException)
        {
            LogLoadFailed(ex, connectionString);
            return null;
        }
    }

    /// <inheritdoc/>
    public void Save(NetRomRoutingSnapshot snapshot, DateTimeOffset savedAt)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        try
        {
            using var conn = Open();
            using var tx = conn.BeginTransaction();

            // Snapshot-replace: the table is tiny (≤ MaxDestinations × MaxRoutes), so a
            // clear-and-insert in one transaction is simplest and atomic.
            conn.Execute("DELETE FROM route; DELETE FROM destination; DELETE FROM neighbour;", transaction: tx);

            foreach (var n in snapshot.Neighbours)
            {
                conn.Execute(
                    "INSERT INTO neighbour (port_id, callsign, alias, path_quality, last_heard_utc) " +
                    "VALUES (@p, @c, @a, @q, @h);",
                    new
                    {
                        c = n.Neighbour.ToString(),
                        a = n.Alias,
                        p = n.PortId,
                        q = (int)n.PathQuality,
                        h = SqliteStamps.Stamp(n.LastHeard),
                    },
                    tx);
            }

            foreach (var d in snapshot.Destinations)
            {
                conn.Execute(
                    "INSERT INTO destination (callsign, alias) VALUES (@c, @a);",
                    new { c = d.Destination.ToString(), a = d.Alias }, tx);

                foreach (var r in d.Routes)
                {
                    conn.Execute(
                        "INSERT INTO route (dest_callsign, port_id, via_neighbour, quality, obsolescence) " +
                        "VALUES (@d, @p, @v, @q, @o);",
                        new
                        {
                            d = d.Destination.ToString(),
                            p = r.PortId,
                            v = r.Neighbour.ToString(),
                            q = (int)r.Quality,
                            o = r.Obsolescence,
                        },
                        tx);
                }
            }

            conn.Execute(
                "INSERT INTO meta (key, value) VALUES (@k, @v) " +
                "ON CONFLICT(key) DO UPDATE SET value = @v;",
                new { k = SavedAtKey, v = SqliteStamps.Stamp(savedAt) }, tx);

            tx.Commit();
        }
        catch (SqliteException ex)
        {
            LogSaveFailed(ex, connectionString);
        }
    }

    // Dapper row DTOs (mutable so Dapper's setter mapping binds them).
    private sealed class NeighbourRow
    {
        public string Callsign { get; set; } = string.Empty;
        public string Alias { get; set; } = string.Empty;
        public string PortId { get; set; } = string.Empty;
        public long PathQuality { get; set; }
        public string LastHeardUtc { get; set; } = string.Empty;
    }

    private sealed class DestRow
    {
        public string Callsign { get; set; } = string.Empty;
        public string Alias { get; set; } = string.Empty;
    }

    private sealed class RouteRow
    {
        public string DestCallsign { get; set; } = string.Empty;
        public string PortId { get; set; } = string.Empty;
        public string ViaNeighbour { get; set; } = string.Empty;
        public long Quality { get; set; }
        public long Obsolescence { get; set; }
    }

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "NET/ROM routing store: could not initialise the schema ({Db}); persistence is disabled for this run.")]
    private partial void LogSchemaFailed(Exception ex, string db);

    [LoggerMessage(Level = LogLevel.Information,
        Message = "NET/ROM routing store: schema v{From} -> v{To}; the persisted routing table was recreated and will re-learn from the next NODES broadcast.")]
    private partial void LogSchemaMigrated(long from, int to);

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "NET/ROM routing store: could not load persisted routes ({Db}); starting with an empty table.")]
    private partial void LogLoadFailed(Exception ex, string db);

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "NET/ROM routing store: could not persist routes ({Db}); the table is unsaved this cycle.")]
    private partial void LogSaveFailed(Exception ex, string db);
}
