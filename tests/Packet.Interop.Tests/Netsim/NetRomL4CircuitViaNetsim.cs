using System.Globalization;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using AwesomeAssertions;
using Packet.Ax25;
using Packet.Ax25.Session;
using Packet.Core;
using Packet.Kiss;
using Packet.NetRom.Routing;
using Packet.NetRom.Transport;
using Packet.Node.Core.Configuration;
using Packet.Node.Core.Console;
using Packet.Node.Core.NetRom;
using Xunit;
using Xunit.Abstractions;

namespace Packet.Interop.Tests.Netsim;

/// <summary>
/// The NET/ROM <b>L3-origination + L4-circuit</b> interop against a real LinBPQ over
/// net-sim — the deferred "real-BPQ-L4 interop" the #305 read-only slice left as a
/// §7.1 follow-up. Where <see cref="NetRomNodesIngestViaNetsim"/> proves pdn
/// <em>hears</em> BPQ's NODES (read-only), this proves the <em>transmitting</em>
/// stack interoperates both ways:
/// </summary>
/// <remarks>
/// <para>
/// <b>1. L3 both ways.</b> A pdn <see cref="NetRomService"/> with
/// <see cref="NetRomConfig.Broadcast"/> on and a node alias <em>originates</em> a
/// NODES broadcast on the netsim channel; we then query LinBPQ's own node interface
/// (the sysop <c>ROUTES</c>/<c>NODES</c> commands, reached via the real
/// positional-challenge <c>PASSWORD</c> handshake) and assert <b>BPQ learned pdn as
/// a node + route</b>. The reverse (pdn hears BPQ's NODES) is the #305 assertion,
/// re-checked here.
/// </para>
/// <para>
/// <b>2. L4 circuit pdn → BPQ (the core deliverable).</b> With
/// <see cref="NetRomConfig.Connect"/> on, pdn routes a <c>connect</c> to BPQ's node
/// over NET/ROM: <see cref="NetRomService.ConnectCircuitAsync"/> opens an interlink
/// AX.25 PID-0xCF session to BPQ and originates an L4 circuit end-to-end; a command
/// round-trips to BPQ's node prompt over the circuit; then it disconnects cleanly.
/// This is the deterministic core (pdn establishes the interlink first, so there is
/// no fresh-link race).
/// </para>
/// <para>
/// <b>3. L4 circuit BPQ → pdn (reverse).</b> After BPQ has learned pdn (step 1), we
/// drive BPQ's telnet <c>C &lt;pdn-alias&gt;</c> so BPQ originates the circuit to us;
/// pdn's <see cref="CircuitManager.IncomingCircuit"/> fires, pdn accepts + bridges a
/// tiny echo console, and data round-trips. Because BPQ pipelines its Connect
/// Request I-frame onto a fresh interlink (and abandons it if our AX.25 session
/// isn't up yet — BPQ does not re-originate the L4 connect), we re-drive BPQ's
/// connect on a bounded cadence so a single raced/lost frame doesn't fail the run.
/// </para>
/// <para>
/// <b>The BPQ framing finding (#308 follow-up).</b> Verifying our L4 framing against
/// real BPQ surfaced one genuine divergence and fixed it: the Connect Request's
/// <em>proposed window</em> and <em>originating user/node</em> travel in the INFO
/// field (<c>[window][user][node]</c>, the de-facto NET/ROM form BPQ originates +
/// accepts), <b>not</b> the transport-header TX byte where pdn previously put the
/// window. BPQ accepted our circuit either way (the window mis-placement only
/// mis-set the negotiated window), but inbound we mis-read BPQ's originating user +
/// proposed window until the fix. See <see cref="ConnectRequestInfo"/> and the
/// strict-vs-pragmatic audit.
/// </para>
/// <para>
/// <b>Topology note.</b> On this net-sim topology node a (8100, "ours") links to
/// node c (8102, LinBPQ) but NOT to node d (8103, XRouter); BPQ and XRouter cannot
/// hear each other, so a multi-hop pdn → via-BPQ → XRouter routed connect is not
/// feasible here and is not attempted.
/// </para>
/// <para>
/// Bring the stack up with
/// <c>docker compose -f docker/compose.interop.yml up -d --wait</c>. Per
/// <c>docs/plan.md</c> §7.1 the interop matrix is environmentally flaky and
/// non-blocking; re-run a lone red in isolation (the #47 box-contention lesson).
/// </para>
/// </remarks>
[Trait("Category", "Interop")]
[Trait("Group", "NetRom")]   // isolated from the timing-sensitive AX.25 tests — interop.yml runs the NET/ROM group against a freshly-recreated stack (see docs/plan.md §7.2)
[Collection(NetsimCollection.Name)]
public class NetRomL4CircuitViaNetsim
{
    private const string Host = "127.0.0.1";
    private const int OurKissPort = 8100;                        // net-sim node a — the shared "ours" endpoint
    private const int BpqTelnetPort = 8010;                      // LinBPQ node prompt

    // A pdn node identity distinct from every other interop test's callsigns so a
    // torn-down link/route from another test cannot be confused with ours, and BPQ
    // has no stale interlink to this callsign at the start of the run.
    private static readonly Callsign OurCall = new("PNL4ND", 0);
    private const string OurAlias = "PNL4";
    private static readonly Callsign BpqCall = new("PN0TST", 0);

    private const string BpqPasswordText = "WONTLISTEN";          // docker/linbpq/bpq32.cfg PASSWORD=

    // pdn originates NODES on this cadence so BPQ learns it; bounded overall.
    private static readonly TimeSpan BroadcastEvery = TimeSpan.FromSeconds(4);
    private static readonly TimeSpan BpqLearnsUsBudget = TimeSpan.FromSeconds(90);

    // We must hear BPQ's NODES first (so we have a route to it for the outbound L4).
    private static readonly TimeSpan HearBpqBudget = TimeSpan.FromSeconds(90);
    private static readonly TimeSpan BpqResendEvery = TimeSpan.FromSeconds(12);

    // L4 connect/data budgets — generous-but-bounded for the simulated half-duplex
    // AFSK1200 channel (these chain several real-time AX.25 + L4 handshakes).
    private static readonly TimeSpan OutboundConnectBudget = TimeSpan.FromSeconds(50);
    private static readonly TimeSpan InboundCircuitBudget = TimeSpan.FromSeconds(120);
    private static readonly TimeSpan DataRoundTripBudget = TimeSpan.FromSeconds(30);

    private readonly ITestOutputHelper output;

    public NetRomL4CircuitViaNetsim(ITestOutputHelper output) => this.output = output;

    [Fact]
    public async Task Pdn_originates_NODES_and_runs_L4_circuits_both_ways_with_real_linbpq()
    {
        using var cts = new CancellationTokenSource(
            BpqLearnsUsBudget + HearBpqBudget + OutboundConnectBudget + InboundCircuitBudget + TimeSpan.FromSeconds(120));

        await using var kiss = await KissTcpClient.ConnectAsync(Host, OurKissPort, cts.Token);
        await using var listener = new Ax25Listener(kiss, new Ax25ListenerOptions { MyCall = OurCall });

        // `await using` (not `using`): NetRomService.DisposeAsync runs the GRACEFUL
        // teardown — it DISCs every interlink AX.25 session and waits (bounded) for the
        // DISC/UA to round-trip on the wire BEFORE the listener below is disposed (the
        // listener is declared earlier, so it disposes after this). That stops the test
        // leaving LinBPQ a half-open interlink it would poll onto the shared channel and
        // flake a subsequent timing-sensitive AX.25 test (the contamination this PR fixes).
        await using var netRom = new NetRomService(new NetRomConfig
        {
            Enabled = true,
            Broadcast = true,
            Connect = true,
            Alias = OurAlias,
            // Fast-ish L4 retransmit so a dropped Info on the sim channel recovers
            // inside the data round-trip budget.
            TransportTimeoutSeconds = 4,
            TransportRetries = 5,
        });

        // Bridge an inbound NET/ROM circuit (BPQ -> pdn) to a tiny echo console so we
        // can both observe IncomingCircuit AND prove data round-trips over the circuit.
        var inboundSeen = new TaskCompletionSource<Callsign>(TaskCreationOptions.RunContinuationsAsynchronously);
        var inboundEchoed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        netRom.RunInboundConsole = async (conn, ct) =>
        {
            output.WriteLine("inbound circuit console started; sending banner");
            await conn.WriteAsync(Encoding.ASCII.GetBytes("pdn-l4\r"), ct);
            while (!ct.IsCancellationRequested)
            {
                var data = await conn.ReadAsync(ct);
                if (data.IsEmpty) break;
                var text = Encoding.ASCII.GetString(data.Span);
                output.WriteLine($"inbound circuit RX: {text.Replace("\r", "\\r")}");
                await conn.WriteAsync(Encoding.ASCII.GetBytes("ack:" + text), ct);
                inboundEchoed.TrySetResult();
            }
        };

        netRom.AttachPort("vhf", OurCall, listener);
        if (netRom.Circuits is not null)
        {
            netRom.Circuits.IncomingCircuit += (_, e) =>
            {
                output.WriteLine($"IncomingCircuit from node={e.RemoteNode} user={e.OriginatingUser} win={e.ProposedWindow}");
                inboundSeen.TrySetResult(e.RemoteNode);
            };
        }
        await listener.StartAsync(cts.Token);

        // Keep originating our NODES for the whole test so BPQ keeps a fresh route to
        // us (its obsolescence count would otherwise decay). Stopped at the end.
        using var broadcastCts = CancellationTokenSource.CreateLinkedTokenSource(cts.Token);
        var broadcaster = RunBroadcastLoop(netRom, broadcastCts.Token);

        try
        {
            // ── L3 (a): pdn hears BPQ's NODES (so we have a route to BPQ) ─────────
            // Provoke BPQ's immediate NODES via the sysop SENDNODES handshake, then
            // wait (bounded, re-triggering) until we have learned BPQ as a neighbour.
            var heardBpq = await ProvokeAndHearBpqAsync(netRom, cts.Token);
            heardBpq.Should().BeTrue(
                "pdn must hear LinBPQ's NODES (PN0TST) so it has a NET/ROM route to dial the L4 circuit over");
            DumpSnapshot(netRom.Snapshot(), "after hearing BPQ");

            // ── L3 (b): BPQ learns pdn as a node + route ─────────────────────────
            // Query BPQ's own node interface and assert our alias/callsign appears in
            // its ROUTES (a directly-heard NET/ROM neighbour on the netsim port).
            var bpqLearnedUs = await WaitForBpqToLearnUsAsync(cts.Token);
            bpqLearnedUs.Should().BeTrue(
                "LinBPQ must learn pdn as a NET/ROM node/route from pdn's originated NODES broadcast (checked via BPQ's ROUTES/NODES)");

            // ── L4 (1): pdn -> BPQ circuit (the core deliverable) ────────────────
            var dest = netRom.Snapshot().ResolveDestination("PNTST")
                       ?? netRom.Snapshot().ResolveDestination(BpqCall.ToString());
            dest.Should().NotBeNull("pdn resolves BPQ (alias PNTST / call PN0TST) as a NET/ROM destination to route to");

            output.WriteLine($"opening L4 circuit pdn -> {dest!.Destination} via {dest.BestRoute?.Neighbour}");
            using (var connectCts = CancellationTokenSource.CreateLinkedTokenSource(cts.Token))
            {
                connectCts.CancelAfter(OutboundConnectBudget);
                await using var connection = await netRom.ConnectCircuitAsync(dest, OurCall, connectCts.Token);

                connection.TransportKind.Should().Be(NodeTransportKind.NetRom,
                    "the console relays the user against a NET/ROM L4 circuit, not a direct AX.25 session");
                netRom.Circuits!.Circuits.Should().NotBeEmpty("pdn holds the originating L4 circuit to BPQ");
                output.WriteLine("*** L4 circuit pdn -> BPQ ESTABLISHED ***");

                // Data round-trip (the proof L4 Information flows BOTH ways): send a
                // command into the circuit (pdn -> BPQ Information) and read BPQ's node
                // reply back out (BPQ -> pdn Information). BPQ's node prompt is
                // "PNTST:PN0TST}" so its callsign appears in the reply. We accumulate
                // ALL circuit RX over a bounded window rather than requiring a specific
                // frame first — BPQ also relays unrelated NET/ROM transit chatter (e.g.
                // XRouter L3RTT keep-alives) over the session, which can interleave with
                // the node output, so order is not guaranteed.
                await connection.WriteAsync(Encoding.ASCII.GetBytes("\r"), cts.Token);          // nudge the prompt
                await connection.WriteAsync(Encoding.ASCII.GetBytes("PORTS\r"), cts.Token);     // a sysop-free node command
                var reply = await ReadCircuitAccumulateAsync(connection, "PN0TST", DataRoundTripBudget, cts.Token);
                reply.Should().Contain("PN0TST",
                    "a command sent over the L4 circuit reaches BPQ's node and the reply (carrying BPQ's PN0TST node identity) relays back — Information round-trips both directions over the circuit");
                output.WriteLine($"circuit data round-trip; reply contained BPQ identity. Sample: {Collapse(reply).Substring(0, Math.Min(160, Collapse(reply).Length))}");

                // Clean disconnect: disposing the connection sends a Disconnect
                // Request; BPQ answers Disconnect Acknowledge and the circuit closes.
            }
            // After the using-block the circuit has been torn down; BPQ acked it.
            await WaitUntil(() => netRom.Circuits!.Circuits.All(c => c.State == NetRomCircuitState.Disconnected),
                TimeSpan.FromSeconds(15), cts.Token);
            output.WriteLine("*** L4 circuit pdn -> BPQ DISCONNECTED cleanly ***");

            // ── L4 (2): BPQ -> pdn circuit (reverse) ─────────────────────────────
            // Drive BPQ's telnet `C <pdn-alias>` so BPQ originates the L4 circuit to
            // us, then push a data line through BPQ which it relays over the circuit to
            // pdn's echo console (proving BPQ -> pdn Information); pdn echoes it, and
            // inboundEchoed fires. Re-drive on a bounded cadence: BPQ pipelines its
            // Connect Request onto a fresh interlink and abandons it if our AX.25
            // session isn't up yet (BPQ does not re-originate the L4 connect), so a
            // single attempt can race on the lossy channel; the retry lands it.
            var reverseEstablished = await DriveBpqConnectAndDataAsync(inboundSeen, inboundEchoed, cts.Token);
            reverseEstablished.Should().BeTrue(
                "LinBPQ originates a NET/ROM L4 circuit to pdn (C <alias>) and pdn's CircuitManager raises IncomingCircuit");

            inboundSeen.Task.IsCompletedSuccessfully.Should().BeTrue("the reverse circuit reached pdn (IncomingCircuit fired)");

            // Data round-trip over the reverse circuit: BPQ relayed a line to pdn's
            // echo console, which acked it back over the circuit.
            inboundEchoed.Task.IsCompletedSuccessfully.Should().BeTrue(
                "data round-trips over the BPQ -> pdn L4 circuit (BPQ relays a line; pdn's echo console replies over the circuit)");
            output.WriteLine("*** L4 circuit BPQ -> pdn ESTABLISHED + data round-tripped ***");
        }
        finally
        {
            await broadcastCts.CancelAsync();
            try { await broadcaster; } catch (OperationCanceledException) { /* expected */ }
        }
    }

    // ── pdn NODES origination loop ───────────────────────────────────────────
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

    // ── L3: hear BPQ (provoke SENDNODES, bounded, re-trigger) ────────────────
    private async Task<bool> ProvokeAndHearBpqAsync(NetRomService netRom, CancellationToken outer)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(outer);
        cts.CancelAfter(HearBpqBudget);
        var nextResend = DateTimeOffset.MinValue;
        while (!cts.IsCancellationRequested)
        {
            if (netRom.Snapshot().Neighbours.Any(n => n.Neighbour == BpqCall))
            {
                return true;
            }
            if (DateTimeOffset.UtcNow >= nextResend)
            {
                try { await BpqTelnet.SendNodesAsync(Host, BpqTelnetPort, BpqPasswordText, output, cts.Token); }
                catch (OperationCanceledException) when (cts.IsCancellationRequested) { break; }
                catch (Exception ex) { output.WriteLine($"SENDNODES trigger failed (will retry): {ex.Message}"); }
                nextResend = DateTimeOffset.UtcNow + BpqResendEvery;
            }
            try { await Task.Delay(250, cts.Token); } catch (OperationCanceledException) { break; }
        }
        return netRom.Snapshot().Neighbours.Any(n => n.Neighbour == BpqCall);
    }

    // ── L3: BPQ learns us (query its ROUTES/NODES via sysop session) ─────────
    private async Task<bool> WaitForBpqToLearnUsAsync(CancellationToken outer)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(outer);
        cts.CancelAfter(BpqLearnsUsBudget);
        while (!cts.IsCancellationRequested)
        {
            try
            {
                var (nodes, routes) = await BpqTelnet.QueryNodesAndRoutesAsync(Host, BpqTelnetPort, BpqPasswordText, output, cts.Token);
                // BPQ shows a learned NET/ROM neighbour in ROUTES (e.g. "> 3 PNL4ND  192 1")
                // and the node in NODES ("PNL4:PNL4ND"). Match our callsign base in
                // either; ROUTES is the directly-heard-neighbour proof.
                if (routes.Contains(OurCall.Base, StringComparison.OrdinalIgnoreCase) ||
                    nodes.Contains(OurCall.Base, StringComparison.OrdinalIgnoreCase))
                {
                    output.WriteLine($"BPQ learned us. NODES=[{Collapse(nodes)}] ROUTES=[{Collapse(routes)}]");
                    return true;
                }
            }
            catch (OperationCanceledException) when (cts.IsCancellationRequested) { break; }
            catch (Exception ex) { output.WriteLine($"BPQ query failed (will retry): {ex.Message}"); }
            try { await Task.Delay(3000, cts.Token); } catch (OperationCanceledException) { break; }
        }
        return false;
    }

    // ── L4: drive BPQ -> pdn connect (+ data) with bounded retry ─────────────
    private async Task<bool> DriveBpqConnectAndDataAsync(
        TaskCompletionSource<Callsign> inboundSeen, TaskCompletionSource inboundEchoed, CancellationToken outer)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(outer);
        cts.CancelAfter(InboundCircuitBudget);
        while (!cts.IsCancellationRequested)
        {
            if (inboundEchoed.Task.IsCompleted)
            {
                return true;
            }
            try
            {
                // One telnet session: `C <alias>` → wait for the circuit to reach pdn
                // (IncomingCircuit) → push a data line BPQ relays to pdn over the
                // circuit → hold until pdn echoes it back (inboundEchoed) or the
                // per-attempt budget elapses.
                await BpqTelnet.ConnectAndSendAsync(
                    Host, BpqTelnetPort, BpqPasswordText, OurAlias, "hello-from-bpq\r",
                    TimeSpan.FromSeconds(30),
                    () => inboundSeen.Task.IsCompleted, () => inboundEchoed.Task.IsCompleted,
                    output, cts.Token);
            }
            catch (OperationCanceledException) when (cts.IsCancellationRequested) { break; }
            catch (Exception ex) { output.WriteLine($"BPQ C {OurAlias} attempt failed (will retry): {ex.Message}"); }

            if (inboundSeen.Task.IsCompleted)
            {
                // Circuit reached pdn; even if the echo round-trip needs another nudge,
                // the reverse-establish goal is met. Keep looping (bounded) to land the
                // data echo too.
                if (inboundEchoed.Task.IsCompleted) return true;
            }
            try { await Task.Delay(2000, cts.Token); } catch (OperationCanceledException) { break; }
        }
        // Establishment is the hard requirement; the data echo is asserted separately.
        return inboundSeen.Task.IsCompleted;
    }

    // ── helpers ──────────────────────────────────────────────────────────────
    // Accumulate circuit RX until the needle is seen or the budget elapses (whichever
    // first). Unlike a single-frame read, this tolerates BPQ interleaving unrelated
    // NET/ROM transit frames before the node output we want.
    private static async Task<string> ReadCircuitAccumulateAsync(INodeConnection conn, string needle, TimeSpan budget, CancellationToken outer)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(outer);
        cts.CancelAfter(budget);
        var sb = new StringBuilder();
        while (!cts.IsCancellationRequested)
        {
            ReadOnlyMemory<byte> data;
            try { data = await conn.ReadAsync(cts.Token); }
            catch (OperationCanceledException) { break; }
            if (!data.IsEmpty)
            {
                sb.Append(Encoding.ASCII.GetString(data.Span));
                if (sb.ToString().Contains(needle, StringComparison.Ordinal)) break;
            }
            else if (sb.Length > 0)
            {
                // EOF on the circuit (closed) — return what we have.
                break;
            }
        }
        return sb.ToString();
    }

    private static async Task WaitUntil(Func<bool> condition, TimeSpan budget, CancellationToken outer)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(outer);
        cts.CancelAfter(budget);
        while (!cts.IsCancellationRequested)
        {
            if (condition()) return;
            try { await Task.Delay(250, cts.Token); } catch (OperationCanceledException) { return; }
        }
    }

    private static string Collapse(string s) => Regex.Replace(s, @"\s+", " ").Trim();

    private void DumpSnapshot(NetRomRoutingSnapshot snap, string when)
    {
        output.WriteLine($"[{when}] {snap.NeighbourCount} neighbour(s), {snap.DestinationCount} destination(s):");
        foreach (var n in snap.Neighbours)
        {
            output.WriteLine($"  neighbour {n.Alias}:{n.Neighbour} port={n.PortId} qual={n.PathQuality}");
        }
        foreach (var d in snap.Destinations)
        {
            var routes = string.Join(", ", d.Routes.Select(r => $"via {r.Neighbour} q{r.Quality} obs{r.Obsolescence}"));
            output.WriteLine($"  dest {d.Alias}:{d.Destination} [{routes}]");
        }
    }

    /// <summary>
    /// IAC-aware telnet driver for LinBPQ's node prompt: performs the real
    /// positional-challenge sysop auth (so the sysop command set — SENDNODES, ROUTES,
    /// NODES — is unlocked), and exposes the operations this test needs.
    /// </summary>
    /// <remarks>
    /// Mirrors the <c>BpqSysop</c> driver in <see cref="NetRomNodesIngestViaNetsim"/>
    /// (the challenge: log in as the non-sysop <c>netop</c> user → bare
    /// <c>PASSWORD</c> → five 1-based positions → answer the chars at those positions
    /// → <c>Ok</c>). Kept a separate nested type so each test owns its driver.
    /// </remarks>
    private static class BpqTelnet
    {
        private const byte IAC = 255, DONT = 254, DO = 253, WONT = 252, WILL = 251;

        public static async Task SendNodesAsync(string host, int port, string passwordText, ITestOutputHelper output, CancellationToken ct)
        {
            using var tcp = new TcpClient();
            await tcp.ConnectAsync(host, port, ct);
            using var stream = tcp.GetStream();
            await AuthenticateAsync(stream, passwordText, output, ct);
            await SendLineAsync(stream, "SENDNODES", ct);
            var resp = await ReadLineAfterPromptAsync(stream, TimeSpan.FromSeconds(6), ct);
            if (!resp.Contains("Ok", StringComparison.Ordinal) || resp.Contains("SYSOP", StringComparison.Ordinal))
            {
                throw new InvalidOperationException($"BPQ did not accept SENDNODES: {resp.Trim()}");
            }
            await Task.Delay(300, ct);
        }

        public static async Task<(string Nodes, string Routes)> QueryNodesAndRoutesAsync(
            string host, int port, string passwordText, ITestOutputHelper output, CancellationToken ct)
        {
            using var tcp = new TcpClient();
            await tcp.ConnectAsync(host, port, ct);
            using var stream = tcp.GetStream();
            await AuthenticateAsync(stream, passwordText, output, ct);
            await SendLineAsync(stream, "NODES", ct);
            var nodes = await ReadForAsync(stream, TimeSpan.FromSeconds(3), ct);
            await SendLineAsync(stream, "ROUTES", ct);
            var routes = await ReadForAsync(stream, TimeSpan.FromSeconds(3), ct);
            return (nodes, routes);
        }

        /// <summary>
        /// Issue <c>C &lt;alias&gt;</c>; once the circuit reaches pdn
        /// (<paramref name="established"/>), send <paramref name="payload"/> (which BPQ
        /// relays over the circuit to pdn) and keep reading until pdn has echoed it
        /// (<paramref name="echoed"/>) or <paramref name="hold"/> elapses. Holding the
        /// telnet session open is essential — closing it tears the circuit down before
        /// the echo can round-trip.
        /// </summary>
        public static async Task ConnectAndSendAsync(
            string host, int port, string passwordText, string alias, string payload, TimeSpan hold,
            Func<bool> established, Func<bool> echoed, ITestOutputHelper output, CancellationToken ct)
        {
            using var tcp = new TcpClient();
            await tcp.ConnectAsync(host, port, ct);
            using var stream = tcp.GetStream();
            await AuthenticateAsync(stream, passwordText, output, ct);
            await SendLineAsync(stream, "C " + alias, ct);

            var buf = new byte[4096];
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(hold);
            var acc = new StringBuilder();
            bool payloadSent = false;
            while (!cts.IsCancellationRequested && !echoed())
            {
                // Push the payload once BPQ's telnet reports the circuit is up
                // ("Connected to ...") AND pdn has seen the IncomingCircuit — only then
                // is BPQ in transparent passthrough; earlier input would hit BPQ's node
                // command interpreter ("Invalid command") instead of the circuit.
                if (!payloadSent && established() &&
                    acc.ToString().Contains("Connected", StringComparison.OrdinalIgnoreCase))
                {
                    payloadSent = true;
                    await Task.Delay(500, cts.Token);   // let BPQ settle into passthrough
                    await SendLineRawAsync(stream, payload, ct);
                    output.WriteLine($"BPQ relayed payload over circuit: {payload.Replace("\r", "\\r")}");
                }

                var readCts = CancellationTokenSource.CreateLinkedTokenSource(cts.Token);
                readCts.CancelAfter(TimeSpan.FromSeconds(2));   // poll so we can (re)check the connect/echo gates
                int n;
                try { n = await stream.ReadAsync(buf, readCts.Token); }
                catch (OperationCanceledException) when (!cts.IsCancellationRequested) { continue; }
                catch (OperationCanceledException) { break; }
                finally { readCts.Dispose(); }
                if (n == 0) break;
                var sb = new StringBuilder();
                AppendStripIac(stream, sb, buf, n);
                acc.Append(sb);
            }
            var text = acc.ToString();
            if (text.Length > 0)
            {
                output.WriteLine($"BPQ C {alias}: {Collapse(text)}");
            }
        }

        private static async Task SendLineRawAsync(NetworkStream stream, string line, CancellationToken ct)
        {
            var bytes = Encoding.ASCII.GetBytes(line);
            await stream.WriteAsync(bytes, ct);
            await stream.FlushAsync(ct);
        }

        private static async Task AuthenticateAsync(NetworkStream stream, string passwordText, ITestOutputHelper output, CancellationToken ct)
        {
            await ReadUntilAsync(stream, "user", TimeSpan.FromSeconds(8), ct);
            await SendLineAsync(stream, "netop", ct);
            await ReadUntilAsync(stream, "password", TimeSpan.FromSeconds(8), ct);
            await SendLineAsync(stream, "netop", ct);
            await ReadUntilAsync(stream, "Telnet Server", TimeSpan.FromSeconds(8), ct);

            await SendLineAsync(stream, "PASSWORD", ct);
            var challenge = await ReadLineAfterPromptAsync(stream, TimeSpan.FromSeconds(6), ct);
            var positions = ParsePositions(challenge);
            var answer = SolveChallenge(positions, passwordText);
            output.WriteLine($"BPQ PASSWORD challenge {string.Join(' ', positions)} -> answer {answer}");

            await SendLineAsync(stream, "PASSWORD " + answer, ct);
            var authResp = await ReadLineAfterPromptAsync(stream, TimeSpan.FromSeconds(6), ct);
            if (!authResp.Contains("Ok", StringComparison.Ordinal))
            {
                throw new InvalidOperationException($"BPQ rejected the PASSWORD challenge answer: {authResp.Trim()}");
            }
        }

        private static int[] ParsePositions(string challenge)
        {
            var nums = Regex.Matches(challenge, @"\d+").Select(m => int.Parse(m.Value, CultureInfo.InvariantCulture)).ToArray();
            if (nums.Length < 5)
            {
                throw new InvalidOperationException($"Could not parse 5 challenge positions from: {challenge.Trim()}");
            }
            return nums[^5..];
        }

        private static string SolveChallenge(int[] positions, string passwordText)
        {
            var sb = new StringBuilder(positions.Length);
            foreach (var p in positions)
            {
                int idx = Math.Clamp(p - 1, 0, passwordText.Length - 1);
                sb.Append(passwordText[idx]);
            }
            return sb.ToString();
        }

        private static async Task SendLineAsync(NetworkStream stream, string line, CancellationToken ct)
        {
            var bytes = Encoding.ASCII.GetBytes(line + "\r");
            await stream.WriteAsync(bytes, ct);
            await stream.FlushAsync(ct);
        }

        private static async Task<string> ReadUntilAsync(NetworkStream stream, string needle, TimeSpan budget, CancellationToken ct)
        {
            var sb = new StringBuilder();
            var buf = new byte[4096];
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(budget);
            while (!cts.IsCancellationRequested)
            {
                int n;
                try { n = await stream.ReadAsync(buf, cts.Token); }
                catch (OperationCanceledException) { break; }
                if (n == 0) break;
                AppendStripIac(stream, sb, buf, n);
                if (needle.Length > 0 && sb.ToString().Contains(needle, StringComparison.Ordinal)) break;
            }
            return sb.ToString();
        }

        private static async Task<string> ReadLineAfterPromptAsync(NetworkStream stream, TimeSpan budget, CancellationToken ct)
        {
            var sb = new StringBuilder();
            var buf = new byte[4096];
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(budget);
            while (!cts.IsCancellationRequested)
            {
                int n;
                try { n = await stream.ReadAsync(buf, cts.Token); }
                catch (OperationCanceledException) { break; }
                if (n == 0) break;
                AppendStripIac(stream, sb, buf, n);
                if (sb.ToString().Contains('\n', StringComparison.Ordinal)) break;
            }
            return sb.ToString();
        }

        // Read everything that arrives within the window (multi-line command output).
        private static async Task<string> ReadForAsync(NetworkStream stream, TimeSpan budget, CancellationToken ct)
        {
            var sb = new StringBuilder();
            var buf = new byte[4096];
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(budget);
            while (!cts.IsCancellationRequested)
            {
                int n;
                try { n = await stream.ReadAsync(buf, cts.Token); }
                catch (OperationCanceledException) { break; }
                if (n == 0) break;
                AppendStripIac(stream, sb, buf, n);
            }
            return sb.ToString();
        }

        private static void AppendStripIac(NetworkStream stream, StringBuilder sb, byte[] buf, int n)
        {
            for (int i = 0; i < n; i++)
            {
                if (buf[i] != IAC)
                {
                    sb.Append((char)buf[i]);
                    continue;
                }
                if (i + 2 >= n) break;
                byte verb = buf[i + 1];
                byte opt = buf[i + 2];
                i += 2;
                byte reply = verb switch { DO => WONT, WILL => DONT, _ => (byte)0 };
                if (reply != 0)
                {
                    _ = stream.WriteAsync(new byte[] { IAC, reply, opt }).AsTask();
                }
            }
        }
    }
}
