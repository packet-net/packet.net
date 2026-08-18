using AwesomeAssertions;
using Packet.Core;
using Packet.NetRom.Routing;
using Xunit;

namespace Packet.NetRom.Tests.Routing;

/// <summary>
/// The locked INP3 selection truth table (plan risk #4,
/// <c>docs/netrom-inp3-i3-design.md</c> §3) realised as unit + property tests over
/// <see cref="Inp3RouteSelector.SelectActiveRoute"/>. Covers every row -
/// disabled⇒quality; prefer+inp3⇒lowest-time; prefer+no-inp3⇒quality fallback;
/// !prefer⇒quality - plus the three "degenerate to today" invariants (§3.3).
/// </summary>
public class Inp3RouteSelectorTests
{
    private static readonly Callsign NbrA = new("GB7AAA", 0);
    private static readonly Callsign NbrB = new("GB7BBB", 0);
    private static readonly Callsign NbrC = new("GB7CCC", 0);
    // Every route here is on one port; a route's next hop is the (port, callsign) adjacency,
    // so the port travels with it.
    private const string Port = "vhf";

    private static readonly Callsign Dest = new("GB7SOT", 0);

    // A quality-only route (today's vanilla triple; no INP3 metric).
    private static NetRomRoute Q(Callsign nbr, byte quality, string portId = Port)
        => new(nbr, portId, quality, Obsolescence: 6);

    // A route carrying both a quality and an INP3 (target-time) metric.
    private static NetRomRoute T(Callsign nbr, byte quality, int targetTimeMs, byte hopCount, string portId = Port)
        => new(nbr, portId, quality, Obsolescence: 6, new Inp3RouteMetric(targetTimeMs, hopCount));

    // Build a destination from a best-quality-first route list (the ordering the table
    // maintains; Routes[0] is the quality-best route = today's BestRoute).
    private static NetRomDestination DestOf(params NetRomRoute[] routes)
        => new(Dest, "SOT", routes);

    // ---- Row: !prefer (and the disabled-overlay default) -> quality, byte-for-byte ----

    [Fact]
    public void NotPrefer_returns_best_quality_route_ignoring_inp3()
    {
        // NbrA is quality-best (first); NbrB carries a far-better (lower) target time.
        // With prefer off, the INP3 metric is invisible - quality wins.
        var dest = DestOf(
            T(NbrA, quality: 200, targetTimeMs: 9000, hopCount: 3),
            T(NbrB, quality: 100, targetTimeMs: 10, hopCount: 1));

        var chosen = Inp3RouteSelector.SelectActiveRoute(dest, preferInp3Routes: false);

        chosen.Should().BeSameAs(dest.BestRoute);
        chosen!.Neighbour.Should().Be(NbrA);
    }

    [Fact]
    public void NotPrefer_returns_best_quality_for_quality_only_destination()
    {
        var dest = DestOf(Q(NbrA, 200), Q(NbrB, 100));

        var chosen = Inp3RouteSelector.SelectActiveRoute(dest, preferInp3Routes: false);

        chosen.Should().BeSameAs(dest.BestRoute);
        chosen!.Neighbour.Should().Be(NbrA);
    }

    // ---- Row: prefer + an INP3 route exists -> lowest-TargetTimeMs INP3 route ----

    [Fact]
    public void Prefer_with_inp3_routes_selects_lowest_target_time()
    {
        // Quality-best is NbrA; the lowest-target-time INP3 route is NbrC (5 ms).
        var dest = DestOf(
            T(NbrA, quality: 250, targetTimeMs: 8000, hopCount: 2),
            T(NbrB, quality: 120, targetTimeMs: 500, hopCount: 4),
            T(NbrC, quality: 60, targetTimeMs: 5, hopCount: 7));

        var chosen = Inp3RouteSelector.SelectActiveRoute(dest, preferInp3Routes: true);

        chosen!.Neighbour.Should().Be(NbrC);
        chosen.Inp3!.TargetTimeMs.Should().Be(5);
    }

    [Fact]
    public void Prefer_picks_inp3_route_even_when_a_higher_quality_quality_only_route_exists()
    {
        // NbrA is the quality-best route but carries NO INP3 metric; NbrB is INP3.
        // Prefer must pick the INP3 route, not the higher-quality quality-only one.
        var dest = DestOf(
            Q(NbrA, 250),
            T(NbrB, quality: 50, targetTimeMs: 1234, hopCount: 3));

        var chosen = Inp3RouteSelector.SelectActiveRoute(dest, preferInp3Routes: true);

        chosen!.Neighbour.Should().Be(NbrB);
        chosen.Inp3.Should().NotBeNull();
    }

    [Fact]
    public void Prefer_breaks_target_time_ties_by_lowest_hop_count()
    {
        var dest = DestOf(
            T(NbrA, quality: 200, targetTimeMs: 400, hopCount: 5),
            T(NbrB, quality: 200, targetTimeMs: 400, hopCount: 2),   // same time, fewer hops
            T(NbrC, quality: 200, targetTimeMs: 400, hopCount: 9));

        var chosen = Inp3RouteSelector.SelectActiveRoute(dest, preferInp3Routes: true);

        chosen!.Neighbour.Should().Be(NbrB);
    }

    [Fact]
    public void Prefer_breaks_time_and_hop_ties_by_neighbour_callsign_ordinal()
    {
        // All three tie on time AND hop; deterministic winner is the lowest ordinal
        // callsign, regardless of the order they appear in the Routes list.
        var dest = DestOf(
            T(NbrC, quality: 200, targetTimeMs: 300, hopCount: 4),
            T(NbrA, quality: 200, targetTimeMs: 300, hopCount: 4),
            T(NbrB, quality: 200, targetTimeMs: 300, hopCount: 4));

        var chosen = Inp3RouteSelector.SelectActiveRoute(dest, preferInp3Routes: true);

        chosen!.Neighbour.Should().Be(NbrA);   // GB7AAA < GB7BBB < GB7CCC
    }

    // ---- Row: prefer but NO INP3 route -> quality fallback (byte-for-byte today) ----

    [Fact]
    public void Prefer_with_no_inp3_route_falls_back_to_best_quality()
    {
        var dest = DestOf(Q(NbrA, 200), Q(NbrB, 100));

        var chosen = Inp3RouteSelector.SelectActiveRoute(dest, preferInp3Routes: true);

        chosen.Should().BeSameAs(dest.BestRoute);
        chosen!.Neighbour.Should().Be(NbrA);
    }

    // ---- Degeneracy: single route -> same result regardless of mode ----

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Single_quality_route_degenerates_to_that_route_in_any_mode(bool prefer)
    {
        var dest = DestOf(Q(NbrA, 180));

        var chosen = Inp3RouteSelector.SelectActiveRoute(dest, prefer);

        chosen.Should().BeSameAs(dest.BestRoute);
        chosen!.Neighbour.Should().Be(NbrA);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Single_inp3_route_is_selected_in_any_mode(bool prefer)
    {
        // One route that happens to carry an INP3 metric: prefer picks it as the INP3
        // winner; !prefer picks it as the (only) quality route. Same neighbour either
        // way - single-route degeneracy holds across the metric spaces.
        var only = T(NbrA, quality: 140, targetTimeMs: 250, hopCount: 2);
        var dest = DestOf(only);

        var chosen = Inp3RouteSelector.SelectActiveRoute(dest, prefer);

        chosen.Should().BeSameAs(only);
    }

    // ---- Degeneracy: empty destination -> null in any mode ----

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void No_routes_returns_null(bool prefer)
    {
        var dest = DestOf();   // no routes at all

        Inp3RouteSelector.SelectActiveRoute(dest, prefer).Should().BeNull();
    }

    [Fact]
    public void Null_destination_throws()
    {
        var act = () => Inp3RouteSelector.SelectActiveRoute(null!, preferInp3Routes: true);
        act.Should().Throw<ArgumentNullException>();
    }

    // ---- Property: !prefer ALWAYS returns today's BestRoute (full degeneracy) ----

    [Theory]
    [MemberData(nameof(MixedRouteSets))]
    public void NotPrefer_always_equals_today_BestRoute(NetRomRoute[] routes)
    {
        var dest = DestOf(routes);

        var chosen = Inp3RouteSelector.SelectActiveRoute(dest, preferInp3Routes: false);

        chosen.Should().BeSameAs(dest.BestRoute);
    }

    // ---- Property: prefer + quality-only set ALWAYS falls back to today's BestRoute ----

    [Theory]
    [MemberData(nameof(QualityOnlyRouteSets))]
    public void Prefer_over_quality_only_set_equals_today_BestRoute(NetRomRoute[] routes)
    {
        var dest = DestOf(routes);

        var chosen = Inp3RouteSelector.SelectActiveRoute(dest, preferInp3Routes: true);

        chosen.Should().BeSameAs(dest.BestRoute);
    }

    // ---- Property: prefer + ANY inp3 route -> a route with the minimum TargetTimeMs ----

    [Theory]
    [MemberData(nameof(Inp3BearingRouteSets))]
    public void Prefer_selects_a_route_with_the_minimum_target_time(NetRomRoute[] routes)
    {
        var dest = DestOf(routes);

        var chosen = Inp3RouteSelector.SelectActiveRoute(dest, preferInp3Routes: true);

        var minTime = routes.Where(r => r.Inp3 is not null).Min(r => r.Inp3!.TargetTimeMs);
        chosen!.Inp3.Should().NotBeNull("prefer with an INP3 route present must pick an INP3 route");
        chosen.Inp3!.TargetTimeMs.Should().Be(minTime);
    }

    public static IEnumerable<object[]> MixedRouteSets() =>
    [
        [new[] { Q(NbrA, 200), Q(NbrB, 100) }],
        [new[] { T(NbrA, 200, 9000, 3), T(NbrB, 100, 10, 1) }],   // INP3 present but ignored
        [new[] { Q(NbrA, 255), T(NbrB, 50, 5, 1) }],
        [new[] { T(NbrA, 1, 1, 1) }],                              // single INP3-bearing route
    ];

    public static IEnumerable<object[]> QualityOnlyRouteSets() =>
    [
        [new[] { Q(NbrA, 200) }],
        [new[] { Q(NbrA, 200), Q(NbrB, 100) }],
        [new[] { Q(NbrA, 200), Q(NbrB, 199), Q(NbrC, 1) }],
    ];

    public static IEnumerable<object[]> Inp3BearingRouteSets() =>
    [
        [new[] { T(NbrA, 200, 5, 2) }],
        [new[] { T(NbrA, 200, 8000, 2), T(NbrB, 120, 500, 4), T(NbrC, 60, 5, 7) }],
        [new[] { Q(NbrA, 255), T(NbrB, 50, 1234, 3) }],            // quality-best is quality-only
        [new[] { T(NbrA, 200, 400, 5), T(NbrB, 200, 400, 2) }],    // tie on time
    ];
}
