using Packet.Core;

namespace Packet.NetRom.Routing;

/// <summary>
/// The pure INP3 route-<b>selection</b> policy: given a destination's kept routes and
/// the <c>preferInp3Routes</c> knob, decide which single <see cref="NetRomRoute"/> the
/// node treats as <em>active</em> for that destination (the route a <c>connect</c> or a
/// best-route forward resolves to). This is the locked truth table of plan risk #4 -
/// the coexistence of the two metric spaces (NODES quality vs INP3 measured target
/// time) - realised as a side-effect-free static function (see
/// <c>docs/netrom-inp3-i3-design.md</c> §3).
/// </summary>
/// <remarks>
/// <para>
/// <b>The truth table.</b>
/// </para>
/// <list type="bullet">
/// <item><description><c>preferInp3Routes == true</c> <em>and</em> the destination has
/// at least one INP3 route (a route whose <see cref="NetRomRoute.Inp3"/> is non-null):
/// select the <b>lowest-<see cref="Inp3RouteMetric.TargetTimeMs"/></b> INP3 route, ties
/// broken by lowest <see cref="Inp3RouteMetric.HopCount"/> then by neighbour callsign
/// (ordinal) for determinism - the time-space mirror of the quality-space "highest
/// quality, then callsign" ordering.</description></item>
/// <item><description>Otherwise (the knob is off, <em>or</em> no INP3 route exists):
/// fall back to the <b>best-quality</b> route - exactly today's behaviour,
/// <see cref="NetRomDestination.BestRoute"/> (the first of the best-quality-first
/// <see cref="NetRomDestination.Routes"/> list). The <see cref="NetRomRoute.Inp3"/>
/// metric is never read on this path.</description></item>
/// </list>
/// <para>
/// <b>Degenerate-to-today invariant (the acceptance bar, §3.3).</b> Selection collapses
/// to today's quality path - byte-for-byte - in every case where INP3 cannot win:
/// (1) the knob off ⇒ quality; (2) a destination with no INP3 route ⇒ quality fallback;
/// (3) a single-route destination ⇒ that one route regardless of mode. INP3 only ever
/// changes the result for a destination that <em>both</em> opted in via the knob and
/// actually holds a time-route. The <c>enabled</c> overlay switch sits above this
/// function: when the overlay is disabled no INP3 route is ever ingested, so
/// <see cref="NetRomRoute.Inp3"/> is null on every route and the caller passes
/// <c>preferInp3Routes: false</c> (or it is moot) - either way this function returns the
/// quality route unchanged.
/// </para>
/// <para>
/// <b>Purity.</b> No table, engine, options-record, or I/O dependency; no allocation on
/// the hot path (the INP3 winner is found by a single linear scan with a struct
/// running-best, not a LINQ <c>OrderBy</c>). The single <c>bool</c> parameter is the
/// already-resolved <c>preferInp3Routes</c> knob (read by the host from
/// <see cref="Wire.NetRomInp3Options.PreferInp3Routes"/>), so the selector itself stays
/// free of the options type.
/// </para>
/// </remarks>
public static class Inp3RouteSelector
{
    /// <summary>
    /// Select the active route for <paramref name="dest"/> under the INP3 selection
    /// policy. Returns the chosen <see cref="NetRomRoute"/>, or <c>null</c> if the
    /// destination has no routes at all.
    /// </summary>
    /// <param name="dest">The destination and its kept routes (best-quality first, the
    /// <see cref="NetRomDestination.Routes"/> ordering the table maintains).</param>
    /// <param name="preferInp3Routes">The resolved <c>preferInp3Routes</c> knob (BPQ's
    /// <c>PREFERINP3ROUTES</c>; <see cref="Wire.NetRomInp3Options.PreferInp3Routes"/>).
    /// When <c>true</c> an INP3 route, if any, beats quality; when <c>false</c> the
    /// <see cref="NetRomRoute.Inp3"/> metric is ignored entirely and quality wins.</param>
    /// <returns>The lowest-target-time INP3 route when
    /// <paramref name="preferInp3Routes"/> is set and one exists; otherwise the
    /// best-quality route (<see cref="NetRomDestination.BestRoute"/>); or <c>null</c>
    /// for a destination with no routes.</returns>
    public static NetRomRoute? SelectActiveRoute(NetRomDestination dest, bool preferInp3Routes)
    {
        ArgumentNullException.ThrowIfNull(dest);

        // Quality fallback path == today's behaviour, byte-for-byte: the first of the
        // best-quality-first Routes list. Taken whenever the knob is off, or no INP3
        // route exists (handled below), or there are no routes at all.
        if (!preferInp3Routes)
        {
            return dest.BestRoute;
        }

        // preferInp3Routes == true: prefer the best INP3 route if the destination holds
        // any time-route; else fall back to quality. A single linear scan keeps a
        // running best by the time-space key (lowest TargetTimeMs, then lowest HopCount,
        // then neighbour callsign ordinal) - no allocation, no sort.
        NetRomRoute? bestInp3 = null;
        foreach (var route in dest.Routes)
        {
            if (route.Inp3 is null)
            {
                continue;   // a pure quality-route: invisible to the INP3 winner search.
            }
            if (bestInp3 is null || IsBetterInp3(route, bestInp3))
            {
                bestInp3 = route;
            }
        }

        // Any INP3 route ⇒ it wins; otherwise the quality fallback (degenerates to today
        // for a destination known only via NODES).
        return bestInp3 ?? dest.BestRoute;
    }

    /// <summary>
    /// True if INP3 route <paramref name="candidate"/> ranks strictly better than the
    /// current best <paramref name="incumbent"/> in the time metric space: lower
    /// <see cref="Inp3RouteMetric.TargetTimeMs"/> wins; ties broken by lower
    /// <see cref="Inp3RouteMetric.HopCount"/>, then by neighbour callsign (ordinal) for
    /// a stable, deterministic choice. Both routes are assumed INP3-bearing
    /// (<see cref="NetRomRoute.Inp3"/> non-null) - the caller filters quality-only
    /// routes out before comparing.
    /// </summary>
    private static bool IsBetterInp3(NetRomRoute candidate, NetRomRoute incumbent)
    {
        var c = candidate.Inp3!;
        var i = incumbent.Inp3!;

        if (c.TargetTimeMs != i.TargetTimeMs)
        {
            return c.TargetTimeMs < i.TargetTimeMs;     // lowest target time = best.
        }
        if (c.HopCount != i.HopCount)
        {
            return c.HopCount < i.HopCount;             // tie-break: fewest hops.
        }

        // Final tie-break: the adjacency - neighbour callsign ordinal, then port id ordinal -
        // for a deterministic winner across the C#/TS/Rust ports (mirrors the quality-space
        // tie-break). The port is needed because one station audible on two ports now yields two
        // routes that can be identical in the whole time metric.
        int byCall = string.CompareOrdinal(candidate.Neighbour.ToString(), incumbent.Neighbour.ToString());
        return byCall != 0
            ? byCall < 0
            : string.CompareOrdinal(candidate.PortId, incumbent.PortId) < 0;
    }
}
