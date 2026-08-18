using AwesomeAssertions;
using Microsoft.Extensions.Time.Testing;
using Packet.Core;
using Packet.NetRom.Routing;
using Packet.NetRom.Wire;
using Xunit;

namespace Packet.NetRom.Tests.Routing;

/// <summary>
/// Tests for INP3 RIF ingestion into the routing table
/// (<see cref="NetRomRoutingTable.IngestRif(Callsign, Callsign, uint, Inp3Rif, int)"/>) -
/// the second metric space (measured target time, lowest-best) learned alongside the
/// NODES quality space. The locked ingestion math is
/// <c>localTargetTimeMs = rip.TargetTimeMs + neighbourSnttMs + 10</c>,
/// <c>localHopCount = rip.HopCount + 1</c>, with the 600 s horizon withdrawing the
/// dest-via-neighbour INP3 route (design doc <c>docs/netrom-inp3-i3-design.md</c> §2 / §5.2).
/// </summary>
public sealed class Inp3IngestTests
{
    // The port every single-port case in this file ingests on. A neighbour is keyed
    // (port, callsign), so a RIF is filed under the adjacency it arrived on.
    private const string Port = "vhf";
    private static readonly Callsign Me = new("M0LTE", 0);
    private static readonly Callsign NbrA = new("GB7RDG", 0);   // the interlink RIF arrived on
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

    private static Inp3Rip Rip(Callsign destination, byte hopCount, int targetTimeMs, string? alias = null)
        => new()
        {
            Destination = destination,
            HopCount = hopCount,
            TargetTimeMs = targetTimeMs,
            Tlvs = alias is null ? [] : [Inp3Tlv.Alias(alias)],
        };

    private static Inp3Rif Rif(params Inp3Rip[] rips) => new() { Rips = rips };

    private static NetRomRoute? RouteVia(NetRomRoutingTable table, Callsign dest, Callsign via)
    {
        var d = table.Snapshot().Destinations.FirstOrDefault(x => x.Destination == dest);
        return d?.Routes.FirstOrDefault(r => r.Neighbour == via);
    }

    // ─── Upsert ───

    [Fact]
    public void Ingesting_a_rif_learns_an_inp3_time_route_via_the_carrying_neighbour()
    {
        var table = NewTable();
        table.IngestRif(NbrA, Port, Me, neighbourSnttMs: 50, Rif(Rip(DestSot, hopCount: 1, targetTimeMs: 100, alias: "SOT")));

        var route = RouteVia(table, DestSot, NbrA);
        route.Should().NotBeNull("the RIF teaches a route to SOT via the neighbour it arrived on");
        route!.Neighbour.Should().Be(NbrA);
        route.Inp3.Should().NotBeNull("the route carries an INP3 metric");
        var dest = table.Snapshot().Destinations.Single(d => d.Destination == DestSot);
        dest.Alias.Should().Be("SOT", "the RIP's alias TLV names the destination");
    }

    [Fact]
    public void A_pure_inp3_route_has_quality_zero_so_it_is_invisible_to_the_quality_path()
    {
        var table = NewTable();
        table.IngestRif(NbrA, Port, Me, neighbourSnttMs: 50, Rif(Rip(DestSot, hopCount: 1, targetTimeMs: 100)));

        var route = RouteVia(table, DestSot, NbrA);
        route!.Quality.Should().Be(0, "a route known only via INP3 carries no NODES quality");
        route.Inp3.Should().NotBeNull();
    }

    [Fact]
    public void Re_ingesting_the_same_dest_via_the_same_neighbour_refreshes_the_metric_in_place()
    {
        var table = NewTable();
        table.IngestRif(NbrA, Port, Me, neighbourSnttMs: 50, Rif(Rip(DestSot, hopCount: 1, targetTimeMs: 100)));
        table.IngestRif(NbrA, Port, Me, neighbourSnttMs: 50, Rif(Rip(DestSot, hopCount: 1, targetTimeMs: 300)));

        var dest = table.Snapshot().Destinations.Single(d => d.Destination == DestSot);
        dest.Routes.Should().ContainSingle("the same (dest, via) is one route, refreshed not duplicated");
        dest.Routes[0].Inp3!.TargetTimeMs.Should().Be(300 + 50 + 10, "the metric is the latest RIP's, recomputed");
    }

    // ─── Target-time accumulation ───

    [Fact]
    public void Local_target_time_is_peer_time_plus_link_sntt_plus_ten_ms_per_hop()
    {
        var table = NewTable();
        // peer says 100 ms to SOT in 2 hops; our link to the neighbour measures 75 ms.
        table.IngestRif(NbrA, Port, Me, neighbourSnttMs: 75, Rif(Rip(DestSot, hopCount: 2, targetTimeMs: 100)));

        var route = RouteVia(table, DestSot, NbrA);
        route!.Inp3!.TargetTimeMs.Should().Be(100 + 75 + 10, "peer target + link SNTT + 10 ms/hop");
        route.Inp3.HopCount.Should().Be(3, "one more hop — through us");
    }

    [Fact]
    public void Per_hop_increment_keeps_target_time_strictly_increasing_across_a_zero_ms_link()
    {
        var table = NewTable();
        // A same-host / loopback link measures ~0 ms and the peer advertises 0 ms.
        table.IngestRif(NbrA, Port, Me, neighbourSnttMs: 0, Rif(Rip(DestSot, hopCount: 0, targetTimeMs: 0)));

        var route = RouteVia(table, DestSot, NbrA);
        route!.Inp3!.TargetTimeMs.Should().Be(10, "the +10 ms per-hop floor keeps the metric > 0 even at zero cost");
        route.Inp3.HopCount.Should().Be(1);
    }

    [Fact]
    public void Full_millisecond_precision_is_kept_not_requantised_to_the_ten_ms_granule()
    {
        var table = NewTable();
        // Wire target time is always a 10 ms multiple, but the SNTT need not be - 73 ms here.
        table.IngestRif(NbrA, Port, Me, neighbourSnttMs: 73, Rif(Rip(DestSot, hopCount: 1, targetTimeMs: 100)));

        var route = RouteVia(table, DestSot, NbrA);
        route!.Inp3!.TargetTimeMs.Should().Be(183, "100 + 73 + 10 — full ms, not rounded to a 10 ms granule");
    }

    [Fact]
    public void Best_inp3_route_per_destination_is_the_lowest_target_time()
    {
        var table = NewTable();
        // Two neighbours both reach SOT; via NbrB is the faster path.
        table.IngestRif(NbrA, Port, Me, neighbourSnttMs: 200, Rif(Rip(DestSot, hopCount: 1, targetTimeMs: 100)));   // 310
        table.IngestRif(NbrB, Port, Me, neighbourSnttMs: 20, Rif(Rip(DestSot, hopCount: 1, targetTimeMs: 100)));    // 130

        var dest = table.Snapshot().Destinations.Single(d => d.Destination == DestSot);
        var bestInp3 = dest.Routes
            .Where(r => r.Inp3 is not null)
            .OrderBy(r => r.Inp3!.TargetTimeMs)
            .ThenBy(r => r.Inp3!.HopCount)
            .ThenBy(r => r.Neighbour.ToString(), StringComparer.Ordinal)
            .First();
        bestInp3.Neighbour.Should().Be(NbrB, "the lowest-target-time INP3 route is via the faster neighbour");
        bestInp3.Inp3!.TargetTimeMs.Should().Be(130);
    }

    // ─── Horizon withdraws ───

    [Fact]
    public void A_rip_at_or_over_the_horizon_is_a_withdrawal_clearing_the_inp3_metric()
    {
        var table = NewTable();
        table.IngestRif(NbrA, Port, Me, neighbourSnttMs: 50, Rif(Rip(DestSot, hopCount: 1, targetTimeMs: 100)));
        RouteVia(table, DestSot, NbrA)!.Inp3.Should().NotBeNull("learned first");

        // The peer now advertises SOT at the 600 s horizon → withdrawal.
        table.IngestRif(NbrA, Port, Me, neighbourSnttMs: 50, Rif(Rip(DestSot, hopCount: 1, targetTimeMs: Inp3Rip.HorizonMs)));

        RouteVia(table, DestSot, NbrA).Should().BeNull(
            "the route had no quality metric, so withdrawing its only (INP3) metric removes the route");
        table.Snapshot().Destinations.Should().NotContain(d => d.Destination == DestSot,
            "and the destination left with no route is removed");
    }

    [Fact]
    public void A_computed_target_time_reaching_the_horizon_also_withdraws()
    {
        var table = NewTable();
        table.IngestRif(NbrA, Port, Me, neighbourSnttMs: 50, Rif(Rip(DestSot, hopCount: 1, targetTimeMs: 100)));

        // peer target just under the horizon, but the link SNTT pushes the computed
        // value to/over it → withdrawal even though rip.IsHorizon is false.
        table.IngestRif(NbrA, Port, Me, neighbourSnttMs: 100,
            Rif(Rip(DestSot, hopCount: 1, targetTimeMs: Inp3Rip.HorizonMs - 10)));

        RouteVia(table, DestSot, NbrA).Should().BeNull("100 + (599_990) + 10 ≥ 600_000 → over the horizon → withdrawn");
    }

    [Fact]
    public void Withdrawal_clears_only_the_inp3_metric_and_leaves_a_coexisting_quality_route()
    {
        var table = NewTable();
        // First a NODES quality route, then an INP3 metric attached to the same (dest, via).
        table.Ingest(NbrA, Me, "vhf", Nodes("RDG", (DestSot, "SOT", NbrA, 200)));
        table.IngestRif(NbrA, Port, Me, neighbourSnttMs: 50, Rif(Rip(DestSot, hopCount: 1, targetTimeMs: 100)));
        var both = RouteVia(table, DestSot, NbrA)!;
        both.Quality.Should().BeGreaterThan(0);
        both.Inp3.Should().NotBeNull();

        // Withdraw via the horizon - the quality route must survive, the INP3 metric cleared.
        table.IngestRif(NbrA, Port, Me, neighbourSnttMs: 50, Rif(Rip(DestSot, hopCount: 1, targetTimeMs: Inp3Rip.HorizonMs)));

        var after = RouteVia(table, DestSot, NbrA);
        after.Should().NotBeNull("the quality route survives a time-route withdrawal");
        after!.Inp3.Should().BeNull("only the INP3 metric was withdrawn");
        after.Quality.Should().Be(NetRomQuality.Combine(200, 192), "the quality metric is untouched");
    }

    [Fact]
    public void An_unset_sntt_never_withdraws_a_route_it_never_learned()
    {
        var table = NewTable();
        // A NODES quality route exists; no SNTT measured for this link yet.
        table.Ingest(NbrA, Me, "vhf", Nodes("RDG", (DestSot, "SOT", NbrA, 200)));

        // A non-horizon RIP arrives but the link is un-probed: skip - do NOT withdraw.
        table.IngestRif(NbrA, Port, Me, neighbourSnttMs: Inp3Sntt.Unset, Rif(Rip(DestSot, hopCount: 1, targetTimeMs: 100)));

        var route = RouteVia(table, DestSot, NbrA);
        route.Should().NotBeNull("an un-probed link learns no time-route and must not disturb the quality route");
        route!.Inp3.Should().BeNull("no time-route learned (link cost unknown)");
        route.Quality.Should().BeGreaterThan(0, "the quality route is intact");
    }

    [Fact]
    public void A_horizon_rip_withdraws_even_when_the_link_is_unmeasured()
    {
        var table = NewTable();
        table.IngestRif(NbrA, Port, Me, neighbourSnttMs: 50, Rif(Rip(DestSot, hopCount: 1, targetTimeMs: 100)));
        RouteVia(table, DestSot, NbrA)!.Inp3.Should().NotBeNull();

        // Even with no current SNTT measurement, an explicit horizon RIP is a withdrawal.
        table.IngestRif(NbrA, Port, Me, neighbourSnttMs: Inp3Sntt.Unset, Rif(Rip(DestSot, hopCount: 1, targetTimeMs: Inp3Rip.HorizonMs)));

        RouteVia(table, DestSot, NbrA).Should().BeNull("an explicit horizon RIP withdraws regardless of SNTT state");
    }

    // ─── Hop limit ───

    [Fact]
    public void A_rip_whose_local_hop_count_exceeds_the_hop_limit_is_not_learned()
    {
        var table = NewTable();
        // hopLimit 5: a RIP at 5 hops becomes 6 local → over the limit → not learned.
        table.IngestRif(NbrA, Port, Me, neighbourSnttMs: 50, Rif(Rip(DestSot, hopCount: 5, targetTimeMs: 100)), hopLimit: 5);

        RouteVia(table, DestSot, NbrA).Should().BeNull("local hop 6 > hopLimit 5");
    }

    [Fact]
    public void A_rip_at_exactly_the_hop_limit_is_learned()
    {
        var table = NewTable();
        // hopLimit 5: a RIP at 4 hops becomes 5 local → at the limit → learned.
        table.IngestRif(NbrA, Port, Me, neighbourSnttMs: 50, Rif(Rip(DestSot, hopCount: 4, targetTimeMs: 100)), hopLimit: 5);

        var route = RouteVia(table, DestSot, NbrA);
        route.Should().NotBeNull("local hop 5 == hopLimit 5 is within the horizon");
        route!.Inp3!.HopCount.Should().Be(5);
    }

    [Fact]
    public void The_default_hop_limit_is_thirty()
    {
        NetRomRoutingTable.DefaultHopLimit.Should().Be(30);
        var table = NewTable();
        // 29 hops → 30 local, learned at the default; 30 hops → 31 local, dropped.
        table.IngestRif(NbrA, Port, Me, neighbourSnttMs: 50, Rif(Rip(DestSot, hopCount: 29, targetTimeMs: 100)));
        table.IngestRif(NbrB, Port, Me, neighbourSnttMs: 50, Rif(Rip(DestMnc, hopCount: 30, targetTimeMs: 100)));

        RouteVia(table, DestSot, NbrA).Should().NotBeNull("30 hops is within the default limit");
        RouteVia(table, DestMnc, NbrB).Should().BeNull("31 hops exceeds the default limit");
    }

    // ─── Trivial-loop guard ───

    [Fact]
    public void A_rip_whose_destination_is_us_is_skipped()
    {
        var table = NewTable();
        table.IngestRif(NbrA, Port, Me, neighbourSnttMs: 50, Rif(Rip(Me, hopCount: 1, targetTimeMs: 100)));

        table.Snapshot().Destinations.Should().NotContain(d => d.Destination == Me,
            "a route to ourselves is never learned (trivial-loop guard)");
    }

    // ─── Route cap ───

    [Fact]
    public void The_per_destination_route_cap_is_respected_evicting_lowest_quality_first()
    {
        var options = NetRomRoutingOptions.Default with { MaxRoutesPerDestination = 2 };
        var table = NewTable(options);
        var n1 = new Callsign("GB7AAA", 0);
        var n2 = new Callsign("GB7BBB", 0);
        var n3 = new Callsign("GB7CCC", 0);

        // Three INP3-only routes (all quality 0) to SOT via three neighbours → capped to 2.
        table.IngestRif(n1, Port, Me, neighbourSnttMs: 10, Rif(Rip(DestSot, hopCount: 1, targetTimeMs: 100)));
        table.IngestRif(n2, Port, Me, neighbourSnttMs: 20, Rif(Rip(DestSot, hopCount: 1, targetTimeMs: 100)));
        table.IngestRif(n3, Port, Me, neighbourSnttMs: 30, Rif(Rip(DestSot, hopCount: 1, targetTimeMs: 100)));

        var dest = table.Snapshot().Destinations.Single(d => d.Destination == DestSot);
        dest.Routes.Should().HaveCount(2, "the per-destination route cap is 2");
    }

    [Fact]
    public void An_inp3_only_route_is_evicted_in_favour_of_a_quality_route_when_capped()
    {
        var options = NetRomRoutingOptions.Default with { MaxRoutesPerDestination = 1 };
        var table = NewTable(options);
        // A quality route via NbrA, then an INP3-only route via NbrB: cap 1 keeps the
        // higher-quality (NbrA) route - eviction is quality-first (AMBIGUITY-I3-2).
        table.Ingest(NbrA, Me, "vhf", Nodes("RDG", (DestSot, "SOT", NbrA, 200)));
        table.IngestRif(NbrB, Port, Me, neighbourSnttMs: 10, Rif(Rip(DestSot, hopCount: 1, targetTimeMs: 1)));

        var dest = table.Snapshot().Destinations.Single(d => d.Destination == DestSot);
        dest.Routes.Should().ContainSingle();
        dest.Routes[0].Neighbour.Should().Be(NbrA, "the quality route is kept; the INP3-only (quality 0) route is evicted");
    }

    // ─── Coexistence with quality routes ───

    [Fact]
    public void Inp3_ingestion_attaches_a_time_metric_to_an_existing_quality_route_without_disturbing_it()
    {
        var table = NewTable();
        table.Ingest(NbrA, Me, "vhf", Nodes("RDG", (DestSot, "SOT", NbrA, 200)));
        var qualityOnly = RouteVia(table, DestSot, NbrA)!;
        qualityOnly.Inp3.Should().BeNull("a NODES-only route has no time metric");
        var q = qualityOnly.Quality;
        var obs = qualityOnly.Obsolescence;

        table.IngestRif(NbrA, Port, Me, neighbourSnttMs: 50, Rif(Rip(DestSot, hopCount: 1, targetTimeMs: 100)));

        var both = RouteVia(table, DestSot, NbrA)!;
        both.Quality.Should().Be(q, "the quality metric is untouched by INP3 ingestion");
        both.Obsolescence.Should().Be(obs, "the obsolescence of the quality route is untouched");
        both.Inp3.Should().NotBeNull("the time metric is attached to the same route");
        both.Inp3!.TargetTimeMs.Should().Be(160);
    }

    [Fact]
    public void A_nodes_refresh_does_not_wipe_a_coexisting_inp3_metric()
    {
        var table = NewTable();
        table.Ingest(NbrA, Me, "vhf", Nodes("RDG", (DestSot, "SOT", NbrA, 200)));
        table.IngestRif(NbrA, Port, Me, neighbourSnttMs: 50, Rif(Rip(DestSot, hopCount: 1, targetTimeMs: 100)));
        RouteVia(table, DestSot, NbrA)!.Inp3.Should().NotBeNull();

        // A later NODES broadcast refreshes the quality - the time metric must survive.
        table.Ingest(NbrA, Me, "vhf", Nodes("RDG", (DestSot, "SOT", NbrA, 100)));

        var route = RouteVia(table, DestSot, NbrA)!;
        route.Inp3.Should().NotBeNull("a NODES quality refresh must not wipe the coexisting time-route");
        route.Inp3!.TargetTimeMs.Should().Be(160);
        route.Quality.Should().Be(NetRomQuality.Combine(100, 192), "and the quality is the refreshed value");
    }

    [Fact]
    public void A_quality_route_and_a_distinct_time_route_coexist_under_one_destination()
    {
        var table = NewTable();
        // Quality route to SOT via NbrA (NODES); a time route to SOT via NbrB (RIF).
        table.Ingest(NbrA, Me, "vhf", Nodes("RDG", (DestSot, "SOT", NbrA, 200)));
        table.IngestRif(NbrB, Port, Me, neighbourSnttMs: 20, Rif(Rip(DestSot, hopCount: 1, targetTimeMs: 100)));

        var dest = table.Snapshot().Destinations.Single(d => d.Destination == DestSot);
        dest.Routes.Should().HaveCount(2, "one route per next-hop neighbour, two distinct metric carriers");
        dest.Routes.Single(r => r.Neighbour == NbrA).Inp3.Should().BeNull("the NbrA route is quality-only");
        dest.Routes.Single(r => r.Neighbour == NbrA).Quality.Should().BeGreaterThan(0);
        dest.Routes.Single(r => r.Neighbour == NbrB).Inp3.Should().NotBeNull("the NbrB route is time-only");
        dest.Routes.Single(r => r.Neighbour == NbrB).Quality.Should().Be(0);
    }

    // ─── One station, two ports: RIF ingest is per (port, neighbour) too (#725) ───

    [Fact]
    public void A_rif_from_one_station_over_two_ports_keeps_two_time_routes()
    {
        var table = NewTable();
        // The same backbone peer is reachable on both bands and RIFs over each. The links have
        // genuinely different costs (SNTT 20 ms vs 200 ms), so blending them into one time-route
        // would corrupt the very measurement INP3 exists to carry.
        table.IngestRif(NbrA, "vhf", Me, neighbourSnttMs: 20, Rif(Rip(DestSot, hopCount: 1, targetTimeMs: 100)));
        table.IngestRif(NbrA, "hf", Me, neighbourSnttMs: 200, Rif(Rip(DestSot, hopCount: 1, targetTimeMs: 100)));

        var dest = table.Snapshot().Destinations.Single(d => d.Destination == DestSot);
        dest.Routes.Should().HaveCount(2, "a RIF is filed under the adjacency it arrived on");
        dest.Routes.Select(r => r.Neighbour).Should().AllBeEquivalentTo(NbrA);
        dest.Routes.Single(r => r.PortId == "vhf").Inp3!.TargetTimeMs
            .Should().BeLessThan(dest.Routes.Single(r => r.PortId == "hf").Inp3!.TargetTimeMs,
                "each route carries ITS OWN link's measured cost");
    }

    [Fact]
    public void A_horizon_withdrawal_on_one_port_leaves_the_other_ports_time_route()
    {
        var table = NewTable();
        table.IngestRif(NbrA, "vhf", Me, neighbourSnttMs: 20, Rif(Rip(DestSot, hopCount: 1, targetTimeMs: 100)));
        table.IngestRif(NbrA, "hf", Me, neighbourSnttMs: 200, Rif(Rip(DestSot, hopCount: 1, targetTimeMs: 100)));

        // The hf side withdraws SOT at the horizon. The vhf time-route is a different route.
        table.IngestRif(NbrA, "hf", Me, neighbourSnttMs: 200, Rif(Rip(DestSot, hopCount: 1, targetTimeMs: Inp3Rip.HorizonMs)));

        var dest = table.Snapshot().Destinations.Single(d => d.Destination == DestSot);
        dest.Routes.Should().ContainSingle().Which.PortId.Should().Be("vhf",
            "withdrawal is per route, so one band's horizon RIP must not withdraw the other's");
        table.RecentlyWithdrawn().Should().NotContain(DestSot,
            "the destination has NOT left the INP3 space - it still holds a time-route on the other port");
    }

    // ─── helpers ───

    private static NodesBroadcast Nodes(string senderAlias, params (Callsign Dest, string Alias, Callsign Via, byte Q)[] entries)
    {
        var info = TestNodesEncoder.Build(senderAlias, entries);
        NodesBroadcast.TryParse(info, out var bc).Should().BeTrue();
        return bc!;
    }
}
