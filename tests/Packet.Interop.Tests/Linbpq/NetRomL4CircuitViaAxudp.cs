using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using AwesomeAssertions;
using Packet.Ax25;
using Packet.Ax25.Session;
using Packet.Core;
using Packet.Interop.Tests.Netsim;
using Packet.NetRom.Routing;
using Packet.NetRom.Transport;
using Packet.NetRom.Wire;
using Packet.Node.Core.Configuration;
using Packet.Node.Core.Console;
using Packet.Node.Core.NetRom;
using Packet.Node.Core.Transports;
using Packet.Node.Core.Hosting;
using Packet.Node.Rhp;
using Packet.Rhp2;
using Packet.Rhp2.Server;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using Xunit.Abstractions;

namespace Packet.Interop.Tests.Linbpq;

/// <summary>
/// <b>Tier 2 (frame-perfect, modem-less) NET/ROM L3-origination + L4-circuit
/// interop vs real LinBPQ over AXUDP.</b> The AXUDP re-home of the load-sensitive
/// net-sim <c>NetRomL4CircuitViaNetsim</c> (removed): it asserts the <em>same</em>
/// protocol behaviour against a real LinBPQ 6.0.25.23 - NODES both ways, an L4 circuit each
/// way with Information round-tripping, and the Connect-Request info-field framing
/// - but over a BPQAXIP/UDP tunnel (<see cref="AxudpFrameTransport"/>) rather than
/// net-sim's software-AFSK channel. AXUDP is AX.25-frames-over-UDP, so it sheds the
/// net-sim flakiness (CPU-glitch → audio-decode-fail → fake loss / half-duplex
/// collision) while staying a genuine real-BPQ interop. See <c>docs/plan.md</c> §7.
/// </summary>
/// <remarks>
/// <para>
/// <b>Transport.</b> pdn runs a real <see cref="Ax25Listener"/> over an
/// <see cref="AxudpFrameTransport"/> bound to a fixed host UDP port, pointed at BPQ's
/// BPQAXIP/UDP listener (127.0.0.1:8093). A <see cref="NetRomService"/> with
/// broadcast + connect on rides that one listener - the whole NET/ROM stack
/// (NODES origination, interlink AX.25 PID-0xCF sessions, L4 circuits) is
/// transport-agnostic, so swapping KISS-TCP for the AXUDP modem is the only change
/// from the net-sim test.
/// </para>
/// <para>
/// <b>BPQAXIP NODES delivery (source-verified + on-the-wire; see
/// <see cref="NetRomNodesIngestViaAxudp"/> for the full finding).</b> A point-to-point
/// BPQAXIP port drops its own NODES UI broadcast unless the recipient MAP entry is
/// flagged a broadcast target. The fixture's AXIP port carries
/// <c>BROADCAST NODES</c> + <c>MAP PNL4BX-1 … B</c> so BPQ emits its
/// <c>PN0TST &gt; NODES</c> to pdn over UDP - the AXUDP counterpart of the net-sim
/// port's <c>QUALITY=192</c>. pdn's own NODES is a UI frame addressed to
/// <c>NODES</c>; BPQ's <c>AUTOADDQUIET</c> learns pdn's reply route from it.
/// </para>
/// <para>
/// <b>1. L3 both ways.</b> pdn (<c>broadcast</c>, alias <c>PNL4B</c>) originates
/// NODES → BPQ learns it (queried via the sysop <c>ROUTES</c>/<c>NODES</c>), and
/// pdn hears BPQ's <c>PNTST:PN0TST</c> (provoked via <c>PASSWORD</c> →
/// <c>SENDNODES</c>).
/// </para>
/// <para>
/// <b>2. L4 circuit pdn → BPQ (the core deliverable).</b>
/// <see cref="NetRomService.ConnectCircuitAsync"/> opens an interlink AX.25 PID-0xCF
/// session over the AXUDP tunnel + originates an L4 circuit; a command round-trips
/// to BPQ's node prompt over the circuit (BPQ's <c>PN0TST</c> identity relays back -
/// Information both ways); then a clean Disconnect Request/Acknowledge.
/// </para>
/// <para>
/// <b>3. L4 circuit BPQ → pdn (reverse).</b> We drive BPQ's telnet <c>C PNL4B</c> so
/// BPQ originates the circuit to pdn over AXIP; pdn's
/// <see cref="CircuitManager.IncomingCircuit"/> fires, pdn accepts + bridges a tiny
/// echo console, and a line round-trips. BPQ pipelines its Connect Request onto a
/// fresh interlink and abandons it if pdn's AX.25 session isn't up yet, so we
/// re-drive on a bounded cadence (same as the net-sim test) - but over a lossless
/// tunnel the first attempt almost always lands.
/// </para>
/// <para>
/// <b>The Connect-Request info-field framing</b> (the #308/#309 finding, re-asserted
/// frame-perfectly here): BPQ carries the proposed window + originating user/node in
/// the INFO field (<c>[window][user][node]</c>), not the transport-header TX byte -
/// the <see cref="ConnectRequestInfo"/> codec. The reverse-circuit
/// <see cref="CircuitManager.IncomingCircuit"/> assertion reads BPQ's originating
/// user from exactly that field, so a regression there fails this test.
/// </para>
/// <para>
/// <b>Determinism.</b> Over a reliable UDP tunnel the budgets are far tighter than
/// the net-sim version's (no channel loss, no half-duplex collision, no audio decode
/// to glitch under CPU load) - this is the load-insensitivity the Tier-2 re-home
/// buys. Serialised into <see cref="NetsimCollection"/> (shared BPQ daemon + fixed
/// UDP port); tagged <c>Group=NetRom</c> so it runs in the clean-stack-fenced
/// NET/ROM phase of <c>interop.yml</c> (fresh BPQ → no learned-state carryover),
/// isolated from the timing-sensitive AX.25 tests. <c>NetRomService</c> is
/// <c>await using</c> so its <c>DisposeAsync</c> cleanly DISCs the interlink before
/// the listener disposes (BPQ-state hygiene - the #309 teardown discipline).
/// </para>
/// <para>
/// Bring the stack up with
/// <c>docker compose -f docker/compose.interop.yml up -d --wait</c>.
/// </para>
/// </remarks>
[Trait("Category", "Interop")]
[Trait("Group", "NetRom")]
[Collection(NetsimCollection.Name)]
public class NetRomL4CircuitViaAxudp
{
    private const string Host = "127.0.0.1";
    private const int BpqAxudpPort = 8093;    // BPQAXIP/UDP listener (published)
    private const int BpqHttpPort = 8008;     // liveness probe
    private const int BpqTelnetPort = 8010;   // node prompt

    // pdn binds the static-MAP target port + uses the static-MAP target callsign so
    // BPQ's reply route (static MAP + AUTOADD) is stable across re-runs.
    private const int PdnLocalPort = 8195;
    private static readonly Callsign OurCall = new("PNL4BX", 1);
    private const string OurAlias = "PNL4B";
    private static readonly Callsign BpqCall = new("PN0TST", 0);

    private const string BpqPasswordText = "WONTLISTEN";   // docker/linbpq/bpq32.cfg PASSWORD=

    // pdn originates NODES on this cadence so BPQ keeps a fresh route + AUTOADD entry.
    private static readonly TimeSpan BroadcastEvery = TimeSpan.FromSeconds(2);

    // Reliable UDP tunnel → tight, bounded budgets (no channel loss / half-duplex).
    private static readonly TimeSpan HearBpqBudget = TimeSpan.FromSeconds(45);
    private static readonly TimeSpan BpqResendEvery = TimeSpan.FromSeconds(8);
    private static readonly TimeSpan BpqLearnsUsBudget = TimeSpan.FromSeconds(45);
    private static readonly TimeSpan OutboundConnectBudget = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan InboundCircuitBudget = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan DataRoundTripBudget = TimeSpan.FromSeconds(20);

    private readonly ITestOutputHelper output;

    public NetRomL4CircuitViaAxudp(ITestOutputHelper output) => this.output = output;

    [SkippableFact]
    public async Task Pdn_originates_NODES_and_runs_L4_circuits_both_ways_with_real_linbpq_over_axudp()
    {
        Skip.IfNot(await IsTcpPortReachable(Host, BpqHttpPort),
            $"LinBPQ not reachable (HTTP {Host}:{BpqHttpPort}). Bring up the interop stack: 'docker compose -f docker/compose.interop.yml up -d --wait'.");

        using var cts = new CancellationTokenSource(
            HearBpqBudget + BpqLearnsUsBudget + OutboundConnectBudget + InboundCircuitBudget + TimeSpan.FromSeconds(90));

        await using var modem = new AxudpFrameTransport(new IPEndPoint(IPAddress.Loopback, BpqAxudpPort), PdnLocalPort);
        await using var listener = new Ax25Listener(modem, new Ax25ListenerOptions { MyCall = OurCall });

        // `await using` (not `using`): NetRomService.DisposeAsync runs the GRACEFUL
        // teardown - DISCs the interlink AX.25 session + waits (bounded) for DISC/UA on
        // the wire BEFORE the listener (declared earlier → disposed after) is torn down,
        // so BPQ isn't left a half-open interlink (the #309 hygiene discipline; cheap
        // over a UDP tunnel).
        await using var netRom = new NetRomService(new NetRomConfig
        {
            Enabled = true,
            Broadcast = true,
            Connect = true,
            // Fast-ish L4 retransmit; on a lossless tunnel this rarely fires.
            TransportTimeoutSeconds = 4,
            TransportRetries = 5,
        }, nodeAlias: OurAlias);

        // Bridge an inbound NET/ROM circuit (BPQ -> pdn) to a tiny echo console so we
        // observe IncomingCircuit AND prove data round-trips over the circuit.
        var inboundSeen = new TaskCompletionSource<Callsign>(TaskCreationOptions.RunContinuationsAsynchronously);
        var inboundEchoed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        netRom.RunInboundConsole = async (conn, ct) =>
        {
            output.WriteLine("inbound circuit console started; sending banner");
            await conn.WriteAsync(Encoding.ASCII.GetBytes("pdn-l4\r"), ct);
            while (!ct.IsCancellationRequested)
            {
                var data = await conn.ReadAsync(ct);
                if (data.IsEmpty)
                {
                    break;
                }

                var text = Encoding.ASCII.GetString(data.Span);
                output.WriteLine($"inbound circuit RX: {text.Replace("\r", "\\r")}");
                await conn.WriteAsync(Encoding.ASCII.GetBytes("ack:" + text), ct);
                inboundEchoed.TrySetResult();
            }
        };

        netRom.AttachPort("axudp", OurCall, listener, nodesBroadcast: true);   // our NODES must reach LinBPQ (#929: per-port, default off)
        if (netRom.Circuits is not null)
        {
            netRom.Circuits.IncomingCircuit += (_, e) =>
            {
                output.WriteLine($"IncomingCircuit from node={e.RemoteNode} user={e.OriginatingUser} win={e.ProposedWindow}");
                inboundSeen.TrySetResult(e.RemoteNode);
            };
        }
        await listener.StartAsync(cts.Token);

        // Keep originating pdn's NODES for the whole test (BPQ's obsolescence + AUTOADD
        // would otherwise decay). Stopped at the end.
        using var broadcastCts = CancellationTokenSource.CreateLinkedTokenSource(cts.Token);
        var broadcaster = RunBroadcastLoop(netRom, broadcastCts.Token);

        try
        {
            // ── L3 (a): pdn hears BPQ's NODES (so we have a route to BPQ) ─────────
            var heardBpq = await ProvokeAndHearBpqAsync(netRom, cts.Token);
            heardBpq.Should().BeTrue(
                "pdn must hear LinBPQ's NODES (PN0TST) over AXUDP so it has a NET/ROM route to dial the L4 circuit over");
            DumpSnapshot(netRom.Snapshot(), "after hearing BPQ (AXUDP)");

            // ── L3 (b): BPQ learns pdn as a node + route ─────────────────────────
            var bpqLearnedUs = await WaitForBpqToLearnUsAsync(cts.Token);
            bpqLearnedUs.Should().BeTrue(
                "LinBPQ must learn pdn as a NET/ROM node/route from pdn's originated NODES (checked via BPQ's ROUTES/NODES)");

            // ── L4 (1): pdn -> BPQ circuit (the core deliverable) ────────────────
            var dest = netRom.Snapshot().ResolveDestination("PNTST")
                       ?? netRom.Snapshot().ResolveDestination(BpqCall.ToString());
            dest.Should().NotBeNull("pdn resolves BPQ (alias PNTST / call PN0TST) as a NET/ROM destination to route to");

            output.WriteLine($"opening L4 circuit pdn -> {dest!.Destination} via {dest.BestRoute?.Neighbour} (AXUDP)");
            using (var connectCts = CancellationTokenSource.CreateLinkedTokenSource(cts.Token))
            {
                connectCts.CancelAfter(OutboundConnectBudget);
                await using var connection = await netRom.ConnectCircuitAsync(dest, OurCall, connectCts.Token);

                connection.TransportKind.Should().Be(NodeTransportKind.NetRom,
                    "the console relays the user against a NET/ROM L4 circuit, not a direct AX.25 session");
                netRom.Circuits!.Circuits.Should().NotBeEmpty("pdn holds the originating L4 circuit to BPQ");
                output.WriteLine("*** L4 circuit pdn -> BPQ ESTABLISHED over AXUDP ***");

                // Data round-trip (Information both ways): nudge the prompt, send a
                // sysop-free node command, accumulate BPQ's reply (which carries its
                // PN0TST node identity). BPQ may interleave NET/ROM transit chatter, so
                // accumulate over a bounded window rather than requiring a first frame.
                await connection.WriteAsync(Encoding.ASCII.GetBytes("\r"), cts.Token);
                await connection.WriteAsync(Encoding.ASCII.GetBytes("PORTS\r"), cts.Token);
                var reply = await ReadCircuitAccumulateAsync(connection, "PN0TST", DataRoundTripBudget, cts.Token);
                reply.Should().Contain("PN0TST",
                    "a command sent over the L4 circuit reaches BPQ's node and the reply (carrying BPQ's PN0TST node identity) relays back — Information round-trips both directions over the circuit");
                output.WriteLine($"circuit data round-trip; reply contained BPQ identity. Sample: {Collapse(reply).Substring(0, Math.Min(160, Collapse(reply).Length))}");
            }
            await WaitUntil(() => netRom.Circuits!.Circuits.All(c => c.State == NetRomCircuitState.Disconnected),
                TimeSpan.FromSeconds(15), cts.Token);
            output.WriteLine("*** L4 circuit pdn -> BPQ DISCONNECTED cleanly ***");

            // ── L4 (2): BPQ -> pdn circuit (reverse) ─────────────────────────────
            var reverseEstablished = await DriveBpqConnectAndDataAsync(inboundSeen, inboundEchoed, cts.Token);
            reverseEstablished.Should().BeTrue(
                "LinBPQ originates a NET/ROM L4 circuit to pdn (C <alias>) over AXUDP and pdn's CircuitManager raises IncomingCircuit");

            inboundSeen.Task.IsCompletedSuccessfully.Should().BeTrue("the reverse circuit reached pdn (IncomingCircuit fired)");
            inboundEchoed.Task.IsCompletedSuccessfully.Should().BeTrue(
                "data round-trips over the BPQ -> pdn L4 circuit (BPQ relays a line; pdn's echo console replies over the circuit)");
            output.WriteLine("*** L4 circuit BPQ -> pdn ESTABLISHED + data round-tripped over AXUDP ***");
        }
        finally
        {
            await broadcastCts.CancelAsync();
            try { await broadcaster; } catch (OperationCanceledException) { /* expected */ }
        }
    }

    // packet.net#931: the app aliases this test advertises. Both point at SSIDs of our call,
    // reached through us; only the first has an app attached.
    private const string AppAlias = "PNAPP";
    private static readonly Callsign AppCall = new("PNL4BX", 7);
    private const string StoppedAppAlias = "PNOFF";
    private static readonly Callsign StoppedAppCall = new("PNL4BX", 8);

    /// <summary>
    /// packet.net#931: a LinBPQ user does <c>C &lt;app alias&gt;</c> and lands in the app pdn
    /// advertises the alias for, not at pdn's node prompt; and the alias of an app that is not
    /// running answers as BPQ itself does for an unavailable application (a choked Connect
    /// Acknowledge, which BPQ shows its user as "Busy from").
    /// </summary>
    [SkippableFact]
    public async Task A_linbpq_user_connecting_to_an_advertised_app_alias_lands_in_the_app()
    {
        Skip.IfNot(await IsTcpPortReachable(Host, BpqHttpPort),
            $"LinBPQ not reachable (HTTP {Host}:{BpqHttpPort}). Bring up the interop stack: 'docker compose -f docker/compose.interop.yml up -d --wait'.");

        using var cts = new CancellationTokenSource(
            HearBpqBudget + BpqLearnsUsBudget + InboundCircuitBudget * 2 + TimeSpan.FromSeconds(60));

        await using var modem = new AxudpFrameTransport(new IPEndPoint(IPAddress.Loopback, BpqAxudpPort), PdnLocalPort);
        await using var listener = new Ax25Listener(modem, new Ax25ListenerOptions { MyCall = OurCall });
        await using var netRom = new NetRomService(new NetRomConfig
        {
            Enabled = true,
            Broadcast = true,
            Connect = true,
            TransportTimeoutSeconds = 4,
            TransportRetries = 5,
        }, nodeAlias: OurAlias);

        netRom.AppAdvertSource = () =>
        [
            new NodesBroadcastBuilder.Entry(AppCall, AppAlias, OurCall, Quality: 255),
            new NodesBroadcastBuilder.Entry(StoppedAppCall, StoppedAppAlias, OurCall, Quality: 255),
        ];

        // A circuit that reaches the node prompt instead of the app is the bug this guards.
        var landedAtConsole = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        netRom.RunInboundConsole = (conn, _) =>
        {
            landedAtConsole.TrySetResult();
            return conn.DisposeAsync().AsTask();
        };

        var appSeen = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var appEchoed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        netRom.ResolveInboundApp = call => !call.Equals(AppCall) ? null : async conn =>
        {
            output.WriteLine($"app circuit accepted from {conn.PeerId}");
            appSeen.TrySetResult(conn.PeerId);
            await conn.WriteAsync(Encoding.ASCII.GetBytes("pdn-app\r"));
            _ = Task.Run(async () =>
            {
                while (true)
                {
                    var data = await conn.ReadAsync();
                    if (data.IsEmpty)
                    {
                        break;
                    }
                    var text = Encoding.ASCII.GetString(data.Span);
                    output.WriteLine($"app circuit RX: {text.Replace("\r", "\\r")}");
                    await conn.WriteAsync(Encoding.ASCII.GetBytes("app-ack:" + text));
                    appEchoed.TrySetResult();
                }
            });
        };

        netRom.AttachPort("axudp", OurCall, listener, nodesBroadcast: true);
        await listener.StartAsync(cts.Token);

        using var broadcastCts = CancellationTokenSource.CreateLinkedTokenSource(cts.Token);
        var broadcaster = RunBroadcastLoop(netRom, broadcastCts.Token);

        try
        {
            (await ProvokeAndHearBpqAsync(netRom, cts.Token)).Should().BeTrue(
                "pdn must hear LinBPQ's NODES so it has a route back to BPQ for its replies");
            (await WaitForBpqToLearnAsync(AppAlias, cts.Token)).Should().BeTrue(
                "LinBPQ learns the app alias from pdn's NODES");

            // ── C <app alias> lands in the app ──────────────────────────────────
            var reached = await DriveBpqConnectAsync(AppAlias, "hello-from-bpq\r",
                () => appSeen.Task.IsCompleted, () => appEchoed.Task.IsCompleted, _ => false, cts.Token);
            reached.Should().BeTrue("a LinBPQ user's C to the app alias reaches the app and data round-trips");
            landedAtConsole.Task.IsCompleted.Should().BeFalse("the circuit is for the app, not pdn's node prompt");
            netRom.Circuits!.Circuits.Should().Contain(c => c.LocalNode.Equals(AppCall),
                "pdn's end of the circuit identifies as the app's callsign");
            output.WriteLine("*** BPQ C <app alias> landed in the app ***");

            // ── C <stopped app alias> is refused as BPQ refuses ─────────────────
            (await WaitForBpqToLearnAsync(StoppedAppAlias, cts.Token)).Should().BeTrue(
                "LinBPQ learns the stopped app's alias too");
            var refused = await DriveBpqConnectAsync(StoppedAppAlias, "",
                () => false, () => false, text => text.Contains("Busy from", StringComparison.OrdinalIgnoreCase), cts.Token);
            refused.Should().BeTrue("the alias of an app that is not running answers with a refusal BPQ reports as Busy");
            landedAtConsole.Task.IsCompleted.Should().BeFalse("a refused app circuit never falls back to the node prompt");
            output.WriteLine("*** BPQ C <stopped app alias> refused (Busy) ***");
        }
        finally
        {
            await broadcastCts.CancelAsync();
            try { await broadcaster; } catch (OperationCanceledException) { /* expected */ }
        }
    }

    // Wait for LinBPQ's NODES table to list `alias`.
    /// <summary>
    /// packet-net/packet.net#932 against real LinBPQ: an RHP <c>open</c> with no port, through
    /// the node's real gateway, supervisor and NET/ROM service, resolves BPQ's alias
    /// <c>PNTST</c> in the routing table and opens an L4 circuit to BPQ's node prompt, which
    /// answers a command over it. Then a portless open to a destination nobody advertises is
    /// <c>errCode 15</c>.
    /// </summary>
    [SkippableFact]
    public async Task A_portless_RHP_open_reaches_linbpq_over_NET_ROM_by_its_alias()
    {
        Skip.IfNot(await IsTcpPortReachable(Host, BpqHttpPort),
            $"LinBPQ not reachable (HTTP {Host}:{BpqHttpPort}). Bring up the interop stack: 'docker compose -f docker/compose.interop.yml up -d --wait'.");

        using var cts = new CancellationTokenSource(
            HearBpqBudget + OutboundConnectBudget + DataRoundTripBudget + TimeSpan.FromSeconds(60));

        var config = new NodeConfig
        {
            Identity = new Identity { Callsign = RhpNodeCall.ToString(), Alias = "PNRHP" },
            NetRom = new NetRomConfig
            {
                Enabled = true,
                Broadcast = true,
                Connect = true,
                TransportTimeoutSeconds = 4,
                TransportRetries = 5,
            },
            Ports =
            [
                new PortConfig
                {
                    Id = "axudp",
                    Enabled = true,
                    Transport = new AxudpTransport { Host = Host, Port = BpqAxudpPort, LocalPort = RhpNodeLocalPort },
                    NodesBroadcast = true,
                },
            ],
            Management = new ManagementConfig { Telnet = new TelnetConfig { Enabled = false } },
        };
        var configProvider = new FixedConfig(config);
        using var host = new NodeHostedService(configProvider, TransportFactory.Instance, TimeProvider.System, NullLoggerFactory.Instance);
        await host.StartAsync(cts.Token);
        try
        {
            await WaitUntil(() => host.Supervisor?.RunningPortIds.Contains("axudp") == true && host.NetRom is not null,
                TimeSpan.FromSeconds(15), cts.Token);
            var netRom = host.NetRom!;

            using var broadcastCts = CancellationTokenSource.CreateLinkedTokenSource(cts.Token);
            var broadcaster = RunBroadcastLoop(netRom, broadcastCts.Token);
            try
            {
                (await ProvokeAndHearBpqAsync(netRom, cts.Token)).Should().BeTrue(
                    "pdn must hear LinBPQ's NODES so it has a NET/ROM route to PNTST");
                await WaitUntil(() => netRom.Snapshot().ResolveDestination("PNTST") is not null,
                    HearBpqBudget, cts.Token);

                var gateway = new SupervisorRhpGateway(host, configProvider);
                using (var connectCts = CancellationTokenSource.CreateLinkedTokenSource(cts.Token))
                {
                    connectCts.CancelAfter(OutboundConnectBudget);
                    await using var conn = await gateway.OpenAx25StreamAsync(
                        portLabel: null, local: RhpAppCall.ToString(), remote: "PNTST", connectCts.Token);

                    conn.TransportKind.Should().Be(NodeTransportKind.NetRom,
                        "a portless open to a NET/ROM alias is an L4 circuit, not an AX.25 dial");
                    await conn.WriteAsync(Encoding.ASCII.GetBytes("\r"), cts.Token);
                    await conn.WriteAsync(Encoding.ASCII.GetBytes("PORTS\r"), cts.Token);
                    var reply = await ReadCircuitAccumulateAsync(conn, "PN0TST", DataRoundTripBudget, cts.Token);
                    reply.Should().Contain("PN0TST", "BPQ's node prompt answers a command over the circuit");
                    output.WriteLine("*** portless RHP open reached LinBPQ over NET/ROM ***");
                }

                var noRoute = () => gateway.OpenAx25StreamAsync(portLabel: null, local: RhpAppCall.ToString(), remote: "NOWHRE", cts.Token);
                (await noRoute.Should().ThrowAsync<RhpGatewayException>()).Which
                    .ErrCode.Should().Be(RhpErrorCode.NoRoute);
            }
            finally
            {
                await broadcastCts.CancelAsync();
                await broadcaster;
            }
        }
        finally
        {
            await host.StopAsync(CancellationToken.None);
        }
    }

    // Its own callsign and port (bpq32.cfg MAP PNRHPX-1 ... 8193 B), apart from PNL4BX-1, so
    // a link or route left from one test cannot perturb the other.
    private static readonly Callsign RhpNodeCall = new("PNRHPX", 1);
    private static readonly Callsign RhpAppCall = new("PNRHPX", 3);
    private const int RhpNodeLocalPort = 8193;

    private sealed class FixedConfig(NodeConfig config) : IConfigProvider
    {
        public NodeConfig Current => config;
        public IDisposable OnChange(Action<NodeConfig> listener) => Noop.Instance;
        private sealed class Noop : IDisposable
        {
            public static readonly Noop Instance = new();
            public void Dispose() { }
        }
    }

    private async Task<bool> WaitForBpqToLearnAsync(string alias, CancellationToken outer)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(outer);
        cts.CancelAfter(BpqLearnsUsBudget);
        while (!cts.IsCancellationRequested)
        {
            try
            {
                var (nodes, _) = await BpqTelnet.QueryNodesAndRoutesAsync(Host, BpqTelnetPort, BpqPasswordText, output, cts.Token);
                if (nodes.Contains(alias, StringComparison.OrdinalIgnoreCase))
                {
                    output.WriteLine($"BPQ learned {alias}. NODES=[{Collapse(nodes)}]");
                    return true;
                }
            }
            catch (OperationCanceledException) when (cts.IsCancellationRequested) { break; }
            catch (Exception ex) { output.WriteLine($"BPQ query failed (will retry): {ex.Message}"); }
            try { await Task.Delay(2000, cts.Token); } catch (OperationCanceledException) { break; }
        }
        return false;
    }

    // Drive BPQ's `C <alias>` with bounded retry until `done` (data round-tripped) or BPQ's
    // reply text satisfies `replied`.
    private async Task<bool> DriveBpqConnectAsync(
        string alias, string payload, Func<bool> established, Func<bool> done, Func<string, bool> replied,
        CancellationToken outer)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(outer);
        cts.CancelAfter(InboundCircuitBudget);
        while (!cts.IsCancellationRequested)
        {
            try
            {
                var text = await BpqTelnet.ConnectAndSendAsync(
                    Host, BpqTelnetPort, BpqPasswordText, alias, payload, TimeSpan.FromSeconds(20),
                    established, done, output, cts.Token, replied);
                if (done() || replied(text))
                {
                    return true;
                }
            }
            catch (OperationCanceledException) when (cts.IsCancellationRequested) { break; }
            catch (Exception ex) { output.WriteLine($"BPQ C {alias} attempt failed (will retry): {ex.Message}"); }
            try { await Task.Delay(2000, cts.Token); } catch (OperationCanceledException) { break; }
        }
        return done();
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
                if (routes.Contains(OurCall.Base, StringComparison.OrdinalIgnoreCase) ||
                    nodes.Contains(OurCall.Base, StringComparison.OrdinalIgnoreCase))
                {
                    output.WriteLine($"BPQ learned us. NODES=[{Collapse(nodes)}] ROUTES=[{Collapse(routes)}]");
                    return true;
                }
            }
            catch (OperationCanceledException) when (cts.IsCancellationRequested) { break; }
            catch (Exception ex) { output.WriteLine($"BPQ query failed (will retry): {ex.Message}"); }
            try { await Task.Delay(2000, cts.Token); } catch (OperationCanceledException) { break; }
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
                await BpqTelnet.ConnectAndSendAsync(
                    Host, BpqTelnetPort, BpqPasswordText, OurAlias, "hello-from-bpq\r",
                    TimeSpan.FromSeconds(20),
                    () => inboundSeen.Task.IsCompleted, () => inboundEchoed.Task.IsCompleted,
                    output, cts.Token);
            }
            catch (OperationCanceledException) when (cts.IsCancellationRequested) { break; }
            catch (Exception ex) { output.WriteLine($"BPQ C {OurAlias} attempt failed (will retry): {ex.Message}"); }

            if (inboundSeen.Task.IsCompleted && inboundEchoed.Task.IsCompleted)
            {
                return true;
            }

            try { await Task.Delay(2000, cts.Token); } catch (OperationCanceledException) { break; }
        }
        return inboundSeen.Task.IsCompleted;
    }

    // ── helpers ──────────────────────────────────────────────────────────────
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
                if (sb.ToString().Contains(needle, StringComparison.Ordinal))
                {
                    break;
                }
            }
            else if (sb.Length > 0)
            {
                break;   // EOF on the circuit (closed)
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
            if (condition())
            {
                return;
            }

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

    /// <summary>
    /// IAC-aware telnet driver for LinBPQ's node prompt: the source-verified
    /// positional-challenge sysop auth, plus the operations this test needs
    /// (SENDNODES, ROUTES/NODES query, and an outbound <c>C &lt;alias&gt;</c> +
    /// data-relay). The same driver shape the removed net-sim
    /// <c>NetRomL4CircuitViaNetsim</c> used.
    /// </summary>
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

        public static async Task<string> ConnectAndSendAsync(
            string host, int port, string passwordText, string alias, string payload, TimeSpan hold,
            Func<bool> established, Func<bool> echoed, ITestOutputHelper output, CancellationToken ct,
            Func<string, bool>? replied = null)
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
            while (!cts.IsCancellationRequested && !echoed() && !(replied?.Invoke(acc.ToString()) ?? false))
            {
                if (!payloadSent && payload.Length > 0 && established() &&
                    acc.ToString().Contains("Connected", StringComparison.OrdinalIgnoreCase))
                {
                    payloadSent = true;
                    await Task.Delay(500, cts.Token);
                    await SendLineRawAsync(stream, payload, ct);
                    output.WriteLine($"BPQ relayed payload over circuit: {payload.Replace("\r", "\\r")}");
                }

                var readCts = CancellationTokenSource.CreateLinkedTokenSource(cts.Token);
                readCts.CancelAfter(TimeSpan.FromSeconds(2));
                int n;
                try { n = await stream.ReadAsync(buf, readCts.Token); }
                catch (OperationCanceledException) when (!cts.IsCancellationRequested) { continue; }
                catch (OperationCanceledException) { break; }
                finally { readCts.Dispose(); }
                if (n == 0)
                {
                    break;
                }

                var sb = new StringBuilder();
                AppendStripIac(stream, sb, buf, n);
                acc.Append(sb);
            }
            var text = acc.ToString();
            if (text.Length > 0)
            {
                output.WriteLine($"BPQ C {alias}: {Collapse(text)}");
            }
            return text;
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
                if (n == 0)
                {
                    break;
                }

                AppendStripIac(stream, sb, buf, n);
                if (needle.Length > 0 && sb.ToString().Contains(needle, StringComparison.Ordinal))
                {
                    break;
                }
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
                if (n == 0)
                {
                    break;
                }

                AppendStripIac(stream, sb, buf, n);
                if (sb.ToString().Contains('\n', StringComparison.Ordinal))
                {
                    break;
                }
            }
            return sb.ToString();
        }

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
                if (n == 0)
                {
                    break;
                }

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
                if (i + 2 >= n)
                {
                    break;
                }

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
