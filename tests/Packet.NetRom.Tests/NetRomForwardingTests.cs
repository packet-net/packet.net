using System.Linq;
using Packet.Core;
using Packet.NetRom.Routing;
using Packet.NetRom.Wire;
using Xunit;

namespace Packet.NetRom.Tests;

/// <summary>
/// The NET/ROM L3 forwarding decision (<see cref="NetRomForwarding.Decide"/>) — the
/// transit node's verdict on a datagram addressed to someone else: drop (TTL expired
/// / looped / no route) or forward (with a decremented, capped TTL) to a next-hop
/// neighbour. Mirrors the de-facto reference (LinBPQ <c>L4Code.c</c>).
/// </summary>
public sealed class NetRomForwardingTests
{
    private static readonly Callsign Me = new("GB7BBB", 0);     // the forwarding (transit) node
    private static readonly Callsign Source = new("GB7AAA", 0); // the datagram's origin
    private static readonly Callsign Dest = new("GB7CCC", 0);   // the destination (not us)
    private static readonly Callsign FromNbr = new("GB7AAA", 0);// arrived from this neighbour
    private static readonly Callsign OnwardNbr = new("GB7CCC", 0); // the way onward to Dest
    private static readonly Callsign AltNbr = new("GB7DDD", 0); // an alternate next hop

    private static NetRomPacket Datagram(Callsign origin, Callsign dest, byte ttl) => new()
    {
        Network = new NetRomNetworkHeader { Origin = origin, Destination = dest, TimeToLive = ttl },
        Transport = new NetRomTransportHeader
        {
            CircuitIndex = 7,
            CircuitId = 9,
            TxSequence = 3,
            RxSequence = 4,
            Opcode = NetRomOpcode.Information,
            Flags = NetRomTransportFlags.None,
        },
        Payload = new byte[] { 0xDE, 0xAD, 0xBE, 0xEF },
    };

    private static NetRomRoutingSnapshot RoutesTo(Callsign dest, params (Callsign neighbour, byte quality)[] routes)
    {
        // Routes are passed best-first (Decide trusts the snapshot's ordering).
        var list = routes.Select(r => new NetRomRoute(r.neighbour, r.quality, 6)).ToList();
        return new NetRomRoutingSnapshot([new NetRomDestination(dest, "DEST", list)], [], DateTimeOffset.UnixEpoch);
    }

    [Fact]
    public void Forwards_a_transit_datagram_to_the_best_next_hop_with_the_ttl_decremented()
    {
        var packet = Datagram(Source, Dest, ttl: 10);
        var routing = RoutesTo(Dest, (OnwardNbr, 200));

        var decision = NetRomForwarding.Decide(packet, FromNbr, Me, routing, maxTimeToLive: 25);

        decision.ShouldForward.Should().BeTrue();
        decision.NextHop.Should().Be(OnwardNbr);
        decision.Packet.Network.TimeToLive.Should().Be(9, "the hop limit is decremented by one per node");
        decision.Packet.Network.Origin.Should().Be(Source, "the origin/destination are unchanged in transit");
        decision.Packet.Network.Destination.Should().Be(Dest);
        decision.Packet.Transport.Should().Be(packet.Transport, "the L4 content is relayed untouched");
        decision.Packet.Payload.ToArray().Should().Equal(packet.Payload.ToArray());
    }

    [Fact]
    public void Drops_when_the_ttl_reaches_zero()
    {
        // Arrives at TTL 1 → decrements to 0 → end of life, not forwarded.
        var decision = NetRomForwarding.Decide(
            Datagram(Source, Dest, ttl: 1), FromNbr, Me, RoutesTo(Dest, (OnwardNbr, 200)), maxTimeToLive: 25);

        decision.ShouldForward.Should().BeFalse();
        decision.Outcome.Should().Be(NetRomForwarding.ForwardOutcome.DropTtlExpired);
    }

    [Fact]
    public void Caps_the_ttl_at_the_configured_maximum()
    {
        // A peer sends an over-large TTL; we forward it capped at our own L3LIVES so a
        // buggy/hostile frame can't circulate far.
        var decision = NetRomForwarding.Decide(
            Datagram(Source, Dest, ttl: 200), FromNbr, Me, RoutesTo(Dest, (OnwardNbr, 200)), maxTimeToLive: 25);

        decision.ShouldForward.Should().BeTrue();
        decision.Packet.Network.TimeToLive.Should().Be(25);
    }

    [Fact]
    public void Drops_a_datagram_that_looped_back_to_its_origin()
    {
        // Origin is us → the datagram has come back to its start; forwarding it loops.
        var decision = NetRomForwarding.Decide(
            Datagram(Me, Dest, ttl: 10), FromNbr, Me, RoutesTo(Dest, (OnwardNbr, 200)), maxTimeToLive: 25);

        decision.ShouldForward.Should().BeFalse();
        decision.Outcome.Should().Be(NetRomForwarding.ForwardOutcome.DropLooped);
    }

    [Fact]
    public void Drops_when_there_is_no_route_to_the_destination()
    {
        var decision = NetRomForwarding.Decide(
            Datagram(Source, Dest, ttl: 10), FromNbr, Me, NetRomRoutingSnapshot.Empty, maxTimeToLive: 25);

        decision.ShouldForward.Should().BeFalse();
        decision.Outcome.Should().Be(NetRomForwarding.ForwardOutcome.DropNoRoute);
    }

    [Fact]
    public void Does_not_bounce_a_datagram_back_to_the_neighbour_it_arrived_from()
    {
        // The only route to Dest is back via the neighbour it just came from → no
        // usable onward route, so it is dropped rather than ping-ponged.
        var decision = NetRomForwarding.Decide(
            Datagram(Source, Dest, ttl: 10), FromNbr, Me, RoutesTo(Dest, (FromNbr, 200)), maxTimeToLive: 25);

        decision.ShouldForward.Should().BeFalse();
        decision.Outcome.Should().Be(NetRomForwarding.ForwardOutcome.DropNoRoute);
    }

    [Fact]
    public void Prefers_an_alternate_route_when_the_best_is_the_way_it_came()
    {
        // Best route to Dest is back the way it came (via FromNbr); a lower-quality
        // alternate via AltNbr is used instead.
        var routing = RoutesTo(Dest, (FromNbr, 220), (AltNbr, 200));

        var decision = NetRomForwarding.Decide(Datagram(Source, Dest, ttl: 10), FromNbr, Me, routing, maxTimeToLive: 25);

        decision.ShouldForward.Should().BeTrue();
        decision.NextHop.Should().Be(AltNbr);
    }

    // ─── multi-route load-balancing (per-flow, quality-weighted) ────────

    /// <summary>A datagram with a chosen flow key (the L3 origin + L4 circuit
    /// index/id are what `FlowHash` keys on; vary the index/id to make distinct
    /// flows).</summary>
    private static NetRomPacket Flow(Callsign origin, Callsign dest, byte ttl, byte circuitIndex, byte circuitId = 0) => new()
    {
        Network = new NetRomNetworkHeader { Origin = origin, Destination = dest, TimeToLive = ttl },
        Transport = new NetRomTransportHeader
        {
            CircuitIndex = circuitIndex,
            CircuitId = circuitId,
            TxSequence = 0,
            RxSequence = 0,
            Opcode = NetRomOpcode.Information,
            Flags = NetRomTransportFlags.None,
        },
        Payload = ReadOnlyMemory<byte>.Empty,
    };

    [Fact]
    public void Per_flow_pins_a_circuit_to_one_route_regardless_of_ttl_or_sequence()
    {
        // Two equal routes; the same flow (same origin + circuit index/id) must take
        // the same route across datagrams (so the circuit's L4 ordering is preserved).
        var routing = RoutesTo(Dest, (OnwardNbr, 200), (AltNbr, 200));

        var a = NetRomForwarding.Decide(Flow(Source, Dest, 20, 5), FromNbr, Me, routing, 25, NetRomForwardMode.PerFlow);
        var b = NetRomForwarding.Decide(Flow(Source, Dest, 9, 5), FromNbr, Me, routing, 25, NetRomForwardMode.PerFlow);

        a.ShouldForward.Should().BeTrue();
        a.NextHop.Should().Be(b.NextHop, "every datagram of one circuit hashes to the same route");
    }

    [Fact]
    public void Per_flow_spreads_distinct_circuits_across_the_kept_routes()
    {
        var routing = RoutesTo(Dest, (OnwardNbr, 200), (AltNbr, 200));
        var counts = new Dictionary<string, int>();

        for (byte i = 0; i < 60; i++)
        {
            var d = NetRomForwarding.Decide(Flow(Source, Dest, 20, i), FromNbr, Me, routing, 25, NetRomForwardMode.PerFlow);
            counts[d.NextHop.ToString()] = counts.GetValueOrDefault(d.NextHop.ToString()) + 1;
        }

        counts.Should().ContainKey(OnwardNbr.ToString(), "distinct circuits should use both routes");
        counts.Should().ContainKey(AltNbr.ToString());
    }

    [Fact]
    public void Per_flow_weights_the_spread_by_route_quality()
    {
        // 2:1 quality → the higher-quality route should carry meaningfully more flows.
        var routing = RoutesTo(Dest, (OnwardNbr, 200), (AltNbr, 100));
        int onward = 0, alt = 0;

        for (int i = 0; i < 256; i++)
        {
            var d = NetRomForwarding.Decide(Flow(Source, Dest, 20, (byte)i), FromNbr, Me, routing, 25, NetRomForwardMode.PerFlow);
            if (d.NextHop.Equals(OnwardNbr)) onward++;
            else if (d.NextHop.Equals(AltNbr)) alt++;
        }

        onward.Should().BeGreaterThan(0);
        alt.Should().BeGreaterThan(0);
        onward.Should().BeGreaterThan(alt, "the higher-quality route carries proportionally more flows");
    }

    [Fact]
    public void Best_route_mode_ignores_the_flow_and_always_takes_the_single_best_route()
    {
        var routing = RoutesTo(Dest, (OnwardNbr, 200), (AltNbr, 100));

        for (byte i = 0; i < 20; i++)
        {
            var d = NetRomForwarding.Decide(Flow(Source, Dest, 20, i), FromNbr, Me, routing, 25, NetRomForwardMode.BestRoute);
            d.NextHop.Should().Be(OnwardNbr, "BestRoute mode always uses the best route, whatever the flow");
        }
    }
}
