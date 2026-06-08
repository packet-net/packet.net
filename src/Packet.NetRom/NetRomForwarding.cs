using System.Linq;
using Packet.Core;
using Packet.NetRom.Routing;
using Packet.NetRom.Wire;

namespace Packet.NetRom;

/// <summary>
/// The route-selection policy a forwarding node uses when a destination has more than
/// one kept route.
/// </summary>
public enum NetRomForwardMode
{
    /// <summary>Always the single best route (bounce-back excluded). Deterministic —
    /// every transit datagram for a destination takes the same path.</summary>
    BestRoute,

    /// <summary>Per-flow quality-weighted spread: every datagram of one L4 circuit
    /// hashes to the same route (so the circuit's ordering is preserved), while
    /// distinct circuits are distributed across the kept routes in proportion to
    /// quality. Stateless. The default — a transit node load-balances.</summary>
    PerFlow,
}

/// <summary>
/// The NET/ROM L3 <b>forwarding decision</b> — what a transit node does with a
/// datagram whose destination is <em>not</em> itself: drop it, or forward it (with a
/// decremented, capped TTL) to a next-hop neighbour. Pure (no I/O): the node host
/// feeds it the datagram, the neighbour it arrived from, this node's callsign, the
/// routing view, and the TTL cap; the host then performs the interlink send for a
/// <see cref="ForwardOutcome.ForwardTo"/> outcome.
/// </summary>
/// <remarks>
/// Mirrors the forward routine in the de-facto reference (LinBPQ <c>L4Code.c</c>):
/// decrement the hop limit and discard at zero; cap the TTL on everything sent;
/// drop a datagram that has looped back to its own origin; resolve the destination's
/// best route whose neighbour is not the one it just arrived from (so it is never
/// bounced straight back the way it came); otherwise forward. The caller has already
/// established the datagram is not addressed to this node (the "for us" check
/// terminates locally before forwarding is considered).
/// </remarks>
public static class NetRomForwarding
{
    /// <summary>What <see cref="Decide"/> determined should happen to a datagram.</summary>
    public enum ForwardOutcome
    {
        /// <summary>Forward it (with the rewritten header) to <see cref="ForwardDecision.NextHop"/>.</summary>
        ForwardTo,

        /// <summary>Drop: the hop limit reached zero.</summary>
        DropTtlExpired,

        /// <summary>Drop: the datagram's origin is this node — it has looped back.</summary>
        DropLooped,

        /// <summary>Drop: no onward route to the destination (excluding the way it came).</summary>
        DropNoRoute,
    }

    /// <summary>The outcome of a forwarding decision. When
    /// <see cref="ForwardOutcome.ForwardTo"/>, <see cref="Packet"/> carries the
    /// rewritten (TTL-decremented) datagram to send to <see cref="NextHop"/>.</summary>
    public readonly record struct ForwardDecision(ForwardOutcome Outcome, NetRomPacket Packet, Callsign NextHop)
    {
        /// <summary>True if the datagram should be forwarded.</summary>
        public bool ShouldForward => Outcome == ForwardOutcome.ForwardTo;
    }

    /// <summary>
    /// Decide what to do with a transit datagram. The caller has already confirmed
    /// <paramref name="packet"/>'s destination is not <paramref name="nodeCall"/>.
    /// </summary>
    /// <param name="packet">The received datagram.</param>
    /// <param name="receivedFrom">The neighbour the datagram arrived from (so it is
    /// not bounced straight back to it).</param>
    /// <param name="nodeCall">This node's callsign (for the loop guard).</param>
    /// <param name="routing">The current routing view.</param>
    /// <param name="maxTimeToLive">The TTL cap applied to everything forwarded (the
    /// node's configured initial TTL — BPQ's <c>L3LIVES</c>).</param>
    /// <param name="preferInp3Routes">The resolved INP3 forwarding preference (BPQ's
    /// <c>PREFERINP3ROUTES</c>; <see cref="Wire.NetRomInp3Options.PreferInp3Routes"/>).
    /// When <c>true</c> and the destination holds at least one INP3 time-route, the
    /// datagram is forwarded over the <b>lowest-target-time</b> INP3 route (the way it
    /// came excluded), falling back to the quality next-hop only when no INP3 route is
    /// usable. When <c>false</c> (the default) the INP3 metric is ignored entirely and
    /// selection is byte-for-byte today's quality path.</param>
    public static ForwardDecision Decide(
        NetRomPacket packet,
        Callsign receivedFrom,
        Callsign nodeCall,
        NetRomRoutingSnapshot routing,
        byte maxTimeToLive,
        NetRomForwardMode mode = NetRomForwardMode.PerFlow,
        bool preferInp3Routes = false)
    {
        ArgumentNullException.ThrowIfNull(packet);
        ArgumentNullException.ThrowIfNull(routing);

        // 1. Decrement the hop limit; a datagram that arrives at TTL 1 (or 0) is at
        //    the end of its life and must not be forwarded.
        var network = packet.Network.Decremented();
        if (network.TimeToLive == 0)
        {
            return new ForwardDecision(ForwardOutcome.DropTtlExpired, packet, default);
        }

        // 2. Cap the TTL on everything sent, so a buggy/hostile peer can't make a
        //    frame circulate longer than this node's own initial TTL.
        if (network.TimeToLive > maxTimeToLive)
        {
            network = network with { TimeToLive = maxTimeToLive };
        }

        // 3. Loop guard: a datagram whose origin is this node has come back to its
        //    start — forwarding it again just loops.
        if (network.Origin.Equals(nodeCall))
        {
            return new ForwardDecision(ForwardOutcome.DropLooped, packet, default);
        }

        // 4. Next hop: the destination's best route (Routes is best-first) whose
        //    neighbour is not the one it arrived from. When INP3 is preferred and the
        //    destination holds a time-route, the lowest-target-time INP3 route wins;
        //    otherwise (knob off, or no usable INP3 route) the quality next-hop, exactly
        //    as today.
        var resolved = routing.Destinations.FirstOrDefault(d => d.Destination.Equals(network.Destination));
        Callsign? nextHop = null;
        if (resolved is not null)
        {
            if (preferInp3Routes)
            {
                nextHop = SelectInp3NextHop(resolved.Routes, receivedFrom);
            }
            nextHop ??= SelectNextHop(resolved.Routes, receivedFrom, mode, packet);
        }

        if (nextHop is null)
        {
            return new ForwardDecision(ForwardOutcome.DropNoRoute, packet, default);
        }

        return new ForwardDecision(ForwardOutcome.ForwardTo, packet with { Network = network }, nextHop.Value);
    }

    // The next-hop neighbour for a destination under the active mode, excluding the
    // neighbour the datagram arrived from. The routes list is best-first.
    private static Callsign? SelectNextHop(
        IReadOnlyList<NetRomRoute> routes, Callsign receivedFrom, NetRomForwardMode mode, NetRomPacket packet)
        => mode == NetRomForwardMode.PerFlow
            ? SelectWeighted(routes, receivedFrom, FlowHash(packet))
            : SelectBest(routes, receivedFrom);

    // The single lowest-target-time INP3 route whose neighbour isn't the way the datagram
    // came — the time-space mirror of SelectBest, and identical to the connect path's
    // Inp3RouteSelector.SelectActiveRoute pick (so forward + connect agree on the active
    // INP3 next hop). Per-flow weighting is a quality-space concept: in the measured
    // time-space we always forward the fastest path (spreading flows across slower
    // time-routes would defeat the measurement), so PerFlow/BestRoute is moot here. Returns
    // null when the destination holds no usable INP3 route (every time-route is the way it
    // came, or there are none), at which point Decide falls back to the quality next-hop.
    private static Callsign? SelectInp3NextHop(IReadOnlyList<NetRomRoute> routes, Callsign receivedFrom)
    {
        NetRomRoute? best = null;
        foreach (var route in routes)
        {
            if (route.Inp3 is not { } m || route.Neighbour.Equals(receivedFrom))
            {
                continue;   // a pure quality-route, or the way it came — not an eligible INP3 next hop.
            }
            var b = best?.Inp3;
            bool better = b is null
                || m.TargetTimeMs < b.TargetTimeMs
                || (m.TargetTimeMs == b.TargetTimeMs && m.HopCount < b.HopCount)
                || (m.TargetTimeMs == b.TargetTimeMs && m.HopCount == b.HopCount
                    && string.CompareOrdinal(route.Neighbour.ToString(), best!.Neighbour.ToString()) < 0);
            if (better)
            {
                best = route;
            }
        }
        return best?.Neighbour;
    }

    // The single best usable route — the first in the best-first list that isn't the
    // way the datagram came.
    private static Callsign? SelectBest(IReadOnlyList<NetRomRoute> routes, Callsign receivedFrom)
    {
        foreach (var route in routes)
        {
            if (!route.Neighbour.Equals(receivedFrom))
            {
                return route.Neighbour;
            }
        }
        return null;
    }

    // A per-flow, quality-weighted pick among the eligible routes (not the way it
    // came, quality &gt; 0): all datagrams of one circuit hash to the same route (so
    // L4 ordering is preserved end-to-end), while distinct circuits spread across the
    // kept routes in proportion to quality. Stateless — no per-flow table.
    private static Callsign? SelectWeighted(IReadOnlyList<NetRomRoute> routes, Callsign receivedFrom, uint flowHash)
    {
        uint total = 0;
        foreach (var route in routes)
        {
            if (!route.Neighbour.Equals(receivedFrom) && route.Quality > 0)
            {
                total += route.Quality;
            }
        }
        if (total == 0)
        {
            return null;
        }

        var target = flowHash % total;
        foreach (var route in routes)
        {
            if (route.Neighbour.Equals(receivedFrom) || route.Quality == 0)
            {
                continue;
            }
            if (target < route.Quality)
            {
                return route.Neighbour;
            }
            target -= route.Quality;
        }
        return null;   // unreachable: total > 0 guarantees a pick above
    }

    // FNV-1a (32-bit) over the flow key — the L3 origin (AX.25-shifted, 7 octets) plus
    // the L4 circuit index + id — so every datagram of a circuit hashes identically
    // across its lifetime. Defined byte-for-byte so the C#/TS/Rust ports agree.
    private static uint FlowHash(NetRomPacket packet)
    {
        Span<byte> key = stackalloc byte[NetRomCallsign.ShiftedLength + 2];
        NetRomCallsign.WriteShifted(packet.Network.Origin, key);
        key[NetRomCallsign.ShiftedLength] = packet.Transport.CircuitIndex;
        key[NetRomCallsign.ShiftedLength + 1] = packet.Transport.CircuitId;

        uint hash = 2166136261u;        // FNV-1a offset basis
        foreach (var b in key)
        {
            hash ^= b;
            hash *= 16777619u;          // FNV-1a prime
        }
        return hash;
    }
}
