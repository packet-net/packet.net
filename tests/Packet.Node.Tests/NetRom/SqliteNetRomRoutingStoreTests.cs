using Dapper;
using Microsoft.Data.Sqlite;
using Packet.Core;
using Packet.NetRom.Routing;
using Packet.Node.Core.NetRom;
using Packet.Node.Tests.Support;

namespace Packet.Node.Tests.NetRom;

/// <summary>
/// The SQLite routing store (<c>pdn.db</c>): save/load round-trip, snapshot-replace,
/// cross-instance durability (the "restart"), and the resilience contract - a store
/// it cannot open must degrade (no throw on construct, null Load, no-op Save) rather
/// than take the node down.
/// </summary>
[Trait("Category", "Node")]
public sealed class SqliteNetRomRoutingStoreTests : IDisposable
{
    private readonly string dbPath = TestPaths.NewPath("pdn-store", ".db");

    // The port these adjacencies are on: a neighbour row and a route both key by
    // (port, callsign) since #725.
    private const string Port = "vhf";

    private static readonly Callsign Nbr = new("GB7RDG", 0);
    private static readonly Callsign Dest = new("GB7SOT", 0);

    private static NetRomRoutingSnapshot Sample(DateTimeOffset at) => new(
        new List<NetRomDestination>
        {
            new(Dest, "SOT", new List<NetRomRoute> { new(Nbr, Port, 200, 6) }),
            new(Nbr, "RDGBPQ", new List<NetRomRoute> { new(Nbr, Port, 192, 5) }),
        },
        new List<NetRomNeighbour> { new(Nbr, "RDGBPQ", Port, 192, at) },
        at);

    [Fact]
    public void Load_from_a_fresh_store_returns_null()
    {
        var store = new SqliteNetRomRoutingStore(dbPath);
        store.Load().Should().BeNull("nothing has been saved yet");
    }

    [Fact]
    public void Save_then_load_round_trips_the_snapshot_and_stamp()
    {
        var at = new DateTimeOffset(2026, 6, 6, 12, 0, 0, TimeSpan.Zero);
        var store = new SqliteNetRomRoutingStore(dbPath);
        store.Save(Sample(at), at);

        var loaded = store.Load();
        loaded.Should().NotBeNull();
        loaded!.Value.SavedAt.Should().Be(at);

        var snap = loaded.Value.Snapshot;
        var nbr = snap.Neighbours.Should().ContainSingle().Subject;
        nbr.Neighbour.Should().Be(Nbr);
        nbr.Alias.Should().Be("RDGBPQ");
        nbr.PortId.Should().Be(Port);
        nbr.PathQuality.Should().Be(192);
        nbr.LastHeard.Should().Be(at);

        var sot = snap.Destinations.Single(d => d.Destination == Dest);
        sot.Alias.Should().Be("SOT");
        sot.BestRoute!.Neighbour.Should().Be(Nbr);
        sot.BestRoute!.Quality.Should().Be(200);
        sot.BestRoute!.Obsolescence.Should().Be(6);
    }

    [Fact]
    public void Save_replaces_the_previous_snapshot_wholesale()
    {
        var t0 = new DateTimeOffset(2026, 6, 6, 12, 0, 0, TimeSpan.Zero);
        var store = new SqliteNetRomRoutingStore(dbPath);
        store.Save(Sample(t0), t0);

        var t1 = t0.AddHours(1);
        store.Save(NetRomRoutingSnapshot.Empty, t1);

        var loaded = store.Load();
        loaded!.Value.SavedAt.Should().Be(t1);
        loaded.Value.Snapshot.DestinationCount.Should().Be(0);
        loaded.Value.Snapshot.NeighbourCount.Should().Be(0);
    }

    [Fact]
    public void A_new_instance_over_the_same_file_reads_what_the_previous_one_wrote()
    {
        var at = new DateTimeOffset(2026, 6, 6, 12, 0, 0, TimeSpan.Zero);
        new SqliteNetRomRoutingStore(dbPath).Save(Sample(at), at);

        // The "restart": a fresh store instance over the same file sees the data.
        var reopened = new SqliteNetRomRoutingStore(dbPath).Load();
        reopened!.Value.Snapshot.Destinations.Should().Contain(d => d.Destination == Dest);
    }

    [Fact]
    public void A_store_it_cannot_open_degrades_instead_of_throwing()
    {
        // A db path whose parent directory does not exist: schema init fails, but
        // construction must not throw, Load returns null, and Save is a no-op -
        // persistence is simply disabled for the run, the node keeps running.
        var bad = Path.Combine(TestPaths.NewPath("no-such-dir"), "pdn.db");

        var construct = () => new SqliteNetRomRoutingStore(bad);
        construct.Should().NotThrow();

        var store = new SqliteNetRomRoutingStore(bad);
        store.Load().Should().BeNull();
        var save = () => store.Save(NetRomRoutingSnapshot.Empty, DateTimeOffset.UtcNow);
        save.Should().NotThrow();
    }

    [Fact]
    public void Two_adjacencies_to_one_callsign_round_trip()
    {
        // The UNIQUE-violation test (#725). At schema v1 `neighbour` had callsign as its PRIMARY
        // KEY and `route` was keyed (dest, via), so the moment the in-memory table could hold one
        // station on two ports every Save threw a UNIQUE violation - which this class swallows and
        // logs, so persistence would have stopped silently. The schema and the key HAD to move in
        // the same commit.
        var at = new DateTimeOffset(2026, 6, 6, 12, 0, 0, TimeSpan.Zero);
        var snapshot = new NetRomRoutingSnapshot(
            [
                new NetRomDestination(Dest, "SOT",
                [
                    new NetRomRoute(Nbr, "vhf", 200, 6),
                    new NetRomRoute(Nbr, "hf", 150, 5),
                ]),
            ],
            [
                new NetRomNeighbour(Nbr, "RDGBPQ", "vhf", 191, at),
                new NetRomNeighbour(Nbr, "RDGBPQ", "hf", 150, at),
            ],
            at);

        var store = new SqliteNetRomRoutingStore(dbPath);
        store.Save(snapshot, at);

        var loaded = store.Load();
        loaded.Should().NotBeNull("the save must not have thrown a swallowed UNIQUE violation");
        loaded!.Value.Snapshot.Neighbours.Where(n => n.Neighbour == Nbr).Should().HaveCount(2);
        loaded.Value.Snapshot.Neighbours.Single(n => n.PortId == "vhf").PathQuality.Should().Be(191);
        loaded.Value.Snapshot.Neighbours.Single(n => n.PortId == "hf").PathQuality.Should().Be(150);
        var routes = loaded.Value.Snapshot.Destinations.Single(d => d.Destination == Dest).Routes;
        routes.Should().HaveCount(2, "a route is keyed (dest, port, via) since v2");
        routes.Single(r => r.PortId == "vhf").Quality.Should().Be(200);
        routes.Single(r => r.PortId == "hf").Obsolescence.Should().Be(5);
    }

    [Fact]
    public void A_v1_database_is_recreated_at_v2_rather_than_left_wearing_a_v2_stamp()
    {
        // EnsureSchema is a version STAMP, not a runner, and CREATE TABLE IF NOT EXISTS no-ops on
        // an existing table - so bumping the version alone would leave v1's callsign-PK tables in
        // place under a v2 stamp, and every Save would fail. Build a genuine v1 file (v1's exact
        // DDL, with a row in it) and open it with the current store.
        using (var conn = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = dbPath }.ToString()))
        {
            conn.Open();
            conn.Execute("""
                CREATE TABLE neighbour (
                    callsign       TEXT PRIMARY KEY,
                    alias          TEXT NOT NULL,
                    port_id        TEXT NOT NULL,
                    path_quality   INTEGER NOT NULL,
                    last_heard_utc TEXT NOT NULL);
                CREATE TABLE destination (
                    callsign TEXT PRIMARY KEY,
                    alias    TEXT NOT NULL);
                CREATE TABLE route (
                    dest_callsign TEXT NOT NULL,
                    via_neighbour TEXT NOT NULL,
                    quality       INTEGER NOT NULL,
                    obsolescence  INTEGER NOT NULL,
                    PRIMARY KEY (dest_callsign, via_neighbour));
                CREATE TABLE meta (
                    key   TEXT PRIMARY KEY,
                    value TEXT NOT NULL);
                """);
            conn.Execute("INSERT INTO neighbour VALUES ('GB7RDG', 'RDGBPQ', 'vhf', 192, '2026-06-06T12:00:00.0000000+00:00');");
            conn.Execute("INSERT INTO meta VALUES ('saved_at_utc', '2026-06-06T12:00:00.0000000+00:00');");
            conn.Execute("PRAGMA user_version=1;");
        }

        var store = new SqliteNetRomRoutingStore(dbPath);

        // Drop-and-recreate: the table is a cache, fully re-learnt within one NODESINTERVAL.
        store.Load().Should().BeNull("v1's rows went with v1's tables - there is no saved_at stamp left");

        // And the recreated schema is genuinely v2: a save that v1's PK could not hold succeeds.
        var at = new DateTimeOffset(2026, 6, 6, 13, 0, 0, TimeSpan.Zero);
        store.Save(
            new NetRomRoutingSnapshot(
                [new NetRomDestination(Dest, "SOT", [new NetRomRoute(Nbr, "vhf", 200, 6), new NetRomRoute(Nbr, "hf", 150, 6)])],
                [new NetRomNeighbour(Nbr, "RDGBPQ", "vhf", 191, at), new NetRomNeighbour(Nbr, "RDGBPQ", "hf", 150, at)],
                at),
            at);

        store.Load()!.Value.Snapshot.Neighbours.Should().HaveCount(2, "the recreated schema keys neighbours (port, callsign)");
    }

    public void Dispose()
    {
        foreach (var p in new[] { dbPath, dbPath + "-wal", dbPath + "-shm" })
        {
            try { if (File.Exists(p)) { File.Delete(p); } } catch { /* best effort */ }
        }
    }
}
