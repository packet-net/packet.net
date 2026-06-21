using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using AwesomeAssertions;
using Packet.Ax25.Session;
using Packet.Core;
using Packet.Interop.Tests.Netsim;
using Packet.NetRom.Routing;
using Packet.NetRom.Wire;
using Packet.Node.Core.Configuration;
using Packet.Node.Core.NetRom;
using Packet.Node.Core.Transports;
using Xunit;
using Xunit.Abstractions;

namespace Packet.Interop.Tests.Linbpq;

/// <summary>
/// <b>Best-effort INP3 interop vs real LinBPQ over AXUDP — DRAFT SKELETON, double-gated OFF.</b>
/// Proves pdn's INP3 overlay (L3RTT / RIF) interoperates with a real LinBPQ across the same
/// AXUDP interlink seam <see cref="NetRomL4CircuitViaAxudp"/> already establishes via
/// <see cref="NetRomService.ConnectCircuitAsync"/>. The full rationale, the feasibility analysis,
/// and the exact BPQ-fixture config delta this skeleton waits on live in
/// <c>docs/netrom-inp3-interop.md</c> — read it before un-gating.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why a skeleton, not a live test (docs/netrom-inp3-interop.md §2–§4).</b> INP3's observable
/// behaviour (L3RTT probe/reflect; RIF time-routes) rides a <em>connected-mode</em> PID-0xCF
/// interlink — not the connectionless NODES broadcast the netsim/AXUDP NET/ROM ingest tests watch.
/// Today <em>(a)</em> neither <c>docker/linbpq/bpq32.cfg</c> nor <c>docker/xrouter/XROUTER.CFG</c>
/// enables INP3, and <em>(b)</em> only <see cref="NetRomL4CircuitViaAxudp"/> stands up the
/// interlink INP3 needs. So this test is <b>double-gated</b> and stays a green skip until both
/// the stack is up AND the BPQ INP3 fixture delta has landed and flips
/// <c>PDN_INTEROP_BPQ_INP3=1</c> (the §4 marker the future config-delta PR sets in
/// <c>interop.yml</c>). It is NOT wired into a live lane by I-5.
/// </para>
/// <para>
/// <b>The observable (public surface).</b> pdn's <see cref="NetRomService.Snapshot"/> exposes
/// <see cref="NetRomRoute.Inp3"/> — a non-null <see cref="Inp3RouteMetric"/> (target time + hop
/// count) for a destination ONLY once pdn has ingested a real <see cref="Inp3Rif"/> from a
/// neighbour. So "pdn learned a time-route from BPQ" is assertable WITHOUT internals access (the
/// <c>Inp3EngineForTest</c>/SNTT seam is <c>InternalsVisibleTo Packet.Node.Tests</c> only, not this
/// project). Asserting a non-null <c>Inp3</c> route metric is therefore the primary, public,
/// deterministic check that BPQ originates INP3 RIF and pdn parses it into the second metric space.
/// </para>
/// <para>
/// <b>What it does NOT assert (deferred — docs/netrom-inp3-interop.md §3.1).</b> The L3RTT SNTT
/// value (needs the internals seam → a sibling <c>Packet.Node.Tests</c> test, or a new public
/// snapshot field) and BPQ RIF cadence/alias-TLV edge cases (I-4 AMBIGUITY-I4-1 locked off). A red
/// here is "characterise + add a <c>NetRomInp3Options</c> flag + a strict-vs-pragmatic-audit row"
/// (plan §2), never "we're wrong".
/// </para>
/// <para>
/// Tagged <c>Group=NetRom</c> so it joins the clean-stack-fenced NET/ROM phase of
/// <c>interop.yml</c> (fresh BPQ, no learned-state carryover), isolated from the timing-sensitive
/// AX.25 tests. Serialised into <see cref="NetsimCollection"/> (shared BPQ daemon + fixed UDP port).
/// Bring the stack up with <c>docker compose -f docker/compose.interop.yml up -d --wait</c>.
/// </para>
/// </remarks>
[Trait("Category", "Interop")]
[Trait("Group", "NetRom")]
[Collection(NetsimCollection.Name)]
public class NetRomInp3L3RttViaAxudp
{
    private const string Host = "127.0.0.1";
    private const int BpqAxudpPort = 8093;   // BPQAXIP/UDP listener (published)
    private const int BpqHttpPort = 8008;    // liveness probe
    private const int BpqTelnetPort = 8010;  // node prompt (PASSWORD->SENDNODES, like NetRomL4CircuitViaAxudp)

    // The env-var marker the future BPQ-INP3 config-delta PR sets in interop.yml once
    // docker/linbpq/bpq32.cfg enables INP3 (PREFERINP3ROUTES=1 + interval pins —
    // docs/netrom-inp3-interop.md §3.2). Until then the test green-skips.
    private const string BpqInp3MarkerEnv = "PDN_INTEROP_BPQ_INP3";

    // A DISTINCT callsign + fixed local UDP port from every other *ViaAxudp test (so the
    // shared BPQ daemon can't confuse this interlink/route with another in the serialised
    // NET/ROM group). Pick an unused port + callsign when un-gating; the BPQ fixture will
    // need a matching `MAP <call> 172.30.0.1 UDP <port> B` entry (mirror the existing rows).
    private const int PdnLocalPort = 8197;
    private static readonly Callsign OurCall = new("PNI3BX", 1);
    private const string OurAlias = "PNI3B";
    private static readonly Callsign BpqCall = new("PN0TST", 0);

    private const string BpqPasswordText = "WONTLISTEN";   // docker/linbpq/bpq32.cfg PASSWORD=

    // pdn originates NODES so BPQ keeps a fresh route/AUTOADD entry (same as the L4-circuit test).
    private static readonly TimeSpan BroadcastEvery = TimeSpan.FromSeconds(2);

    // Bounded budget for pdn to ingest a BPQ-originated RIF over the lossless AXUDP tunnel,
    // once INP3 is on at both ends. Generous-but-bounded: a genuinely-deaf node fails rather
    // than hangs. Calibrate against the actual BPQ RIF cadence when un-gating.
    private static readonly TimeSpan LearnTimeRouteBudget = TimeSpan.FromSeconds(120);

    private readonly ITestOutputHelper output;

    public NetRomInp3L3RttViaAxudp(ITestOutputHelper output) => this.output = output;

    [SkippableFact]
    public async Task Pdn_learns_an_INP3_time_route_from_real_linbpq_over_axudp()
    {
        // Gate 1: the docker stack must be up (the standard interop precondition).
        Skip.IfNot(await IsTcpPortReachable(Host, BpqHttpPort),
            $"LinBPQ not reachable (HTTP {Host}:{BpqHttpPort}). Bring up the interop stack: 'docker compose -f docker/compose.interop.yml up -d --wait'.");

        // Gate 2: INP3 must be ENABLED in the BPQ fixture. Until the docs §3.2 config delta lands
        // (and flips this marker in interop.yml), BPQ originates no L3RTT/RIF, so there is nothing
        // to interop with — green-skip rather than red-fail a vanilla stack.
        Skip.If(Environment.GetEnvironmentVariable(BpqInp3MarkerEnv) != "1",
            $"BPQ INP3 not enabled in the fixture ({BpqInp3MarkerEnv}!=1). Land the docker/linbpq/bpq32.cfg INP3 delta (docs/netrom-inp3-interop.md §3.2) and set {BpqInp3MarkerEnv}=1 in interop.yml to activate this lane.");

        using var cts = new CancellationTokenSource(LearnTimeRouteBudget + TimeSpan.FromSeconds(90));

        await using var modem = new AxudpFrameTransport(new IPEndPoint(IPAddress.Loopback, BpqAxudpPort), PdnLocalPort);
        await using var listener = new Ax25Listener(modem, new Ax25ListenerOptions { MyCall = OurCall });

        // pdn with INP3 ON. Connect=true is required for the Inp3Host to be constructed (it rides
        // the connected-mode interlink machinery — NetRomService.cs). L3RttInterval is shortened so
        // pdn probes BPQ promptly; the rest of NetRomInp3Options defaults interoperate.
        await using var netRom = new NetRomService(new NetRomConfig
        {
            Enabled = true,
            Broadcast = true,
            Connect = true,
            TransportTimeoutSeconds = 4,
            TransportRetries = 5,
            Inp3 = new NetRomInp3Options
            {
                Enabled = true,
                L3RttInterval = TimeSpan.FromSeconds(5),   // probe BPQ promptly for CI observability
            },
        }, nodeAlias: OurAlias);

        netRom.AttachPort("axudp", OurCall, listener);
        await listener.StartAsync(cts.Token);

        // Keep originating pdn's NODES so BPQ holds a route to us (its obsolescence/AUTOADD would
        // otherwise decay) — the same pattern NetRomL4CircuitViaAxudp uses.
        using var broadcastCts = CancellationTokenSource.CreateLinkedTokenSource(cts.Token);
        var broadcaster = RunBroadcastLoop(netRom, broadcastCts.Token);

        try
        {
            // Hear BPQ's NODES (provoked SENDNODES, bounded retry) so pdn has a routed neighbour and
            // opens/keeps the interlink that L3RTT + RIF ride. (When un-gating, confirm on the wire
            // that BPQ then exchanges L3RTT on this interlink; see docs §3.2.)
            var heardBpq = await ProvokeAndHearBpqAsync(netRom, cts.Token);
            heardBpq.Should().BeTrue(
                "pdn must hear LinBPQ's NODES (PN0TST) over AXUDP so it has a NET/ROM neighbour the INP3 overlay can probe + ingest RIF from");
            DumpSnapshot(netRom.Snapshot(), "after hearing BPQ");

            // THE INP3 ASSERTION (public surface): pdn ingests a BPQ-originated RIF and builds a
            // time-route — a destination whose best route carries a non-null Inp3RouteMetric. This
            // is genuine cross-implementation evidence that pdn parses a real LinBPQ INP3 RIF into
            // its second (target-time) metric space.
            var learned = await WaitForInp3TimeRouteAsync(netRom, cts.Token);
            DumpSnapshot(netRom.Snapshot(), "after INP3 window");

            learned.Should().BeTrue(
                "pdn must ingest a BPQ-originated INP3 RIF over the interlink and surface a non-null Inp3 route metric (target time + hop count) in its routing snapshot");

            // ── Tier B/C upgrades (un-comment when the SNTT + RIF-edge cases are wired) ──
            // SNTT (L3RTT reflection) needs the internals seam → assert it from a sibling
            // Packet.Node.Tests deterministic test, or add a public snapshot field, rather than
            // here. RIF alias-TLV / cadence edge cases: characterise once Tier-A is green
            // (docs/netrom-inp3-interop.md §3.1). Any divergence → a NetRomInp3Options flag + an
            // audit row, not a spec bend.
        }
        finally
        {
            await broadcastCts.CancelAsync();
            try { await broadcaster; } catch (OperationCanceledException) { /* expected */ }
        }
    }

    // True once any destination in the snapshot has a best route carrying an INP3 metric — i.e. pdn
    // ingested a RIF. Polls within a bounded budget so a single dropped/late RIF doesn't fail outright.
    private static async Task<bool> WaitForInp3TimeRouteAsync(NetRomService netRom, CancellationToken outer)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(outer);
        cts.CancelAfter(LearnTimeRouteBudget);
        while (!cts.IsCancellationRequested)
        {
            if (netRom.Snapshot().Destinations.Any(d => d.Routes.Any(r => r.Inp3 is not null)))
            {
                return true;
            }
            try { await Task.Delay(500, cts.Token); } catch (OperationCanceledException) { break; }
        }
        return netRom.Snapshot().Destinations.Any(d => d.Routes.Any(r => r.Inp3 is not null));
    }

    private async Task RunBroadcastLoop(NetRomService netRom, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try { netRom.BroadcastNodes(); }
            catch (Exception ex) { output.WriteLine($"broadcast failed (continuing): {ex.Message}"); }
            try { await Task.Delay(BroadcastEvery, ct); }
            catch (OperationCanceledException) { break; }
        }
    }

    // Hear BPQ via the real PASSWORD->SENDNODES sysop handshake (source-verified; the same driver
    // shape NetRomL4CircuitViaAxudp/NetRomNodesIngestViaNetsim use). When un-gating, lift the shared
    // BpqTelnet driver out of NetRomL4CircuitViaAxudp into a shared helper rather than re-implementing
    // it; this skeleton references the operation it needs (SENDNODES) by intent.
    private async Task<bool> ProvokeAndHearBpqAsync(NetRomService netRom, CancellationToken outer)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(outer);
        cts.CancelAfter(TimeSpan.FromSeconds(45));
        var nextResend = DateTimeOffset.MinValue;
        while (!cts.IsCancellationRequested)
        {
            if (netRom.Snapshot().Neighbours.Any(n => n.Neighbour == BpqCall))
            {
                return true;
            }
            if (DateTimeOffset.UtcNow >= nextResend)
            {
                try { await SendNodesViaTelnetAsync(cts.Token); }
                catch (OperationCanceledException) when (cts.IsCancellationRequested) { break; }
                catch (Exception ex) { output.WriteLine($"SENDNODES trigger failed (will retry): {ex.Message}"); }
                nextResend = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(8);
            }
            try { await Task.Delay(250, cts.Token); } catch (OperationCanceledException) { break; }
        }
        return netRom.Snapshot().Neighbours.Any(n => n.Neighbour == BpqCall);
    }

    // Placeholder for the shared BPQ sysop SENDNODES driver. When un-gating, replace this with a
    // call into the extracted shared BpqTelnet helper (currently a private nested class in
    // NetRomL4CircuitViaAxudp). Left as a throwing stub so an accidental un-gate without wiring the
    // driver fails loudly rather than silently passing/skipping.
    private Task SendNodesViaTelnetAsync(CancellationToken ct) =>
        throw new NotImplementedException(
            "Wire the shared BPQ PASSWORD->SENDNODES telnet driver here when un-gating this lane " +
            "(extract BpqTelnet from NetRomL4CircuitViaAxudp into a shared helper). " +
            "See docs/netrom-inp3-interop.md §4.");

    private void DumpSnapshot(NetRomRoutingSnapshot snap, string when)
    {
        output.WriteLine($"[{when}] {snap.NeighbourCount} neighbour(s), {snap.DestinationCount} destination(s):");
        foreach (var n in snap.Neighbours)
        {
            output.WriteLine($"  neighbour {n.Alias}:{n.Neighbour} port={n.PortId} qual={n.PathQuality}");
        }
        foreach (var d in snap.Destinations)
        {
            var routes = string.Join(", ", d.Routes.Select(r =>
                $"via {r.Neighbour} q{r.Quality} obs{r.Obsolescence}" +
                (r.Inp3 is { } i ? $" INP3[t={i.TargetTimeMs}ms hops={i.HopCount}]" : "")));
            output.WriteLine($"  dest {d.Alias}:{d.Destination} [{routes}]");
        }
    }

    private static async Task<bool> IsTcpPortReachable(string host, int port)
    {
        try
        {
            using var client = new TcpClient();
            using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));
            await client.ConnectAsync(host, port, cts.Token);
            return client.Connected;
        }
        catch { return false; }
    }
}
