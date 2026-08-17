using AwesomeAssertions;
using Microsoft.Extensions.Time.Testing;
using Packet.Core;
using Packet.NetRom.Routing;
using Packet.NetRom.Wire;
using Xunit;

namespace Packet.NetRom.Tests.Routing;

/// <summary>
/// Tests for the INP3 <b>recently-withdrawn</b> set on <see cref="NetRomRoutingTable"/>
/// (invariant W, design <c>docs/netrom-inp3-host-integration-design.md</c> §6): when a
/// destination loses its <b>last</b> <c>Inp3</c>-bearing route — withdrawn at the horizon in
/// <see cref="NetRomRoutingTable.IngestRif"/>, dropped by
/// <see cref="NetRomRoutingTable.MarkNeighbourDown"/>, or aged out by
/// <see cref="NetRomRoutingTable.Sweep"/> — it enters <see cref="NetRomRoutingTable.RecentlyWithdrawn"/>
/// (a read-only peek), the host <see cref="NetRomRoutingTable.DrainRecentlyWithdrawn"/>s it ONCE at
/// the start of a fan-out round (atomic snapshot+clear), and
/// <see cref="NetRomRoutingTable.BuildRif"/> emits one horizon RIP per entry of the snapshot the
/// host passes to it. The headline is that the drained snapshot, handed to every neighbour's
/// BuildRif, carries the withdrawal to each (so a mid-round add is captured by the next drain, not
/// cleared unadvertised), and the load-bearing default-off guard: a quality-only
/// MarkNeighbourDown / Sweep (the INP3-off path) never touches the set.
/// </summary>
public sealed class Inp3RecentlyWithdrawnTests
{
    // The port every single-port case in this file ingests on. A neighbour is keyed
    // (port, callsign), so a RIF is filed under the adjacency it arrived on.
    private const string Port = "vhf";
    private static readonly Callsign Me = new("M0LTE", 0);
    private static readonly Callsign NbrA = new("GB7RDG", 0);
    private static readonly Callsign NbrB = new("GB7XYZ", 0);
    private static readonly Callsign DestSot = new("GB7SOT", 0);
    private static readonly Callsign DestMnc = new("GB7MNC", 0);

    private static NetRomRoutingTable NewTable()
    {
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 6, 8, 12, 0, 0, TimeSpan.Zero));
        return new NetRomRoutingTable(NetRomRoutingOptions.Default, clock);
    }

    private static Inp3Rip Rip(Callsign destination, byte hopCount, int targetTimeMs)
        => new() { Destination = destination, HopCount = hopCount, TargetTimeMs = targetTimeMs, Tlvs = [] };

    private static Inp3Rif Rif(params Inp3Rip[] rips) => new() { Rips = rips };

    private static NodesBroadcast Nodes(string senderAlias, params (Callsign Dest, string Alias, Callsign Via, byte Q)[] entries)
    {
        var info = TestNodesEncoder.Build(senderAlias, entries);
        NodesBroadcast.TryParse(info, out var bc).Should().BeTrue();
        return bc!;
    }

    // ─── Population: where an INP3 route fully leaves ───

    [Fact]
    public void Ingesting_a_horizon_rip_withdraws_the_last_inp3_route_and_records_it()
    {
        var table = NewTable();
        // Learn an INP3 route to SOT via NbrA, then a horizon RIP withdraws it.
        table.IngestRif(NbrA, Port, Me, neighbourSnttMs: 50, Rif(Rip(DestSot, hopCount: 1, targetTimeMs: 100)));
        table.RecentlyWithdrawn().Should().BeEmpty("learning a route is not a withdrawal");

        table.IngestRif(NbrA, Port, Me, neighbourSnttMs: 50, Rif(Rip(DestSot, hopCount: 1, targetTimeMs: Inp3Rip.HorizonMs)));

        table.RecentlyWithdrawn().Should().ContainSingle().Which.Should().Be(DestSot,
            "SOT lost its last INP3 route at the horizon → it is recently-withdrawn");
    }

    [Fact]
    public void MarkNeighbourDown_records_a_destination_that_loses_its_last_inp3_route()
    {
        var table = NewTable();
        table.IngestRif(NbrA, Port, Me, neighbourSnttMs: 50, Rif(Rip(DestSot, hopCount: 1, targetTimeMs: 100)));

        table.MarkNeighbourDown(new NeighbourKey(Port, NbrA));

        table.RecentlyWithdrawn().Should().ContainSingle().Which.Should().Be(DestSot,
            "dropping NbrA removed SOT's only (INP3) route → SOT is withdrawn from the time-space");
    }

    [Fact]
    public void Sweep_records_a_destination_whose_last_inp3_route_ages_out()
    {
        var table = NewTable();
        table.IngestRif(NbrA, Port, Me, neighbourSnttMs: 50, Rif(Rip(DestSot, hopCount: 1, targetTimeMs: 100)));

        // OBSINIT default is 6 → sweep it down to 0 to purge the route.
        for (int i = 0; i < 6; i++)
        {
            table.Sweep();
        }

        table.RecentlyWithdrawn().Should().ContainSingle().Which.Should().Be(DestSot,
            "SOT's only (INP3) route aged out → it left the INP3 space");
    }

    [Fact]
    public void A_destination_that_keeps_another_inp3_route_is_NOT_withdrawn()
    {
        var table = NewTable();
        // SOT reachable via BOTH neighbours. Dropping one leaves the other → not withdrawn.
        table.IngestRif(NbrA, Port, Me, neighbourSnttMs: 50, Rif(Rip(DestSot, hopCount: 1, targetTimeMs: 100)));
        table.IngestRif(NbrB, Port, Me, neighbourSnttMs: 20, Rif(Rip(DestSot, hopCount: 1, targetTimeMs: 100)));

        table.MarkNeighbourDown(new NeighbourKey(Port, NbrA));

        table.RecentlyWithdrawn().Should().BeEmpty(
            "SOT still has an INP3 route via NbrB — it has not left the time-space");
    }

    [Fact]
    public void Withdrawing_one_route_when_another_inp3_route_survives_does_not_record()
    {
        var table = NewTable();
        table.IngestRif(NbrA, Port, Me, neighbourSnttMs: 50, Rif(Rip(DestSot, hopCount: 1, targetTimeMs: 100)));
        table.IngestRif(NbrB, Port, Me, neighbourSnttMs: 20, Rif(Rip(DestSot, hopCount: 1, targetTimeMs: 100)));

        // Withdraw only the NbrA route at the horizon — NbrB's INP3 route survives.
        table.IngestRif(NbrA, Port, Me, neighbourSnttMs: 50, Rif(Rip(DestSot, hopCount: 1, targetTimeMs: Inp3Rip.HorizonMs)));

        table.RecentlyWithdrawn().Should().BeEmpty("an INP3 route to SOT still exists via NbrB");
    }

    // ─── The default-off guard (design §7.1) — the load-bearing detail ───

    [Fact]
    public void A_quality_only_MarkNeighbourDown_never_populates_the_set()
    {
        var table = NewTable();
        // A vanilla NODES quality route only — no IngestRif ever called. This is the
        // INP3-off world (the L4 dial-failure path runs MarkNeighbourDown with INP3 off).
        table.Ingest(NbrA, Me, "vhf", Nodes("RDG", (DestSot, "SOT", NbrA, 200)));

        table.MarkNeighbourDown(new NeighbourKey(Port, NbrA));

        table.RecentlyWithdrawn().Should().BeEmpty(
            "a quality-only neighbour-down (the INP3-off path) must never touch the withdrawn set");
    }

    [Fact]
    public void A_quality_only_Sweep_never_populates_the_set()
    {
        var table = NewTable();
        table.Ingest(NbrA, Me, "vhf", Nodes("RDG", (DestSot, "SOT", NbrA, 200)));

        for (int i = 0; i < 10; i++)
        {
            table.Sweep();   // age the quality route all the way out
        }

        table.RecentlyWithdrawn().Should().BeEmpty(
            "a quality-only sweep (INP3 off) must never touch the withdrawn set; BuildRif is moot");
    }

    [Fact]
    public void A_route_that_keeps_its_quality_after_inp3_withdrawal_is_still_recorded()
    {
        var table = NewTable();
        // A route carrying BOTH a quality metric (NODES) and an INP3 metric (RIF).
        table.Ingest(NbrA, Me, "vhf", Nodes("RDG", (DestSot, "SOT", NbrA, 200)));
        table.IngestRif(NbrA, Port, Me, neighbourSnttMs: 50, Rif(Rip(DestSot, hopCount: 1, targetTimeMs: 100)));

        // Withdraw the INP3 metric at the horizon. The quality route SURVIVES (SOT stays in
        // the table for NODES) but SOT has left the INP3 time-space → recorded (the
        // "lost its last INP3 route" trigger, not "lost its last route").
        table.IngestRif(NbrA, Port, Me, neighbourSnttMs: 50, Rif(Rip(DestSot, hopCount: 1, targetTimeMs: Inp3Rip.HorizonMs)));

        table.RecentlyWithdrawn().Should().ContainSingle().Which.Should().Be(DestSot);
        table.Snapshot().Destinations.Should().Contain(d => d.Destination == DestSot,
            "the quality route survives — SOT is still reachable by NODES");
    }

    // ─── BuildRif emits from the host-drained snapshot; the drain clears atomically ───

    [Fact]
    public void BuildRif_emits_one_horizon_rip_for_each_withdrawn_destination_in_the_snapshot()
    {
        var table = NewTable();
        table.IngestRif(NbrA, Port, Me, neighbourSnttMs: 50, Rif(Rip(DestSot, hopCount: 1, targetTimeMs: 100)));
        table.MarkNeighbourDown(new NeighbourKey(Port, NbrA));   // SOT withdrawn

        // The host hands BuildRif the snapshot it drained (here peeked, to keep this single-RIF case
        // simple). One explicit one-shot horizon withdrawal per entry.
        var rif = table.BuildRif(Me, NbrB, table.RecentlyWithdrawn());

        var sotRip = rif.Rips.Should().ContainSingle(r => r.Destination == DestSot).Subject;
        sotRip.TargetTimeMs.Should().Be(Inp3Rip.HorizonMs, "an explicit one-shot horizon withdrawal");
        sotRip.IsHorizon.Should().BeTrue();
    }

    [Fact]
    public void BuildRif_with_no_snapshot_omits_withdrawals()
    {
        var table = NewTable();
        table.IngestRif(NbrA, Port, Me, neighbourSnttMs: 50, Rif(Rip(DestSot, hopCount: 1, targetTimeMs: 100)));
        table.MarkNeighbourDown(new NeighbourKey(Port, NbrA));   // SOT withdrawn, but…

        // …a BuildRif that is NOT handed the withdrawn snapshot (the default — e.g. a pure
        // poison-reverse RIF, or a caller that does not drive the withdrawn set) appends no
        // withdrawal RIPs. The set is unaffected (BuildRif never touches it).
        table.BuildRif(Me, NbrB).Rips.Should().NotContain(r => r.Destination == DestSot,
            "no snapshot passed ⇒ no one-shot horizon withdrawals");
        table.RecentlyWithdrawn().Should().ContainSingle("BuildRif does not consume the set — only the drain does");
    }

    [Fact]
    public void The_drained_snapshot_carries_the_withdrawal_to_every_neighbour()
    {
        var table = NewTable();
        table.IngestRif(NbrA, Port, Me, neighbourSnttMs: 50, Rif(Rip(DestSot, hopCount: 1, targetTimeMs: 100)));
        table.MarkNeighbourDown(new NeighbourKey(Port, NbrA));   // SOT withdrawn

        // The host drains ONCE at the round start (atomic snapshot+clear), then fans the SAME
        // snapshot out to every neighbour. Both RIFs carry the horizon withdrawal, and the live set
        // is already empty (drained) — so a concurrent mid-round add lands in the NEXT round's drain.
        var snapshot = table.DrainRecentlyWithdrawn();
        var towardB = table.BuildRif(Me, NbrB, snapshot);
        var towardC = table.BuildRif(Me, new Callsign("GB7ZZZ", 0), snapshot);

        towardB.Rips.Single(r => r.Destination == DestSot).IsHorizon.Should().BeTrue("first neighbour's RIF");
        towardC.Rips.Single(r => r.Destination == DestSot).IsHorizon.Should().BeTrue("second neighbour's RIF — same snapshot");
        table.RecentlyWithdrawn().Should().BeEmpty("the drain cleared the live set atomically");
    }

    [Fact]
    public void DrainRecentlyWithdrawn_returns_then_empties_so_a_later_rif_omits_the_withdrawal()
    {
        var table = NewTable();
        table.IngestRif(NbrA, Port, Me, neighbourSnttMs: 50, Rif(Rip(DestSot, hopCount: 1, targetTimeMs: 100)));
        table.MarkNeighbourDown(new NeighbourKey(Port, NbrA));

        var drained = table.DrainRecentlyWithdrawn();
        drained.Should().ContainSingle().Which.Should().Be(DestSot, "the drain returns the snapshot");
        table.BuildRif(Me, NbrB, drained).Rips.Should().Contain(r => r.Destination == DestSot, "the round carries it once");

        table.RecentlyWithdrawn().Should().BeEmpty("the drain cleared the set");
        table.BuildRif(Me, NbrB, table.DrainRecentlyWithdrawn()).Rips.Should().NotContain(r => r.Destination == DestSot,
            "after the drain, SOT is absent from every RIF until a fresh IngestRif re-learns it");
    }

    [Fact]
    public void A_re_learned_destination_is_carried_finite_not_poisoned_in_the_same_round()
    {
        var table = NewTable();
        // SOT withdrawn via NbrA…
        table.IngestRif(NbrA, Port, Me, neighbourSnttMs: 50, Rif(Rip(DestSot, hopCount: 1, targetTimeMs: 100)));
        table.MarkNeighbourDown(new NeighbourKey(Port, NbrA));
        table.RecentlyWithdrawn().Should().Contain(DestSot);

        // …then re-learned via NbrB in the SAME round (before the host drains). It now holds a
        // finite INP3 route again, so BuildRif must carry it FINITE (its real metric), not as a
        // horizon withdrawal — the emitted-finite-dest is excluded from the horizon-RIP pass even
        // though it is still in the withdrawn snapshot the host passes.
        table.IngestRif(NbrB, Port, Me, neighbourSnttMs: 20, Rif(Rip(DestSot, hopCount: 1, targetTimeMs: 100)));

        var rif = table.BuildRif(Me, NbrA, table.RecentlyWithdrawn());   // toward NbrA: SOT is NOT via NbrA anymore → finite, not poisoned
        var sotRip = rif.Rips.Single(r => r.Destination == DestSot);
        sotRip.IsHorizon.Should().BeFalse("SOT was re-learned finite — carried by its real metric, not the withdrawal");
        sotRip.TargetTimeMs.Should().Be(130, "100 + 20 (NbrB SNTT) + 10 per-hop, quantised");
        rif.Rips.Count(r => r.Destination == DestSot).Should().Be(1, "exactly one RIP for SOT — finite, not both finite + horizon");
    }

    [Fact]
    public void The_own_node_is_never_emitted_as_a_withdrawal()
    {
        var table = NewTable();
        // Force Me into the withdrawn set via the public clear/build contract is impossible
        // (the table never withdraws itself), but assert BuildRif never poisons Me even with a
        // populated set — the Source invariant.
        table.IngestRif(NbrA, Port, Me, neighbourSnttMs: 50, Rif(Rip(DestSot, hopCount: 1, targetTimeMs: 100)));
        table.MarkNeighbourDown(new NeighbourKey(Port, NbrA));

        var rif = table.BuildRif(Me, NbrB, table.RecentlyWithdrawn());
        rif.Rips.Single(r => r.Destination == Me).IsHorizon.Should().BeFalse("our own node is never withdrawn (Source invariant)");
        rif.Rips[0].Destination.Should().Be(Me, "own-node RIP first, at 0/0");
    }

    [Fact]
    public void Re_withdrawing_after_a_drain_re_populates_the_set()
    {
        var table = NewTable();
        table.IngestRif(NbrA, Port, Me, neighbourSnttMs: 50, Rif(Rip(DestSot, hopCount: 1, targetTimeMs: 100)));
        table.MarkNeighbourDown(new NeighbourKey(Port, NbrA));
        table.DrainRecentlyWithdrawn();
        table.RecentlyWithdrawn().Should().BeEmpty();

        // A fresh learn-then-withdraw cycle re-populates it (the next round re-advertises).
        table.IngestRif(NbrB, Port, Me, neighbourSnttMs: 20, Rif(Rip(DestSot, hopCount: 1, targetTimeMs: 100)));
        table.MarkNeighbourDown(new NeighbourKey(Port, NbrB));

        table.RecentlyWithdrawn().Should().ContainSingle().Which.Should().Be(DestSot);
    }

    [Fact]
    public void Multiple_withdrawn_destinations_are_returned_in_stable_ordinal_order()
    {
        var table = NewTable();
        table.IngestRif(NbrA, Port, Me, neighbourSnttMs: 50, Rif(
            Rip(DestSot, hopCount: 1, targetTimeMs: 100),
            Rip(DestMnc, hopCount: 1, targetTimeMs: 100)));
        table.MarkNeighbourDown(new NeighbourKey(Port, NbrA));   // both lose their last INP3 route

        table.RecentlyWithdrawn().Should().Equal(
            [DestMnc, DestSot],   // GB7MNC < GB7SOT ordinally
            "stable ordinal ordering for deterministic, cross-stack-comparable RIFs");
    }
}
