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

    // ─── INP3 forwarding-by-time (preferInp3Routes) ─────────────────────

    // Routes carrying BOTH a quality metric (NODES) and an INP3 time-route (RIF). Each entry
    // is (neighbour, quality, targetTimeMs, hopCount). Quality-first ordering as passed.
    private static NetRomRoutingSnapshot Inp3RoutesTo(
        Callsign dest, params (Callsign neighbour, byte quality, int targetTimeMs, byte hop)[] routes)
    {
        var list = routes
            .Select(r => new NetRomRoute(r.neighbour, r.quality, 6, new Inp3RouteMetric(r.targetTimeMs, r.hop)))
            .ToList();
        return new NetRomRoutingSnapshot([new NetRomDestination(dest, "DEST", list)], [], DateTimeOffset.UnixEpoch);
    }

    [Fact]
    public void Prefers_the_lowest_target_time_inp3_route_overriding_quality_and_per_flow()
    {
        // OnwardNbr is the best QUALITY route; AltNbr is the fastest by measured TIME. With
        // preferInp3Routes on, every flow forwards over AltNbr (the time winner) — overriding
        // both the quality ranking AND the per-flow spread (time-space forwards the fastest path).
        var routing = Inp3RoutesTo(Dest, (OnwardNbr, 200, 300, 2), (AltNbr, 100, 100, 3));

        for (byte i = 0; i < 30; i++)
        {
            var d = NetRomForwarding.Decide(
                Flow(Source, Dest, 20, i), FromNbr, Me, routing, 25, NetRomForwardMode.PerFlow, preferInp3Routes: true);
            d.ShouldForward.Should().BeTrue();
            d.NextHop.Should().Be(AltNbr, "the lowest-target-time INP3 route wins for every flow when INP3 is preferred");
        }

        // Knob off ⇒ quality wins, byte-for-byte today (BestRoute picks the highest-quality route).
        NetRomForwarding.Decide(Datagram(Source, Dest, 20), FromNbr, Me, routing, 25, NetRomForwardMode.BestRoute)
            .NextHop.Should().Be(OnwardNbr, "preferInp3Routes defaults off — quality decides");
    }

    [Fact]
    public void preferInp3Routes_off_ignores_the_inp3_metric_entirely()
    {
        // The degenerate-to-today guard: routes carry INP3 metrics that would change the pick,
        // but with the knob off the metric is never read — the quality route is chosen, identical
        // to a node that never heard of INP3.
        var routing = Inp3RoutesTo(Dest, (OnwardNbr, 200, 999, 9), (AltNbr, 100, 1, 1));

        var off = NetRomForwarding.Decide(Datagram(Source, Dest, 20), FromNbr, Me, routing, 25, NetRomForwardMode.BestRoute);
        var defaulted = NetRomForwarding.Decide(Datagram(Source, Dest, 20), FromNbr, Me, routing, 25);   // preferInp3Routes defaults false

        off.NextHop.Should().Be(OnwardNbr, "knob off ⇒ quality wins despite AltNbr's far lower target time");
        defaulted.NextHop.Should().Be(OnwardNbr, "the parameter defaults to off (byte-for-byte today)");
    }

    [Fact]
    public void Falls_back_to_quality_when_preferred_but_no_inp3_route_exists()
    {
        // preferInp3Routes on, but the destination holds only quality routes (no time-route) →
        // fall back to the quality next-hop, exactly as today.
        var routing = RoutesTo(Dest, (OnwardNbr, 200), (AltNbr, 100));

        var d = NetRomForwarding.Decide(
            Datagram(Source, Dest, 20), FromNbr, Me, routing, 25, NetRomForwardMode.BestRoute, preferInp3Routes: true);

        d.NextHop.Should().Be(OnwardNbr, "no INP3 route to prefer → quality fallback");
    }

    [Fact]
    public void Excludes_the_inp3_route_that_arrived_from_and_takes_the_next_best_time()
    {
        // The fastest INP3 route is back the way it came (split-horizon) → excluded; the next
        // lowest-target-time INP3 route is used instead.
        var routing = Inp3RoutesTo(Dest, (FromNbr, 100, 50, 1), (OnwardNbr, 200, 300, 2));

        var d = NetRomForwarding.Decide(
            Datagram(Source, Dest, 20), FromNbr, Me, routing, 25, NetRomForwardMode.PerFlow, preferInp3Routes: true);

        d.NextHop.Should().Be(OnwardNbr, "the time winner is the way it came → use the next-best INP3 route");
    }

    [Fact]
    public void Falls_back_to_quality_when_the_only_inp3_route_is_the_way_it_came()
    {
        // The single INP3 route is back the way it came (excluded); a quality-only alternate
        // exists → fall back to it rather than dropping.
        var routing = new NetRomRoutingSnapshot(
            [new NetRomDestination(Dest, "DEST",
            [
                new NetRomRoute(FromNbr, 100, 6, new Inp3RouteMetric(50, 1)),   // INP3, but the way it came
                new NetRomRoute(AltNbr, 200, 6),                                // quality-only alternate
            ])],
            [], DateTimeOffset.UnixEpoch);

        var d = NetRomForwarding.Decide(
            Datagram(Source, Dest, 20), FromNbr, Me, routing, 25, NetRomForwardMode.BestRoute, preferInp3Routes: true);

        d.NextHop.Should().Be(AltNbr, "no usable INP3 route (the only one is the way it came) → quality fallback");
    }

    [Fact]
    public void Inp3_tie_break_is_target_time_then_hop_then_callsign()
    {
        // Two INP3 routes at the same target time: the lower hop count wins (then, on a hop tie,
        // the lower neighbour callsign ordinal — mirroring Inp3RouteSelector).
        var byHop = Inp3RoutesTo(Dest, (AltNbr, 200, 100, 3), (OnwardNbr, 100, 100, 2));
        NetRomForwarding.Decide(Datagram(Source, Dest, 20), FromNbr, Me, byHop, 25, NetRomForwardMode.PerFlow, preferInp3Routes: true)
            .NextHop.Should().Be(OnwardNbr, "equal target time → fewer hops wins");

        // GB7CCC (OnwardNbr) < GB7DDD (AltNbr) ordinally, equal time + hop → callsign tie-break.
        var byCall = Inp3RoutesTo(Dest, (AltNbr, 200, 100, 2), (OnwardNbr, 100, 100, 2));
        NetRomForwarding.Decide(Datagram(Source, Dest, 20), FromNbr, Me, byCall, 25, NetRomForwardMode.PerFlow, preferInp3Routes: true)
            .NextHop.Should().Be(OnwardNbr, "equal target time + hop → lower callsign ordinal wins");
    }
}
