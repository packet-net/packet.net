using AwesomeAssertions;
using Microsoft.Extensions.Time.Testing;
using Packet.Core;
using Packet.NetRom.Routing;
using Packet.NetRom.Wire;
using Xunit;

namespace Packet.NetRom.Tests.Routing;

/// <summary>
/// Tests for INP3 RIF <b>emission</b>
/// (<see cref="NetRomRoutingTable.BuildRif"/>) — the
/// poison-reversed, per-target-neighbour RIF the node advertises (the time-space analogue
/// of <see cref="NetRomRoutingTable.BuildAdvertisement(int)"/>). The locked emission rules
/// are <c>docs/netrom-inp3-i4-design.md</c> §1 (content) / §2 (poison-reverse): own node
/// at 0/0 first and never poisoned; a destination is advertised iff we HOLD an INP3
/// time-route for it (at our best held target time — independent of the local forwarding
/// preference); but a destination reached through the target neighbour via <em>any</em> kept
/// route is advertised back at the 600 s horizon (the poison), covering the whole multi-route
/// forwarding set so a two-hop loop can never form; alias TLVs gated off.
/// </summary>
public sealed class Inp3BuildRifTests
{
    // The port every single-port case in this file ingests on. A neighbour is keyed
    // (port, callsign), so a RIF is filed under the adjacency it arrived on.
    private const string Port = "vhf";
    private static readonly Callsign Me = new("M0LTE", 0);
    private static readonly Callsign NbrA = new("GB7RDG", 0);   // a neighbour
    private static readonly Callsign NbrB = new("GB7XYZ", 0);   // a second neighbour
    private static readonly Callsign DestSot = new("GB7SOT", 0);
    private static readonly Callsign DestMnc = new("GB7MNC", 0);

    private static NetRomRoutingTable NewTable()
        => NewTable(NetRomRoutingOptions.Default);

    private static NetRomRoutingTable NewTable(NetRomRoutingOptions options)
    {
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 6, 8, 12, 0, 0, TimeSpan.Zero));
        return new NetRomRoutingTable(options, clock);
    }

    private static Inp3Rip Rip(Callsign destination, byte hopCount, int targetTimeMs)
        => new()
        {
            Destination = destination,
            HopCount = hopCount,
            TargetTimeMs = targetTimeMs,
            Tlvs = [],
        };

    private static Inp3Rif Rif(params Inp3Rip[] rips) => new() { Rips = rips };

    private static Inp3Rip RipFor(Inp3Rif rif, Callsign dest)
        => rif.Rips.Single(r => r.Destination == dest);

    // ─── Own-node source RIP (invariant (Source)) ───

    [Fact]
    public void Empty_table_emits_just_the_own_node_rip_at_zero_zero()
    {
        var table = NewTable();

        var rif = table.BuildRif(Me, NbrA);

        rif.Rips.Should().ContainSingle("an empty table advertises only our own-node source RIP");
        var own = rif.Rips[0];
        own.Destination.Should().Be(Me);
        own.TargetTimeMs.Should().Be(0, "the cost to reach us from us is zero");
        own.HopCount.Should().Be(0, "in zero hops");
        own.Tlvs.Should().BeEmpty("alias TLV emission is gated off (AMBIGUITY-I4-1)");
        own.IsHorizon.Should().BeFalse("the source is never poisoned");
    }

    [Fact]
    public void Own_node_rip_is_always_first_and_is_zero_zero_regardless_of_table_state()
    {
        var table = NewTable();
        table.IngestRif(NbrA, Port, Me, neighbourSnttMs: 50, Rif(Rip(DestSot, hopCount: 1, targetTimeMs: 100)));
        table.IngestRif(NbrB, Port, Me, neighbourSnttMs: 20, Rif(Rip(DestMnc, hopCount: 1, targetTimeMs: 100)));

        foreach (var toward in new[] { NbrA, NbrB })
        {
            var rif = table.BuildRif(Me, toward);
            rif.Rips[0].Destination.Should().Be(Me, "the own-node RIP is always first");
            rif.Rips[0].TargetTimeMs.Should().Be(0);
            rif.Rips[0].HopCount.Should().Be(0);
            rif.Rips.Count(r => r.Destination == Me).Should().Be(1, "exactly one own-node RIP");
        }
    }

    [Fact]
    public void A_rif_built_toward_us_never_poisons_our_own_node()
    {
        // Degenerate: building toward ourselves (the loop-guard identity == target neighbour).
        var table = NewTable();
        table.IngestRif(NbrA, Port, Me, neighbourSnttMs: 50, Rif(Rip(DestSot, hopCount: 1, targetTimeMs: 100)));

        var rif = table.BuildRif(Me, Me);

        var own = rif.Rips.Single(r => r.Destination == Me);
        own.TargetTimeMs.Should().Be(0, "our own node is exempt from poison-reverse — always 0/0");
        own.IsHorizon.Should().BeFalse();
    }

    // ─── Content (§1.1) ───

    [Fact]
    public void Each_selected_inp3_route_becomes_one_destination_rip_at_its_quantised_target_time()
    {
        var table = NewTable();
        // 100 + 73 + 10 = 183 ms stored; emitted floored to the 10 ms wire granule → 180.
        table.IngestRif(NbrA, Port, Me, neighbourSnttMs: 73, Rif(Rip(DestSot, hopCount: 1, targetTimeMs: 100)));

        var rif = table.BuildRif(Me, NbrB);   // toward a DIFFERENT neighbour → no poison

        var rip = RipFor(rif, DestSot);
        rip.TargetTimeMs.Should().Be(180, "183 ms stored, quantised down to the 10 ms wire granule");
        rip.HopCount.Should().Be(2, "the selected route's local hop count (peer 1 + 1 through us)");
        rip.Tlvs.Should().BeEmpty("no alias TLV (gated off)");
        rip.IsHorizon.Should().BeFalse("a finite, reachable destination toward a non-via neighbour");
    }

    [Fact]
    public void A_quality_only_destination_is_not_in_the_rif()
    {
        var table = NewTable();
        // A NODES quality route only — no INP3 time-route — must not appear in the RIF.
        table.Ingest(NbrA, Me, "vhf", Nodes("RDG", (DestSot, "SOT", NbrA, 200)));

        var rif = table.BuildRif(Me, NbrB);

        rif.Rips.Should().ContainSingle("only the own-node RIP — the quality-only dest is carried by NODES, not the RIF");
        rif.Rips[0].Destination.Should().Be(Me);
    }

    [Fact]
    public void Destination_rips_are_ordered_by_ascending_target_time_then_callsign_after_the_own_node_rip()
    {
        var table = NewTable();
        // Two destinations, faster via the same neighbour so neither is poisoned toward NbrB.
        table.IngestRif(NbrA, Port, Me, neighbourSnttMs: 200, Rif(Rip(DestSot, hopCount: 1, targetTimeMs: 100)));   // 310
        table.IngestRif(NbrA, Port, Me, neighbourSnttMs: 20, Rif(Rip(DestMnc, hopCount: 1, targetTimeMs: 100)));    // 130

        var rif = table.BuildRif(Me, NbrB);

        rif.Rips[0].Destination.Should().Be(Me, "own-node RIP first");
        rif.Rips[1].Destination.Should().Be(DestMnc, "then the lowest target time (130) first");
        rif.Rips[2].Destination.Should().Be(DestSot, "then the slower one (310)");
    }

    // ─── Poison-reverse (the headline correctness feature, §2 / invariant (P)) ───

    [Fact]
    public void A_dest_via_N_is_poisoned_at_the_horizon_in_the_rif_toward_N()
    {
        var table = NewTable();
        // SOT is reached via NbrA. The RIF toward NbrA must poison SOT.
        table.IngestRif(NbrA, Port, Me, neighbourSnttMs: 50, Rif(Rip(DestSot, hopCount: 1, targetTimeMs: 100)));

        var towardA = table.BuildRif(Me, NbrA);

        var rip = RipFor(towardA, DestSot);
        rip.TargetTimeMs.Should().Be(Inp3Rip.HorizonMs, "SOT is via NbrA — poison it back to NbrA at the horizon");
        rip.IsHorizon.Should().BeTrue("the horizon marks the destination unreachable to NbrA via us");
    }

    [Fact]
    public void The_same_dest_is_finite_in_the_rif_toward_a_different_neighbour()
    {
        var table = NewTable();
        // SOT via NbrA: poisoned toward NbrA, but advertised at its real time toward NbrB.
        table.IngestRif(NbrA, Port, Me, neighbourSnttMs: 50, Rif(Rip(DestSot, hopCount: 1, targetTimeMs: 100)));

        var towardA = table.BuildRif(Me, NbrA);
        var towardB = table.BuildRif(Me, NbrB);

        RipFor(towardA, DestSot).TargetTimeMs.Should().Be(Inp3Rip.HorizonMs, "poisoned toward its own next hop (NbrA)");
        RipFor(towardB, DestSot).TargetTimeMs.Should().Be(160, "advertised at its real time (100+50+10) toward a different neighbour");
        RipFor(towardB, DestSot).IsHorizon.Should().BeFalse();
    }

    [Fact]
    public void Poison_reverse_covers_every_kept_next_hop_not_just_the_best()
    {
        var table = NewTable();
        // SOT reachable via BOTH neighbours. The shipped multi-route LB forwards SOT traffic
        // over BOTH, so advertising SOT back at a finite metric to EITHER seeds a loop — both
        // must be poisoned, not just the faster/best one.
        table.IngestRif(NbrA, Port, Me, neighbourSnttMs: 200, Rif(Rip(DestSot, hopCount: 1, targetTimeMs: 100)));   // 310 via NbrA
        table.IngestRif(NbrB, Port, Me, neighbourSnttMs: 20, Rif(Rip(DestSot, hopCount: 1, targetTimeMs: 100)));    // 130 via NbrB

        RipFor(table.BuildRif(Me, NbrA), DestSot).IsHorizon.Should().BeTrue("SOT is reached via NbrA → poison toward NbrA");
        RipFor(table.BuildRif(Me, NbrB), DestSot).IsHorizon.Should().BeTrue("SOT is reached via NbrB too → poison toward NbrB");

        // Toward a neighbour that is NOT one of SOT's next hops, advertise the best (130) finite.
        var rip = RipFor(table.BuildRif(Me, new Callsign("GB7ZZZ", 0)), DestSot);
        rip.IsHorizon.Should().BeFalse("toward a non-next-hop neighbour, SOT is advertised finite");
        rip.TargetTimeMs.Should().Be(130, "at the best (lowest) INP3 target time we hold for it");
    }

    // ─── Invariant (P′): never a finite metric back to a route's own next hop ───

    [Fact]
    public void Emitter_never_advertises_a_finite_metric_back_to_a_routes_own_next_hop()
    {
        // A spread of destinations, each selected via one of two neighbours. For EVERY
        // neighbour N and EVERY destination D whose selected next hop is N, the RIF toward N
        // must carry D at the horizon — the operational restatement of invariant (P).
        var table = NewTable();
        var n1 = new Callsign("GB7AAA", 0);
        var n2 = new Callsign("GB7BBB", 0);
        var d1 = new Callsign("GB7DDD", 0);
        var d2 = new Callsign("GB7EEE", 0);
        var d3 = new Callsign("GB7FFF", 0);

        table.IngestRif(n1, Port, Me, neighbourSnttMs: 10, Rif(Rip(d1, hopCount: 1, targetTimeMs: 100)));   // d1 via n1
        table.IngestRif(n2, Port, Me, neighbourSnttMs: 10, Rif(Rip(d2, hopCount: 1, targetTimeMs: 100)));   // d2 via n2
        table.IngestRif(n1, Port, Me, neighbourSnttMs: 10, Rif(Rip(d3, hopCount: 1, targetTimeMs: 100)));   // d3 via n1

        foreach (var toward in new[] { n1, n2 })
        {
            var rif = table.BuildRif(Me, toward);
            foreach (var rip in rif.Rips)
            {
                if (rip.Destination.Equals(Me))
                {
                    rip.IsHorizon.Should().BeFalse("the own-node RIP is never poisoned");
                    continue;
                }

                // Assert the (D reached via toward through ANY kept route) ⟹ horizon
                // implication holds on emitter output — split-horizon over the full
                // forwarding next-hop set, not just the best route.
                var dest = table.Snapshot().Destinations.Single(x => x.Destination == rip.Destination);
                if (dest.Routes.Any(r => r.Neighbour.Equals(toward)))
                {
                    rip.TargetTimeMs.Should().Be(Inp3Rip.HorizonMs,
                        $"{rip.Destination} is via {toward} — it must be poisoned in the RIF toward {toward}");
                }
                else
                {
                    rip.IsHorizon.Should().BeFalse(
                        $"{rip.Destination} is NOT via {toward} — it must carry a finite metric");
                }
            }
        }
    }

    // ─── emission is holding-based, independent of the forwarding preference ───

    [Fact]
    public void A_held_inp3_route_is_advertised_regardless_of_the_forwarding_preference()
    {
        var table = NewTable();
        // Emission advertises every destination we HOLD an INP3 time-route for, so neighbours
        // learn the time topology — even on a node that forwards by quality. preferInp3Routes is
        // a local *forwarding* preference (Inp3RouteSelector / I-3), not an advertisement gate.
        table.IngestRif(NbrA, Port, Me, neighbourSnttMs: 50, Rif(Rip(DestSot, hopCount: 1, targetTimeMs: 100)));   // 160 via NbrA

        var rif = table.BuildRif(Me, NbrB);   // toward a different neighbour → finite

        var rip = RipFor(rif, DestSot);
        rip.Destination.Should().Be(DestSot);
        rip.IsHorizon.Should().BeFalse();
        rip.TargetTimeMs.Should().Be(160, "the held INP3 route is advertised at its target time regardless of the forwarding preference");
    }

    // ─── helpers ───

    private static NodesBroadcast Nodes(string senderAlias, params (Callsign Dest, string Alias, Callsign Via, byte Q)[] entries)
    {
        var info = TestNodesEncoder.Build(senderAlias, entries);
        NodesBroadcast.TryParse(info, out var bc).Should().BeTrue();
        return bc!;
    }
}
