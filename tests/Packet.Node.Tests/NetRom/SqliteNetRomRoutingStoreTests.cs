using Packet.Core;
using Packet.NetRom.Routing;
using Packet.Node.Core.NetRom;
using Packet.Node.Tests.Support;

namespace Packet.Node.Tests.NetRom;

/// <summary>
/// The SQLite routing store (<c>pdn.db</c>): save/load round-trip, snapshot-replace,
/// cross-instance durability (the "restart"), and the resilience contract — a store
/// it cannot open must degrade (no throw on construct, null Load, no-op Save) rather
/// than take the node down.
/// </summary>
[Trait("Category", "Node")]
public sealed class SqliteNetRomRoutingStoreTests : IDisposable
{
    private readonly string dbPath = TestPaths.NewPath("pdn-store", ".db");

    private static readonly Callsign Nbr = new("GB7RDG", 0);
    private static readonly Callsign Dest = new("GB7SOT", 0);

    private static NetRomRoutingSnapshot Sample(DateTimeOffset at) => new(
        new List<NetRomDestination>
        {
            new(Dest, "SOT", new List<NetRomRoute> { new(Nbr, 200, 6) }),
            new(Nbr, "RDGBPQ", new List<NetRomRoute> { new(Nbr, 192, 5) }),
        },
        new List<NetRomNeighbour> { new(Nbr, "RDGBPQ", "p1", 192, at) },
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
        nbr.PortId.Should().Be("p1");
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
        // construction must not throw, Load returns null, and Save is a no-op —
        // persistence is simply disabled for the run, the node keeps running.
        var bad = Path.Combine(TestPaths.NewPath("no-such-dir"), "pdn.db");

        var construct = () => new SqliteNetRomRoutingStore(bad);
        construct.Should().NotThrow();

        var store = new SqliteNetRomRoutingStore(bad);
        store.Load().Should().BeNull();
        var save = () => store.Save(NetRomRoutingSnapshot.Empty, DateTimeOffset.UtcNow);
        save.Should().NotThrow();
    }

    public void Dispose()
    {
        foreach (var p in new[] { dbPath, dbPath + "-wal", dbPath + "-shm" })
        {
            try { if (File.Exists(p)) { File.Delete(p); } } catch { /* best effort */ }
        }
    }
}
