using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Packet.Core;
using Packet.NetRom.Routing;
using Packet.Node.Core.Configuration;
using Packet.Node.Core.NetRom;
using Packet.Node.Tests.Support;

namespace Packet.Node.Tests.NetRom;

/// <summary>
/// <see cref="NetRomService"/>'s persistence wiring: it hydrates its routing table from
/// the store on construction (ageing the routes by the downtime so stale ones do not
/// resurrect), and persistence rides on NET/ROM being enabled.
/// </summary>
[Trait("Category", "Node")]
public sealed class NetRomPersistenceTests : IDisposable
{
    private readonly string dbPath = TestPaths.NewPath("pdn-svc", ".db");

    // The port these adjacencies are on: a neighbour row and a route both key by
    // (port, callsign) since #725.
    private const string Port = "vhf";

    private static readonly Callsign Nbr = new("GB7RDG", 0);
    private static readonly Callsign Dest = new("GB7SOT", 0);
    private static readonly DateTimeOffset T0 = new(2026, 6, 6, 12, 0, 0, TimeSpan.Zero);

    private static NetRomRoutingSnapshot Sample(DateTimeOffset at, int obs = 6) => new(
        new List<NetRomDestination>
        {
            new(Dest, "SOT", new List<NetRomRoute> { new(Nbr, Port, 200, obs) }),
            new(Nbr, "RDGBPQ", new List<NetRomRoute> { new(Nbr, Port, 192, obs) }),
        },
        new List<NetRomNeighbour> { new(Nbr, "RDGBPQ", Port, 192, at) },
        at);

    private NetRomService NewService(FakeTimeProvider clock, bool enabled = true) => new(
        new NetRomConfig { Enabled = enabled },
        clock,
        NullLogger<NetRomService>.Instance,
        new SqliteNetRomRoutingStore(dbPath));

    [Fact]
    public void A_node_with_a_store_hydrates_its_routing_table_on_construction()
    {
        new SqliteNetRomRoutingStore(dbPath).Save(Sample(T0), T0);

        using var netRom = NewService(new FakeTimeProvider(T0));   // restarts immediately — no decay

        var snap = netRom.Snapshot();
        snap.Destinations.Should().Contain(d => d.Destination == Dest);
        snap.Neighbours.Should().Contain(n => n.Neighbour == Nbr);
        snap.Destinations.Single(d => d.Destination == Dest).BestRoute!.Obsolescence.Should().Be(6);
    }

    [Fact]
    public void Hydration_ages_routes_by_the_downtime()
    {
        // Saved at T0 with obsolescence 6; the default sweep interval is 1 hour.
        new SqliteNetRomRoutingStore(dbPath).Save(Sample(T0, obs: 6), T0);

        // Restart 4 hours later → 4 intervals of decay → obsolescence 6 - 4 = 2.
        using var netRom = NewService(new FakeTimeProvider(T0.AddHours(4)));

        netRom.Snapshot().Destinations.Single(d => d.Destination == Dest)
            .BestRoute!.Obsolescence.Should().Be(2);
    }

    [Fact]
    public void A_long_downtime_ages_every_route_out_and_the_node_starts_empty()
    {
        new SqliteNetRomRoutingStore(dbPath).Save(Sample(T0, obs: 6), T0);

        // Down for a week — far more than the 6 obsolescence intervals.
        using var netRom = NewService(new FakeTimeProvider(T0.AddDays(7)));

        netRom.Snapshot().DestinationCount.Should().Be(0, "a week of downtime ages every route out");
    }

    [Fact]
    public void A_disabled_service_does_not_hydrate()
    {
        new SqliteNetRomRoutingStore(dbPath).Save(Sample(T0), T0);

        using var netRom = NewService(new FakeTimeProvider(T0), enabled: false);

        netRom.Snapshot().DestinationCount.Should().Be(0, "persistence rides on NET/ROM being enabled");
    }

    public void Dispose()
    {
        foreach (var p in new[] { dbPath, dbPath + "-wal", dbPath + "-shm" })
        {
            try { if (File.Exists(p)) { File.Delete(p); } } catch { /* best effort */ }
        }
    }
}
