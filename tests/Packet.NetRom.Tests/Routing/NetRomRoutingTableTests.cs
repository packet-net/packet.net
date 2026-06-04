using AwesomeAssertions;
using Microsoft.Extensions.Time.Testing;
using Packet.Core;
using Packet.NetRom.Routing;
using Packet.NetRom.Wire;
using Xunit;

namespace Packet.NetRom.Tests.Routing;

public class NetRomRoutingTableTests
{
    private static readonly Callsign Me = new("M0LTE", 0);
    private static readonly Callsign NbrA = new("GB7RDG", 0);   // a heard neighbour (originator)
    private static readonly Callsign NbrB = new("GB7XYZ", 0);   // another heard neighbour
    private static readonly Callsign DestSot = new("GB7SOT", 0);
    private static readonly Callsign DestMnc = new("GB7MNC", 0);

    private static NodesBroadcast Broadcast(string senderAlias, params (Callsign Dest, string Alias, Callsign Via, byte Q)[] entries)
    {
        var info = NodesBroadcastBuilder.Build(senderAlias, entries);
        NodesBroadcast.TryParse(info, out var bc).Should().BeTrue();
        return bc!;
    }

    private static NetRomRoutingTable NewTable(out FakeTimeProvider clock)
        => NewTable(NetRomRoutingOptions.Default, out clock);

    private static NetRomRoutingTable NewTable(NetRomRoutingOptions options, out FakeTimeProvider clock)
    {
        clock = new FakeTimeProvider(new DateTimeOffset(2026, 6, 4, 12, 0, 0, TimeSpan.Zero));
        return new NetRomRoutingTable(options, clock);
    }

    [Fact]
    public void Hearing_a_broadcast_records_the_originator_as_a_neighbour()
    {
        var table = NewTable(out var clock);
        table.Ingest(NbrA, Me, "vhf", Broadcast("RDGBPQ"));

        var snap = table.Snapshot();
        snap.Neighbours.Should().ContainSingle();
        var n = snap.Neighbours[0];
        n.Neighbour.Should().Be(NbrA);
        n.Alias.Should().Be("RDGBPQ");
        n.PortId.Should().Be("vhf");
        n.PathQuality.Should().Be(192);   // default neighbour quality
        n.LastHeard.Should().Be(clock.GetUtcNow());
    }

    [Fact]
    public void A_direct_route_to_the_originator_is_assumed_at_path_quality()
    {
        var table = NewTable(out _);
        table.Ingest(NbrA, Me, "vhf", Broadcast("RDGBPQ"));

        var snap = table.Snapshot();
        var dest = snap.Destinations.Single(d => d.Destination == NbrA);
        dest.BestRoute.Should().NotBeNull();
        dest.BestRoute!.Neighbour.Should().Be(NbrA);
        dest.BestRoute.Quality.Should().Be(192);
    }

    [Fact]
    public void An_advertised_destination_is_learned_at_the_combined_quality()
    {
        var table = NewTable(out _);
        // RDG advertises it can reach SOT via XYZ at quality 200. Our path to RDG
        // is the default 192. Derived = (200*192 + 128)/256 = 150.5 → 150.
        table.Ingest(NbrA, Me, "vhf", Broadcast("RDGBPQ", (DestSot, "SOT", NbrB, 200)));

        var snap = table.Snapshot();
        var sot = snap.Destinations.Single(d => d.Destination == DestSot);
        sot.Alias.Should().Be("SOT");
        sot.BestRoute!.Neighbour.Should().Be(NbrA);          // we forward to RDG (the originator)
        sot.BestRoute.Quality.Should().Be(NetRomQuality.Combine(200, 192));   // 150
    }

    [Fact]
    public void Trivial_loop_guard_zeroes_a_route_whose_best_neighbour_is_us()
    {
        var table = NewTable(out _);
        // RDG advertises a destination reachable via US (M0LTE) — a loop. The route
        // becomes quality 0, which is never kept, so DestMnc gets no route.
        table.Ingest(NbrA, Me, "vhf", Broadcast("RDGBPQ", (DestMnc, "MNC", Me, 200)));

        var snap = table.Snapshot();
        snap.Destinations.Should().NotContain(d => d.Destination == DestMnc);
    }

    [Fact]
    public void Keeps_only_the_three_best_routes_per_destination()
    {
        var table = NewTable(out _);
        // Four different neighbours each advertise SOT at different qualities.
        // Each is a distinct originator, so we learn four routes — capped to 3.
        var n1 = new Callsign("GB7AAA", 0);
        var n2 = new Callsign("GB7BBB", 0);
        var n3 = new Callsign("GB7CCC", 0);
        var n4 = new Callsign("GB7DDD", 0);
        table.Ingest(n1, Me, "vhf", Broadcast("AAA", (DestSot, "SOT", n1, 250)));
        table.Ingest(n2, Me, "vhf", Broadcast("BBB", (DestSot, "SOT", n2, 200)));
        table.Ingest(n3, Me, "vhf", Broadcast("CCC", (DestSot, "SOT", n3, 150)));
        table.Ingest(n4, Me, "vhf", Broadcast("DDD", (DestSot, "SOT", n4, 100)));

        var sot = table.Snapshot().Destinations.Single(d => d.Destination == DestSot);
        sot.Routes.Should().HaveCount(3, "the per-destination route cap is 3");
        // Best-first, and the weakest (via n4, derived from 100) is dropped.
        sot.Routes.Select(r => r.Quality).Should().BeInDescendingOrder();
        sot.Routes.Should().NotContain(r => r.Neighbour == n4);
    }

    [Fact]
    public void Re_advertising_updates_the_route_in_place_not_duplicates_it()
    {
        var table = NewTable(out _);
        table.Ingest(NbrA, Me, "vhf", Broadcast("RDG", (DestSot, "SOT", NbrB, 200)));
        table.Ingest(NbrA, Me, "vhf", Broadcast("RDG", (DestSot, "SOT", NbrB, 100)));

        var sot = table.Snapshot().Destinations.Single(d => d.Destination == DestSot);
        sot.Routes.Should().ContainSingle("the same (dest, via-neighbour) is one route, refreshed");
        sot.BestRoute!.Quality.Should().Be(NetRomQuality.Combine(100, 192));
    }

    // ─── Obsolescence ───

    [Fact]
    public void A_route_is_initialised_to_obsinit_and_decremented_each_sweep()
    {
        var table = NewTable(out _);
        table.Ingest(NbrA, Me, "vhf", Broadcast("RDG", (DestSot, "SOT", NbrB, 200)));

        table.Snapshot().Destinations.Single(d => d.Destination == DestSot)
            .BestRoute!.Obsolescence.Should().Be(6);   // OBSINIT default

        table.Sweep();
        table.Snapshot().Destinations.Single(d => d.Destination == DestSot)
            .BestRoute!.Obsolescence.Should().Be(5);
    }

    [Fact]
    public void A_route_is_purged_when_its_obsolescence_reaches_zero()
    {
        var options = NetRomRoutingOptions.Default with { ObsoleteInitial = 2 };
        var table = NewTable(options, out _);
        table.Ingest(NbrA, Me, "vhf", Broadcast("RDG", (DestSot, "SOT", NbrB, 200)));

        table.Sweep();   // 2 -> 1
        table.Snapshot().Destinations.Should().Contain(d => d.Destination == DestSot);

        int purged = table.Sweep();   // 1 -> 0 → purge
        purged.Should().BeGreaterThan(0);
        table.Snapshot().Destinations.Should().NotContain(d => d.Destination == DestSot);
    }

    [Fact]
    public void A_fresh_broadcast_resets_obsolescence_back_to_obsinit()
    {
        var table = NewTable(out _);
        table.Ingest(NbrA, Me, "vhf", Broadcast("RDG", (DestSot, "SOT", NbrB, 200)));
        table.Sweep();   // 6 -> 5
        table.Sweep();   // 5 -> 4
        table.Ingest(NbrA, Me, "vhf", Broadcast("RDG", (DestSot, "SOT", NbrB, 200)));   // refresh

        table.Snapshot().Destinations.Single(d => d.Destination == DestSot)
            .BestRoute!.Obsolescence.Should().Be(6);
    }

    [Fact]
    public void Sweeping_a_purged_destinations_only_neighbour_drops_the_neighbour_too()
    {
        var options = NetRomRoutingOptions.Default with { ObsoleteInitial = 1 };
        var table = NewTable(options, out _);
        table.Ingest(NbrA, Me, "vhf", Broadcast("RDG", (DestSot, "SOT", NbrB, 200)));

        table.Snapshot().Neighbours.Should().ContainSingle();
        table.Sweep();   // purges both the direct route to RDG and the SOT route
        var snap = table.Snapshot();
        snap.Destinations.Should().BeEmpty();
        snap.Neighbours.Should().BeEmpty("a neighbour with no surviving route is an orphan");
    }

    // ─── Quality floor (MINQUAL) ───

    [Fact]
    public void A_route_below_the_minqual_floor_is_dropped_by_a_higher_floor_but_kept_by_the_default()
    {
        // RDG advertises SOT via XYZ at quality 80 → derived (80*192+128)/256 = 60.
        var bc = Broadcast("RDG", (DestSot, "SOT", NbrB, 80));

        // Default floor (0): the route is learned.
        var lenient = NewTable(out _);
        lenient.Ingest(NbrA, Me, "vhf", bc);
        lenient.Snapshot().Destinations.Should().Contain(d => d.Destination == DestSot);

        // Raised floor (MINQUAL 128): the derived 60 is below the floor → dropped.
        var strict = NewTable(NetRomRoutingOptions.Default with { MinQuality = 128 }, out _);
        strict.Ingest(NbrA, Me, "vhf", bc);
        strict.Snapshot().Destinations.Should().NotContain(d => d.Destination == DestSot);
    }

    [Fact]
    public void A_re_advertisement_that_falls_below_the_floor_removes_the_existing_route()
    {
        var table = NewTable(NetRomRoutingOptions.Default with { MinQuality = 128 }, out _);
        table.Ingest(NbrA, Me, "vhf", Broadcast("RDG", (DestSot, "SOT", NbrB, 250)));   // derived 187 — kept
        table.Snapshot().Destinations.Should().Contain(d => d.Destination == DestSot);

        table.Ingest(NbrA, Me, "vhf", Broadcast("RDG", (DestSot, "SOT", NbrB, 80)));    // derived 60 — below floor
        var sot = table.Snapshot().Destinations.FirstOrDefault(d => d.Destination == DestSot);
        sot.Should().BeNull("the route decayed below the floor and the destination has no other route");
    }

    // ─── Destination cap ───

    [Fact]
    public void The_destination_list_stops_growing_at_the_cap()
    {
        var options = NetRomRoutingOptions.Default with { MaxDestinations = 2 };
        var table = NewTable(options, out _);

        // Originator NbrA itself counts as one destination (its assumed direct
        // route). Advertise two more distinct destinations; only one fits.
        table.Ingest(NbrA, Me, "vhf", Broadcast("RDG",
            (DestSot, "SOT", NbrB, 200),
            (DestMnc, "MNC", NbrB, 200)));

        var snap = table.Snapshot();
        snap.DestinationCount.Should().Be(2, "the originator + one advertised destination fill the cap");
    }

    // ─── Snapshot shape ───

    [Fact]
    public void Snapshot_orders_destinations_by_alias_then_callsign()
    {
        var table = NewTable(out _);
        table.Ingest(NbrA, Me, "vhf", Broadcast("RDG",
            (DestSot, "SOT", NbrB, 200),
            (DestMnc, "MNC", NbrB, 200)));

        var snap = table.Snapshot();
        var aliases = snap.Destinations.Select(d => d.Alias).Where(a => a is "MNC" or "SOT").ToList();
        aliases.Should().BeInAscendingOrder();   // MNC before SOT
    }

    [Fact]
    public void Empty_table_yields_an_empty_snapshot()
    {
        var table = NewTable(out _);
        var snap = table.Snapshot();
        snap.Destinations.Should().BeEmpty();
        snap.Neighbours.Should().BeEmpty();
        snap.DestinationCount.Should().Be(0);
        snap.NeighbourCount.Should().Be(0);
    }
}
