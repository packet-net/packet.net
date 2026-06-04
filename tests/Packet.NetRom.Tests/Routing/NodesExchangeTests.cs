using Microsoft.Extensions.Time.Testing;
using Packet.Core;
using Packet.NetRom.Routing;
using Packet.NetRom.Wire;

namespace Packet.NetRom.Tests.Routing;

/// <summary>
/// Deterministic two-node NODES-exchange test (FakeTimeProvider, no wall-clock):
/// node A originates a NODES broadcast from its table via
/// <see cref="NodesBroadcastBuilder"/>, node B ingests it through the production
/// parser + <see cref="NetRomRoutingTable"/>, and the route appears in B's table at
/// the multiplicatively-decayed quality — the L3-origination ↔ L3-ingest round-trip
/// the read-only slice only had the ingest half of.
/// </summary>
public sealed class NodesExchangeTests
{
    private static readonly Callsign ANode = new("GB7AAA", 0);
    private static readonly Callsign BNode = new("GB7BBB", 0);
    private static readonly Callsign DistantSot = new("GB7SOT", 0);
    private static readonly Callsign ViaHub = new("GB7HUB", 0);

    [Fact]
    public void Node_B_learns_node_A_and_its_routes_from_A_broadcast()
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 6, 4, 12, 0, 0, TimeSpan.Zero));

        // Node A's table knows a route to GB7SOT via GB7HUB at quality 200 (its own
        // best route). Build A's outgoing NODES broadcast from it.
        var tableA = new NetRomRoutingTable(NetRomRoutingOptions.Default, time);
        // Seed A's table by having it hear HUB advertise SOT (so A has a real route).
        var hubBroadcast = new NodesBroadcast
        {
            SenderAlias = "HUB",
            Entries = new[]
            {
                new NodesRoutingEntry { Destination = DistantSot, DestinationAlias = "SOT", BestNeighbour = ViaHub, BestQuality = 255 },
            },
        };
        tableA.Ingest(ViaHub, ANode, "p1", hubBroadcast);

        var entries = tableA.BuildAdvertisement(obsoleteMinimum: 0);
        var frames = NodesBroadcastBuilder.Build("AAANOD", entries);
        frames.Should().NotBeEmpty();

        // Node B hears A's broadcast (A is the UI-frame source / originator).
        var tableB = new NetRomRoutingTable(NetRomRoutingOptions.Default, time);
        foreach (var frame in frames)
        {
            NodesBroadcast.TryParse(frame, out var parsed).Should().BeTrue();
            tableB.Ingest(ANode, BNode, "p1", parsed!);
        }

        var snapB = tableB.Snapshot();

        // B learned A as a directly-heard neighbour (assumed direct route).
        snapB.Neighbours.Should().ContainSingle().Which.Neighbour.Should().Be(ANode);
        snapB.Destinations.Should().Contain(d => d.Destination == ANode);

        // B learned SOT via A, at the quality A advertised (A's 192-ish to HUB ×
        // 255, then B's 192 path to A) — strictly decayed below A's advertised value.
        var sot = snapB.Destinations.SingleOrDefault(d => d.Destination == DistantSot);
        sot.Should().NotBeNull("B learned the distant destination A advertised");
        sot!.BestRoute!.Neighbour.Should().Be(ANode, "B forwards to A to reach SOT");
        sot.Alias.Should().Be("SOT");

        // Quality strictly decreased over the extra hop (multiplicative decay).
        byte advertisedByA = entries.Single(e => e.Destination == DistantSot).Quality;
        sot.BestRoute.Quality.Should().BeLessThan(advertisedByA, "an extra hop multiplicatively decays quality");
    }

    [Fact]
    public void OBSMIN_gate_stops_advertising_a_faded_route_before_it_is_purged()
    {
        var time = new FakeTimeProvider();
        // OBSINIT 6, OBSMIN 4: after two sweeps a route is at obs 4 (still advertised);
        // after three it is at 3 (kept + usable, but no longer advertised).
        var opts = NetRomRoutingOptions.Default with { ObsoleteInitial = 6, ObsoleteMinimum = 4 };
        var table = new NetRomRoutingTable(opts, time);

        table.Ingest(ViaHub, ANode, "p1", new NodesBroadcast
        {
            SenderAlias = "HUB",
            Entries = new[]
            {
                new NodesRoutingEntry { Destination = DistantSot, DestinationAlias = "SOT", BestNeighbour = ViaHub, BestQuality = 255 },
            },
        });

        // Fresh: obs 6 ≥ OBSMIN 4 → SOT (and HUB itself) advertised.
        table.BuildAdvertisement(opts.ObsoleteMinimum).Should().Contain(e => e.Destination == DistantSot);

        table.Sweep();   // 6 → 5
        table.Sweep();   // 5 → 4  (still ≥ 4)
        table.BuildAdvertisement(opts.ObsoleteMinimum).Should().Contain(e => e.Destination == DistantSot);

        table.Sweep();   // 4 → 3  (< OBSMIN 4)
        table.BuildAdvertisement(opts.ObsoleteMinimum).Should().NotContain(e => e.Destination == DistantSot,
            "a route below OBSMIN is kept + usable but no longer advertised");

        // It is still in the table (resolvable for routing) — not yet purged.
        table.Snapshot().ResolveDestination("SOT").Should().NotBeNull();
    }
}
