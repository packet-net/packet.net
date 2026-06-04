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
/// <see cref="TransportFactory"/> → <see cref="AxudpKissModem"/> → port supervisor
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

        // The connect itself proves the SABM/UA handshake crossed the AXUDP link
        // (ConnectAsync only returns on DL-CONNECT-confirm). Now prove an I-frame
        // round-trip both ways: send a command and read node B's reply back over the
        // tunnel. (We drive the command rather than waiting on node B's eager banner
        // because the connecting side subscribes to its session only once ConnectAsync
        // returns — a documented slice-1 ordering nuance in Ax25NodeConnection — so a
        // banner sent before that subscribe can be missed; a reply to OUR command is
        // strictly after we subscribed and is race-free.)
        await toB.WriteAsync(Encoding.ASCII.GetBytes("I\r"), cts.Token);
        var infoReply = await ReadUntilAsync(toB, "NODEB-1", cts.Token);
        infoReply.Should().Contain("Software: Packet.NET",
            "node B's Info reply (carried in an I-frame over AXUDP) reached node A");
        infoReply.Should().Contain("NODEB-1",
            "the Info reply names node B's callsign — proving a full request/response I-frame round-trip over AXUDP");
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
                    // FCS-on both ends — the new interoperable default (this is the same
                    // wire form pdn uses against LinBPQ/XRouter/ax25ipd). The receiver
                    // strips+validates the FCS, so the listener parses a clean frame and
                    // RR/RNR acks survive (an unstripped FCS tail would drop every S-frame).
                    IncludeFcs = true,
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
