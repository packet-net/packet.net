using System.Net;
using System.Net.Sockets;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Packet.Core;
using Packet.Node.Core.Configuration;
using Packet.Node.Core.Console;
using Packet.Node.Core.Hosting;
using Packet.Node.Core.Transports;
using Packet.Node.Tests.Support;

namespace Packet.Node.Tests.Integration;

/// <summary>
/// The marquee AXUDP test: <b>two real pdn node-host instances connect to each
/// other over AXUDP</b> on the loopback interface — a full AX.25 connected-mode
/// session (SABM/UA + I-frames carrying the node banner) carried over real UDP
/// datagrams, end-to-end through the node-host machinery (the real
/// <see cref="TransportFactory"/> → <see cref="AxudpFrameTransport"/> → port supervisor
/// → console). This is the "AXUDP connectivity to another node over a standard
/// mechanism" capability: the C# node is the AXUDP peer a remote node (e.g. the
/// Rust Pico-W) can dial.
/// </summary>
[Trait("Category", "Node")]
public sealed class AxudpNodeToNodeIntegrationTests
{
    private static readonly Callsign NodeACall = new("NODEA", 1);
    private static readonly Callsign NodeBCall = new("NODEB", 1);

    [Fact]
    public async Task Two_pdn_nodes_connect_over_AXUDP_and_a_banner_crosses_the_link()
    {
        // Two free UDP ports, then cross-address the tunnel: A receives on portA and
        // sends to portB; B receives on portB and sends to portA.
        var (portA, portB) = (FreeUdpPort(), FreeUdpPort());

        var configA = NodeConfig(NodeACall, "NODE-A", localPort: portA, remotePort: portB);
        var configB = NodeConfig(NodeBCall, "NODE-B", localPort: portB, remotePort: portA);

        // Real transport factory — this exercises the actual AXUDP wiring, not a fake.
        await using var nodeA = new PortSupervisor(
            new TestConfigProvider(configA), TransportFactory.Instance, TimeProvider.System, NullLoggerFactory.Instance);
        await using var nodeB = new PortSupervisor(
            new TestConfigProvider(configB), TransportFactory.Instance, TimeProvider.System, NullLoggerFactory.Instance);

        await nodeA.StartAsync();
        await nodeB.StartAsync();
        await Wait.ForAsync(() => nodeA.RunningPortIds.Contains("axudp") && nodeB.RunningPortIds.Contains("axudp"),
            "both AXUDP ports should come up");

        // Node A dials node B through its own node-host connector (the same path the
        // telnet/console Connect command uses) — so node A won't start a console
        // against B, and we can read B's banner off the returned connection.
        var connector = nodeA.ResolveDefaultConnector();
        connector.Should().NotBeNull("node A has a running AXUDP port to dial out on");

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await using var toB = await connector!.ConnectAsync(NodeBCall, cts.Token);

        // Monitor-v2 (#173): with the session up (ConnectAsync returns on
        // DL-CONNECT-confirm), the link's live timer state is surfaced from the connected
        // session — node A has a real, positive SRT estimate for NODEB-1. This is the value
        // that feeds the REST /links projection + the MCP link_quality tool (previously
        // hard-coded 0); the retry count (RC) is read from the same place. (RC's exact
        // value just after the handshake is timing-dependent, so it isn't asserted here.)
        var (rttMs, _) = Packet.Node.Api.PdnReadApi.SessionTimers(nodeA, "axudp", "NODEB-1");
        rttMs.Should().BeGreaterThan(0, "a connected session has a smoothed round-trip estimate (SRT)");

        // The connect itself proves the SABM/UA handshake crossed the AXUDP link
        // (ConnectAsync only returns on DL-CONNECT-confirm). Now prove a request/response
        // I-frame round-trip: send a command and read node B's reply back over the tunnel.
        // (Node B's eager connect banner is asserted separately by the unsolicited-banner
        // test below — Ax25NodeConnection now replays pre-subscribe data, so the banner is
        // no longer lost to the connect/subscribe window.)
        await toB.WriteAsync(Encoding.ASCII.GetBytes("I\r"), cts.Token);
        // Read through to the Info reply itself. The needle must be a token unique to the
        // Info reply ("Software:"), NOT "NODEB-1" — node B's eager connect banner
        // ("Welcome to NODE-B (NODEB-1)") also contains the callsign, so a callsign needle
        // can return on the banner before the Info reply arrives (a flake).
        var infoReply = await ReadUntilAsync(toB, "Software:", cts.Token);
        infoReply.Should().Contain("Software: Packet.NET",
            "node B's Info reply (carried in an I-frame over AXUDP) reached node A");
        infoReply.Should().Contain("NODEB-1",
            "the Info reply names node B's callsign — proving a full request/response I-frame round-trip over AXUDP");
    }

    [Fact]
    public async Task The_dialer_receives_node_Bs_eager_connect_banner_without_sending_first()
    {
        // The regression this guards: an outbound connect (the path an RHP `open` to a node's
        // own callsign, or the console `C <node>`, takes) wraps an ALREADY-connected session in
        // Ax25NodeConnection. Node B emits its console banner the instant it accepts — it does
        // not wait — so the banner lands in the window between connect and subscribe. Before the
        // fix that banner was dropped (open succeeded, Connected, then nothing — the caller
        // stalls); now Ax25NodeConnection replays pre-subscribe inbound data, so reading WITHOUT
        // sending anything first yields B's banner.
        var (portA, portB) = (FreeUdpPort(), FreeUdpPort());
        await using var nodeA = new PortSupervisor(
            new TestConfigProvider(NodeConfig(NodeACall, "NODE-A", localPort: portA, remotePort: portB)),
            TransportFactory.Instance, TimeProvider.System, NullLoggerFactory.Instance);
        await using var nodeB = new PortSupervisor(
            new TestConfigProvider(NodeConfig(NodeBCall, "NODE-B", localPort: portB, remotePort: portA)),
            TransportFactory.Instance, TimeProvider.System, NullLoggerFactory.Instance);

        await nodeA.StartAsync();
        await nodeB.StartAsync();
        await Wait.ForAsync(() => nodeA.RunningPortIds.Contains("axudp") && nodeB.RunningPortIds.Contains("axudp"),
            "both AXUDP ports should come up");

        var connector = nodeA.ResolveDefaultConnector();
        connector.Should().NotBeNull();

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await using var toB = await connector!.ConnectAsync(NodeBCall, cts.Token);

        // No write — read straight through to the banner. If the eager banner were still being
        // dropped, this would hang until the 20 s budget and the assertion would fail empty.
        var banner = await ReadUntilAsync(toB, "NODEB-1", cts.Token);
        banner.Should().Contain("NODEB-1",
            "node B's unsolicited connect banner reaches the dialer (replayed past the connect/subscribe window)");
    }

    [Fact]
    public async Task A_redial_to_the_same_node_still_replays_the_eager_banner()
    {
        // packet.net#659, the two-node case the first-connect banner test never saw: the
        // Ax25Listener caches and REUSES its Ax25Session per (local, remote) across
        // connect/disconnect cycles, and the early-inbound replay buffer used to be a
        // one-shot per session object — disarmed forever by the FIRST connection's consumer.
        // So on a RE-DIAL to a peer already connected once, node B's eager banner was
        // L2-ACKed but never buffered and never replayed: the caller connected, then got
        // zero bytes. Live two-node testing connects/disconnects/reconnects repeatedly, which
        // is why the real rig hit this and the single-connect harness did not.
        var (portA, portB) = (FreeUdpPort(), FreeUdpPort());
        await using var nodeA = new PortSupervisor(
            new TestConfigProvider(NodeConfig(NodeACall, "NODE-A", localPort: portA, remotePort: portB)),
            TransportFactory.Instance, TimeProvider.System, NullLoggerFactory.Instance);
        await using var nodeB = new PortSupervisor(
            new TestConfigProvider(NodeConfig(NodeBCall, "NODE-B", localPort: portB, remotePort: portA)),
            TransportFactory.Instance, TimeProvider.System, NullLoggerFactory.Instance);

        await nodeA.StartAsync();
        await nodeB.StartAsync();
        await Wait.ForAsync(() => nodeA.RunningPortIds.Contains("axudp") && nodeB.RunningPortIds.Contains("axudp"),
            "both AXUDP ports should come up");

        var connector = nodeA.ResolveDefaultConnector();
        connector.Should().NotBeNull();

        // --- First connection: read the eager banner, then disconnect. This is the path that
        // arms + disarms the reused session's early-inbound buffer.
        using (var cts1 = new CancellationTokenSource(TimeSpan.FromSeconds(20)))
        {
            var firstDial = await connector!.ConnectAsync(NodeBCall, cts1.Token);
            var firstBanner = await ReadUntilAsync(firstDial, "NODEB-1", cts1.Token);
            firstBanner.Should().Contain("NODEB-1", "the first connect's eager banner reaches the dialer (baseline)");
            await firstDial.DisposeAsync();
        }

        // Let the link fully tear down on BOTH sides before re-dialling: node A's session must
        // leave AwaitingRelease so the re-dial starts a clean SABM, and node B's session must
        // reach Disconnected so its previous console loop ends and the reconnect SABM starts a
        // FRESH console (which re-emits the banner) rather than being deduped away.
        await Wait.ForAsync(() => PeerSessionInState(nodeA, NodeBCall, "Disconnected"),
            "node A's outbound session settles to Disconnected after the first close");
        await Wait.ForAsync(() => PeerSessionInState(nodeB, NodeACall, "Disconnected"),
            "node B's inbound session settles to Disconnected so its console loop ends");

        // --- Re-dial the SAME peer: both nodes reuse their cached Ax25Session. Read the banner
        // again WITHOUT sending anything first. Before the fix this hung to the 20 s budget and
        // returned empty (the reused session's buffer stayed disarmed); the re-dial's fresh
        // DL-CONNECT-confirm now re-arms it, so the banner is replayed exactly as on connect 1.
        using var cts2 = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await using var secondDial = await connector!.ConnectAsync(NodeBCall, cts2.Token);
        var secondBanner = await ReadUntilAsync(secondDial, "NODEB-1", cts2.Token);
        secondBanner.Should().Contain("NODEB-1",
            "the re-dial's eager banner is replayed too — the reused session re-arms its early-inbound buffer on reconnect (packet.net#659)");
    }

    // True when the listener on <paramref name="node"/>'s AXUDP port holds a session with
    // <paramref name="peer"/> in <paramref name="state"/> (SDL state name). Used to wait for a
    // clean teardown before a re-dial without an arbitrary sleep.
    private static bool PeerSessionInState(PortSupervisor node, Callsign peer, string state)
    {
        var sessions = node.GetPort("axudp")?.Listener.ActiveSessions;
        if (sessions is null)
        {
            return false;
        }
        foreach (var session in sessions)
        {
            if (session.Context.Remote.ToString() == peer.ToString() && session.CurrentState == state)
            {
                return true;
            }
        }
        return false;
    }

    private static NodeConfig NodeConfig(Callsign call, string alias, int localPort, int remotePort) => new()
    {
        Identity = new Identity { Callsign = call.ToString(), Alias = alias },
        Ports =
        [
            new PortConfig
            {
                Id = "axudp",
                Enabled = true,
                Transport = new AxudpTransport
                {
                    Host = "127.0.0.1",
                    Port = remotePort,
                    LocalPort = localPort,
                    // AXUDP unconditionally carries the FCS (the same wire form pdn uses
                    // against LinBPQ/XRouter/ax25ipd). The receiver strips + validates it,
                    // so the listener parses a clean frame and RR/RNR acks survive (an
                    // unstripped FCS tail would drop every S-frame).
                },
                // Small N2 bounds the node-to-node connect backstop at 30 s instead of
                // the 66 s spec default, so a starved handshake fails fast under CI
                // load (#47); T1 stays the spec default.
                Ax25 = new Ax25PortParams { N2 = TestAx25Timing.NodeN2 },
            },
        ],
        // Loopback telnet on an unused port (we don't use it, but keep config valid /
        // realistic); distinct per node so two instances don't clash.
        Management = new ManagementConfig { Telnet = new TelnetConfig { Enabled = false } },
    };

    // Accumulate bytes off the connection until the needle appears (or cancelled).
    private static async Task<string> ReadUntilAsync(INodeConnection conn, string needle, CancellationToken ct)
    {
        var sb = new StringBuilder();
        while (!ct.IsCancellationRequested)
        {
            var chunk = await conn.ReadAsync(ct);
            if (chunk.Length == 0)
            {
                // EOF — return what we have (the assertion will report the miss).
                break;
            }
            sb.Append(Encoding.ASCII.GetString(chunk.Span));
            if (sb.ToString().Contains(needle, StringComparison.Ordinal))
            {
                break;
            }
        }
        return sb.ToString();
    }

    // Grab a currently-free UDP port by binding to 0 and reading the assignment.
    // The probe socket is closed before we hand the port back; on loopback the OS
    // doesn't immediately reassign an ephemeral port, so the subsequent bind wins.
    private static int FreeUdpPort()
    {
        using var probe = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        probe.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        return ((IPEndPoint)probe.LocalEndPoint!).Port;
    }
}
