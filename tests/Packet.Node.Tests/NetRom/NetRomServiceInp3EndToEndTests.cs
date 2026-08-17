using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Packet.Core;
using Packet.NetRom.Routing;
using Packet.NetRom.Wire;
using Packet.Node.Core.Configuration;
using Packet.Node.Core.NetRom;

namespace Packet.Node.Tests.NetRom;

/// <summary>
/// INP3 slice I-5 — the our-fleet <b>end-to-end</b> proof: two (and three) real
/// <see cref="NetRomService"/> instances with the overlay on, cross-wired in-process over a
/// deterministic message "wire" on one <see cref="FakeTimeProvider"/>, exchanging real L3RTT
/// probes / reflections and periodic RIFs. Where <see cref="NetRomServiceInp3Tests"/> drives a
/// single node against a fake peer, this drives the <em>actual</em> two-way loop between real
/// nodes: L3RTT measures the link both ways, SNTT converges, the periodic RIF propagates routes
/// (the own-node source RIP bootstraps a neighbour as a learned time-route), and poison-reverse
/// blocks the would-be loop. The homogeneous pdn fleet is INP3's first-class interop target
/// (BPQ/XRouter are best-effort), so this is the reference convergence test.
/// </summary>
[Trait("Category", "Node")]
public sealed class NetRomServiceInp3EndToEndTests
{
    // The port every node in this fleet exchanges interlink frames on.
    private const string Port = "vhf";

    private static readonly Callsign A = new("GB7AAA", 0);
    private static readonly Callsign B = new("GB7BBB", 0);
    private static readonly Callsign C = new("GB7CCC", 0);
    private static readonly Callsign D = new("GB7DDD", 0);
    private static readonly Callsign E = new("GB7EEE", 0);
    private static readonly DateTimeOffset T0 = new(2026, 6, 8, 12, 0, 0, TimeSpan.Zero);

    // A short periodic RIF (5 s) so a periodic fan-out fires well inside the 180 s reflection-reset
    // window we keep alive by re-probing — the same FastRif trick as NetRomServiceInp3Tests.
    private static NetRomConfig Config() => new()
    {
        Enabled = true,
        Connect = true,
        Inp3 = new NetRomInp3Options
        {
            Enabled = true,
            RifInterval = TimeSpan.FromSeconds(5),
            PositiveDebounce = TimeSpan.FromSeconds(1),
        },
    };

    /// <summary>An in-process INP3 fleet on one fake clock: each node's outbound interlink frames
    /// land on a shared wire; <see cref="DeliverRound"/> hands each queued frame to its target as if
    /// it arrived from the sender, one hop per call (newly-emitted frames wait for the next round, so
    /// a clock advance between rounds yields a controllable RTT).</summary>
    private sealed class Fleet : IDisposable
    {
        public FakeTimeProvider Clock { get; } = new(T0);
        private readonly Dictionary<string, NetRomService> nodes = new();
        private readonly List<(Callsign To, Callsign From, byte[] Bytes)> wire = new();

        public NetRomService Add(Callsign call)
        {
            var svc = new NetRomService(Config(), Clock, NullLogger<NetRomService>.Instance);
            svc.interlinkSendSinkForTest = (to, bytes) =>
            {
                wire.Add((to.Callsign, call, (byte[])bytes.Clone()));
                return true;
            };
            svc.SetInp3LocalNodeForTest(call);
            nodes[call.ToString()] = svc;
            return svc;
        }

        public NetRomService Node(Callsign call) => nodes[call.ToString()];

        /// <summary>Deliver the frames currently on the wire (one hop). Frames emitted as a side
        /// effect stay queued for the next round.</summary>
        public int DeliverRound()
        {
            var batch = wire.ToList();
            wire.Clear();
            foreach (var (to, from, bytes) in batch)
            {
                if (nodes.TryGetValue(to.ToString(), out var svc))
                {
                    svc.IngestInterlinkForTest(Port, from, bytes);
                }
            }
            return batch.Count;
        }

        /// <summary>Deliver rounds until the wire is quiet (bounded), for steps where exact RTT
        /// timing doesn't matter (RIF propagation).</summary>
        public void Quiesce()
        {
            int guard = 0;
            while (DeliverRound() > 0 && guard++ < 200)
            {
            }
        }

        public void TickAll()
        {
            foreach (var svc in nodes.Values)
            {
                svc.Inp3TickForTest();
            }
        }

        public void Advance(TimeSpan d) => Clock.Advance(d);

        /// <summary>The frames currently queued (for asserting what a node put on the wire).</summary>
        public IReadOnlyList<(Callsign To, Callsign From, byte[] Bytes)> Wire => wire;

        public void ClearWire() => wire.Clear();

        /// <summary>Bootstrap mutual awareness: each peer hears the other's first probe (the interlink
        /// coming up), so the next tick probes it. Faithful to the real path — a node only probes a
        /// neighbour it has observed 0xCF from. Reflections land on the wire; cleared by the caller.</summary>
        public void Observe(Callsign x, Callsign y)
        {
            Node(x).IngestInterlinkForTest(Port, y, Inp3L3RttFrame.Build(y).ToBytes());
            Node(y).IngestInterlinkForTest(Port, x, Inp3L3RttFrame.Build(x).ToBytes());
        }

        public void Dispose()
        {
            foreach (var svc in nodes.Values)
            {
                svc.Dispose();
            }
        }
    }

    // Run one probe/reflect exchange between every observed pair to a known RTT: tick (probes out at
    // t), deliver (peers reflect, still at t), advance rtt, deliver (originators measure → SNTT rtt/2).
    private static void MeasureRound(Fleet f, int rttMs)
    {
        f.TickAll();          // each node probes its observed neighbours
        f.DeliverRound();     // probes arrive; peers reflect (reflections queued at the same instant)
        f.Advance(TimeSpan.FromMilliseconds(rttMs));
        f.DeliverRound();     // reflections arrive → RTT measured, SNTT folded
        f.ClearWire();        // drop anything left (e.g. a coincident periodic RIF) — measured state is set
    }

    [Fact]
    public void Two_nodes_mutually_measure_sntt_and_become_inp3_capable()
    {
        using var f = new Fleet();
        f.Add(A);
        f.Add(B);
        f.Observe(A, B);
        f.ClearWire();

        MeasureRound(f, rttMs: 100);

        f.Node(A).Inp3EngineForTest!.SnttMs(B).Should().Be(50u, "RTT 100 ms ÷ 2, first sample seeds directly");
        f.Node(B).Inp3EngineForTest!.SnttMs(A).Should().Be(50u, "the link is measured both ways");
        f.Node(A).Inp3EngineForTest!.Neighbours.Should().Contain(n => n.Neighbour == B && n.Inp3Capable);
        f.Node(B).Inp3EngineForTest!.Neighbours.Should().Contain(n => n.Neighbour == A && n.Inp3Capable);
    }

    [Fact]
    public void Two_nodes_learn_each_other_as_inp3_time_routes_via_the_periodic_rif()
    {
        using var f = new Fleet();
        f.Add(A);
        f.Add(B);
        f.Observe(A, B);
        f.ClearWire();
        MeasureRound(f, rttMs: 100);   // SNTT(A↔B) = 50 each way

        // Advance past the 5 s periodic RIF interval (well inside the 180 s reset), tick → each node
        // fans out a periodic RIF whose own-node source RIP (0/0) the peer learns as a time-route to
        // the sender. Quiesce delivers the RIFs (+ any reflections) to convergence.
        f.Advance(TimeSpan.FromSeconds(6));
        f.TickAll();
        f.Quiesce();

        var aToB = f.Node(A).Snapshot().Destinations
            .SingleOrDefault(d => d.Destination == B);
        aToB.Should().NotBeNull("A learns B as an INP3 destination from B's periodic RIF source RIP");
        var route = aToB!.Routes.Single(r => r.Neighbour == B);
        route.Inp3.Should().NotBeNull("the learned route carries a measured target time");
        route.Inp3!.TargetTimeMs.Should().Be(60, "B's source RIP target 0 + SNTT 50 + 10 per-hop");
        route.Inp3.HopCount.Should().Be(1, "B's source hop 0 + 1 through the link");

        // Symmetric: B learns A too.
        f.Node(B).Snapshot().Destinations.Should().Contain(d => d.Destination == A);
    }

    [Fact]
    public void A_node_poison_reverses_the_neighbour_it_learned_a_route_via()
    {
        using var f = new Fleet();
        f.Add(A);
        f.Add(B);
        f.Observe(A, B);
        f.ClearWire();
        MeasureRound(f, rttMs: 100);

        // First periodic round: A learns B (via B). Clear the wire, then drive a SECOND periodic round
        // and capture A's RIF toward B — B is reachable only via B, so it must be poison-reversed.
        f.Advance(TimeSpan.FromSeconds(6));
        f.TickAll();
        f.Quiesce();
        f.Node(A).Snapshot().Destinations.Should().Contain(d => d.Destination == B, "A holds B as a time-route");

        f.ClearWire();
        f.Advance(TimeSpan.FromSeconds(6));
        f.Node(A).Inp3TickForTest();   // just A fans out

        var rifToB = f.Wire.FirstOrDefault(m => m.To == B && m.Bytes.Length >= 1 && m.Bytes[0] == Inp3Rif.Signature);
        rifToB.Bytes.Should().NotBeNull("A fans a periodic RIF toward B");
        Inp3Rif.TryParse(rifToB.Bytes, out var rif).Should().BeTrue();
        var bRip = rif!.Rips.SingleOrDefault(r => r.Destination == B);
        bRip.Should().NotBeNull("A advertises B back toward B");
        bRip!.IsHorizon.Should().BeTrue("B is reached only via B → poison-reverse it at the horizon (loop-safety)");
        rif.Rips[0].Destination.Should().Be(A, "own-node source RIP first, at 0/0");
        rif.Rips[0].IsHorizon.Should().BeFalse();
    }

    [Fact]
    public void A_three_node_line_propagates_a_time_route_two_hops_with_accumulated_target_time()
    {
        // A — B — C (a line; A has no direct link to C). After convergence + two periodic rounds,
        // A learns C via B with the target time accumulated across both hops — the real multi-hop
        // INP3 propagation (the RIP for C rides B's RIF to A one periodic round after B learns C).
        using var f = new Fleet();
        f.Add(A);
        f.Add(B);
        f.Add(C);
        f.Observe(A, B);
        f.Observe(B, C);
        f.ClearWire();

        MeasureRound(f, rttMs: 100);   // SNTT = 50 on both links (A↔B, B↔C)

        // Two periodic rounds: round 1 — B learns C (via C's source RIP); round 2 — B's RIF to A now
        // carries C, so A learns C via B.
        for (int round = 0; round < 2; round++)
        {
            f.Advance(TimeSpan.FromSeconds(6));
            f.TickAll();
            f.Quiesce();
        }

        var aToC = f.Node(A).Snapshot().Destinations.SingleOrDefault(d => d.Destination == C);
        aToC.Should().NotBeNull("A learns C two hops away, via B, from B's propagated RIF");
        var route = aToC!.Routes.Single(r => r.Neighbour == B);
        route.Inp3.Should().NotBeNull();
        // B's local target to C = 0 + 50 (SNTT B→C) + 10 = 60; A ingests = 60 + 50 (SNTT A→B) + 10 = 120.
        route.Inp3!.TargetTimeMs.Should().Be(120, "accumulated Σ-SNTT + per-hop across both links");
        route.Inp3.HopCount.Should().Be(2, "two hops A→B→C");

        // A reaches C only through B (no direct link) — its only route's next hop is B.
        aToC.Routes.Should().OnlyContain(r => r.Neighbour == B);
    }

    [Fact]
    public void A_holds_two_time_routes_to_C_and_failover_by_time_selects_the_faster_path()
    {
        // The end-to-end failover-by-time proof. A reaches C two ways, of UNEQUAL hop length, so the
        // accumulated INP3 target time differs even though every link measures the same single RTT:
        //
        //   short:  A — B — C        (2 hops)   →  via B, target 120 ms
        //   long:   A — D — E — C    (3 hops)   →  via D, target 180 ms
        //
        // After convergence A holds C with BOTH next hops, each carrying its measured target time, and
        // the Inp3RouteSelector — given the preferInp3Routes knob — picks the via-B (lower-time) route:
        // having learned two real multi-hop time-routes, the node selects the faster. With the knob off
        // it falls back to best quality (today's behaviour).
        using var f = new Fleet();
        f.Add(A);
        f.Add(B);
        f.Add(C);
        f.Add(D);
        f.Add(E);

        // The 5-node graph: the short path A→B→C and the long path A→D→E→C. Observe every adjacent pair
        // so each link is probed and SNTT-measured both ways.
        f.Observe(A, B);
        f.Observe(B, C);
        f.Observe(A, D);
        f.Observe(D, E);
        f.Observe(E, C);
        f.ClearWire();

        // One RTT for every link → SNTT 50 ms on all five (A↔B, B↔C, A↔D, D↔E, E↔C).
        MeasureRound(f, rttMs: 100);

        // The long path needs an extra periodic round to propagate C all the way (C→E, E→D, D→A); three
        // rounds cover both arms (the short path converges in two). Each round is a full RIF fan-out at a
        // fresh 6 s tick (well inside the 180 s reflection-reset window — at ~18 s total nothing resets).
        for (int round = 0; round < 3; round++)
        {
            f.Advance(TimeSpan.FromSeconds(6));
            f.TickAll();
            f.Quiesce();
        }

        var aToC = f.Node(A).Snapshot().Destinations.SingleOrDefault(d => d.Destination == C);
        aToC.Should().NotBeNull("A learns C from both arms of the graph");

        // Both next hops are present, each an INP3 time-route (within the per-dest route cap of 3).
        var viaB = aToC!.Routes.SingleOrDefault(r => r.Neighbour == B);
        var viaD = aToC.Routes.SingleOrDefault(r => r.Neighbour == D);
        viaB.Should().NotBeNull("A reaches C via B on the 2-hop short path");
        viaD.Should().NotBeNull("A reaches C via D on the 3-hop long path");
        viaB!.Inp3.Should().NotBeNull("the via-B route carries a measured target time");
        viaD!.Inp3.Should().NotBeNull("the via-D route carries a measured target time");

        // The accumulated target-time arithmetic (Σ-SNTT + 10 ms per hop), mirroring the line test:
        //   via B:  C source 0 → B ingests 0 +50 +10 = 60 → A ingests 60 +50 +10 = 120  (hop 2)
        //   via D:  C source 0 → E ingests 0 +50 +10 = 60 → D ingests 60 +50 +10 = 120 → A ingests 120 +50 +10 = 180  (hop 3)
        // Relative ordering is the load-bearing invariant; pin the exact values too since they hold here.
        viaB.Inp3!.TargetTimeMs.Should().BeLessThan(viaD.Inp3!.TargetTimeMs,
            "the 2-hop short path has a lower accumulated target time than the 3-hop long path");
        viaB.Inp3.TargetTimeMs.Should().Be(120, "Σ-SNTT + per-hop across A→B→C (2 hops)");
        viaD.Inp3.TargetTimeMs.Should().Be(180, "Σ-SNTT + per-hop across A→D→E→C (3 hops)");
        viaB.Inp3.HopCount.Should().Be(2, "A→B→C is two hops");
        viaD.Inp3.HopCount.Should().Be(3, "A→D→E→C is three hops");

        // Failover-by-time: with the INP3 knob on, the selector returns the via-B (lower-target-time)
        // route — proving end-to-end that, holding two real multi-hop time-routes, the node selects the
        // faster path.
        var active = Inp3RouteSelector.SelectActiveRoute(aToC, preferInp3Routes: true);
        active.Should().NotBeNull();
        active!.Neighbour.Should().Be(B, "lowest accumulated target time wins under preferInp3Routes");
        active.Inp3!.TargetTimeMs.Should().Be(120);

        // Contrast (today's behaviour): with the knob off the selector ignores the time metric entirely
        // and returns the best-QUALITY route (Routes[0], the quality-first head) — the dual-metric
        // coexistence, with INP3 changing the result only when explicitly preferred.
        var qualityActive = Inp3RouteSelector.SelectActiveRoute(aToC, preferInp3Routes: false);
        qualityActive.Should().Be(aToC.BestRoute, "knob off ⇒ quality fallback, byte-for-byte today's path");
    }
}
