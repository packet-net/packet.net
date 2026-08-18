using AwesomeAssertions;
using Microsoft.Extensions.Time.Testing;
using Packet.Core;
using Packet.NetRom.Routing;
using Xunit;

namespace Packet.NetRom.Tests.Routing;

/// <summary>
/// <see cref="NetRomRoutingTable.Restore"/> - the hydrate path the node host uses to
/// reload a persisted table at startup (so a restart does not lose the topology). Pure
/// table maintenance: it round-trips a <see cref="NetRomRoutingSnapshot"/>, ages routes
/// by the elapsed-downtime decay, drops what expired, and prunes orphan neighbours.
/// </summary>
public class NetRomRoutingRestoreTests
{
    private static readonly Callsign Nbr = new("GB7RDG", 0);
    // Every route here is on one port; a route's next hop is the (port, callsign) adjacency,
    // so the port travels with it.
    private const string Port = "vhf";

    private static readonly Callsign Dest = new("GB7SOT", 0);

    private static NetRomRoutingTable NewTable()
        => new(NetRomRoutingOptions.Default, new FakeTimeProvider(new DateTimeOffset(2026, 6, 6, 12, 0, 0, TimeSpan.Zero)));

    private static NetRomRoutingSnapshot Sample(int obs = 6)
    {
        var heard = new DateTimeOffset(2026, 6, 6, 11, 0, 0, TimeSpan.Zero);
        var dests = new List<NetRomDestination>
        {
            new(Dest, "SOT", new List<NetRomRoute> { new(Nbr, Port, 200, obs) }),
            new(Nbr, "RDGBPQ", new List<NetRomRoute> { new(Nbr, Port, 192, obs) }),
        };
        var neighbours = new List<NetRomNeighbour> { new(Nbr, "RDGBPQ", Port, 192, heard) };
        return new NetRomRoutingSnapshot(dests, neighbours, heard);
    }

    [Fact]
    public void Restore_with_no_decay_round_trips_the_snapshot()
    {
        var table = NewTable();
        table.Restore(Sample(), obsolescenceDecay: 0);

        var snap = table.Snapshot();
        snap.Neighbours.Should().ContainSingle().Which.Neighbour.Should().Be(Nbr);
        snap.Neighbours[0].Alias.Should().Be("RDGBPQ");
        snap.Neighbours[0].PortId.Should().Be(Port);

        var sot = snap.Destinations.Single(d => d.Destination == Dest);
        sot.Alias.Should().Be("SOT");
        sot.BestRoute!.Neighbour.Should().Be(Nbr);
        sot.BestRoute!.Quality.Should().Be(200);
        sot.BestRoute!.Obsolescence.Should().Be(6);
    }

    [Fact]
    public void Restore_ages_obsolescence_by_the_decay()
    {
        var table = NewTable();
        table.Restore(Sample(obs: 6), obsolescenceDecay: 2);

        table.Snapshot().Destinations.Single(d => d.Destination == Dest)
            .BestRoute!.Obsolescence.Should().Be(4);   // 6 - 2
    }

    [Fact]
    public void Restore_drops_routes_that_age_out_and_prunes_orphan_neighbours()
    {
        var table = NewTable();
        // Every route starts at obsolescence 6; a decay of 6 ages them all out.
        table.Restore(Sample(obs: 6), obsolescenceDecay: 6);

        var snap = table.Snapshot();
        snap.Destinations.Should().BeEmpty("all routes aged out during the downtime");
        snap.Neighbours.Should().BeEmpty("a neighbour with no surviving route is pruned");
    }

    [Fact]
    public void Restore_replaces_any_existing_contents()
    {
        var table = NewTable();
        table.Restore(Sample(), 0);
        table.Snapshot().DestinationCount.Should().BeGreaterThan(0);

        table.Restore(NetRomRoutingSnapshot.Empty, 0);
        table.Snapshot().DestinationCount.Should().Be(0);
        table.Snapshot().NeighbourCount.Should().Be(0);
    }

    [Fact]
    public void A_negative_decay_is_clamped_to_zero()
    {
        var table = NewTable();
        table.Restore(Sample(obs: 6), obsolescenceDecay: -5);

        table.Snapshot().Destinations.Single(d => d.Destination == Dest)
            .BestRoute!.Obsolescence.Should().Be(6, "a future save-stamp must not inflate obsolescence");
    }

    [Fact]
    public void Restore_round_trips_two_adjacencies_to_one_callsign()
    {
        // The multi-port shape (#725): one station, two ports, two neighbour rows and two routes.
        // Before PC4 both halves collapsed onto the callsign, so a restore silently lost one.
        var heard = new DateTimeOffset(2026, 6, 6, 11, 0, 0, TimeSpan.Zero);
        var snapshot = new NetRomRoutingSnapshot(
            [
                new NetRomDestination(Dest, "SOT",
                [
                    new NetRomRoute(Nbr, "vhf", 200, 6),
                    new NetRomRoute(Nbr, "hf", 150, 6),
                ]),
            ],
            [
                new NetRomNeighbour(Nbr, "RDGBPQ", "vhf", 191, heard),
                new NetRomNeighbour(Nbr, "RDGBPQ", "hf", 150, heard),
            ],
            heard);

        var table = NewTable();
        table.Restore(snapshot, obsolescenceDecay: 0);

        var restored = table.Snapshot();
        restored.Neighbours.Where(n => n.Neighbour == Nbr).Should().HaveCount(2);
        restored.Neighbours.Single(n => n.PortId == "vhf").PathQuality.Should().Be(191);
        restored.Neighbours.Single(n => n.PortId == "hf").PathQuality.Should().Be(150);
        var dest = restored.Destinations.Single(d => d.Destination == Dest);
        dest.Routes.Should().HaveCount(2);
        dest.BestRoute!.PortId.Should().Be("vhf", "the better route is restored as the best one");
    }
}
