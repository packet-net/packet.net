using System.Text;
using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Packet.Ax25;
using Packet.Ax25.Session;
using Packet.Core;
using Packet.Node.Core.Configuration;
using Packet.Node.Core.Hosting;
using Packet.Node.Tests.Support;
using Xunit;

namespace Packet.Node.Tests.Integration;

/// <summary>
/// #885 through the node host: a console user who starts the link over (SABM) while the
/// console still has output they never acknowledged gets a fresh console on the restarted
/// link, not UA followed by DISC. The old console's connection ends on the reset signal and
/// hands the link to a new console; that hand-over must not be refused as a duplicate of the
/// console that is still winding down.
/// </summary>
[Trait("Category", "Node")]
public sealed class ConsoleRestartOnResetTests
{
    private static readonly Callsign NodeCall = new("N0BBB", 1);
    private static readonly Callsign Station = new("N0AAA", 3);

    [Fact]
    public async Task A_caller_who_restarts_the_link_with_the_banner_unacknowledged_gets_a_fresh_console_not_DISC()
    {
        var bus = new SharedRadioBus();
        var nodeSent = new List<Ax25Frame>();
        int uasFromNode = 0;
        // The node's I frames are lost on the air until it has answered the station's second
        // SABM, so the first console's banner is never acknowledged and the reset discards it.
        // Decided on the node's own send path, in order, so the fresh console's banner (sent
        // after that second UA) is the first I frame the station hears.
        var nodeWire = bus.Attach(loseOutbound: bytes =>
        {
            if (!Ax25Frame.TryParse(bytes.Span, Ax25ParseOptions.Lenient, out var f))
            {
                return false;
            }

            lock (nodeSent)
            {
                nodeSent.Add(f);
                if (f.FrameType == Ax25FrameType.Ua)
                {
                    uasFromNode++;
                }

                return f.FrameType == Ax25FrameType.I && uasFromNode < 2;
            }
        });
        var config = new TestConfigProvider(new NodeConfig
        {
            Identity = new Identity { Callsign = NodeCall.ToString(), Alias = "TESTNODE" },
            Ports = [new PortConfig { Id = "p1", Enabled = true, Transport = new KissTcpTransport { Host = "mem", Port = 1 } }],
        });
        var factory = new FakeTransportFactory().Provide("kiss-tcp:mem:1", nodeWire);
        using var host = new NodeHostedService(config, factory, TimeProvider.System, NullLoggerFactory.Instance);
        await host.StartAsync(CancellationToken.None);
        await Wait.ForAsync(() => host.Supervisor?.RunningPortIds.Contains("p1") == true, "port p1 comes up");

        await using var station = new Ax25Listener(bus.Attach(), new Ax25ListenerOptions { MyCall = Station }, TimeProvider.System);
        await station.StartAsync();
        var link = await station.ConnectAsync(NodeCall, Station, extended: false, preConnectXidNegotiatesSrej: false)
            .WaitAsync(TimeSpan.FromSeconds(10));
        var heard = new StringBuilder();
        link.DataLinkSignalEmitted += (_, s) =>
        {
            if (s is DataLinkDataIndication d)
            {
                lock (heard) { heard.Append(Encoding.ASCII.GetString(d.Info.Span)); }
            }
        };

        bool NodeSent(Ax25FrameType type)
        {
            lock (nodeSent) { return nodeSent.Any(f => f.FrameType == type); }
        }

        await Wait.ForAsync(() => NodeSent(Ax25FrameType.I), "the console's banner is on the air, and lost", TimeSpan.FromSeconds(10));

        // The station sends a line the node acknowledges. A second SABM straight after the first
        // would be re-acknowledged as a repeat of it (Ax25Spec50RepeatedConnectSabmReacknowledged,
        // the link kept); a frame from the station in between closes that window, so the SABM
        // below is a restart of a link that has carried traffic.
        link.PostEvent(new DlDataRequest(Encoding.ASCII.GetBytes("i\r")));
        await Wait.ForAsync(() =>
        {
            lock (nodeSent) { return nodeSent.Any(f => f.FrameType is Ax25FrameType.I or Ax25FrameType.Rr && f.Nr == 1); }
        }, "the node acknowledged the station's line", TimeSpan.FromSeconds(10));

        // The station starts the link over: SABM on the up link. The node answers UA, the reset
        // discards the banner, the console ends on the reset and hands the link to a fresh one.
        link.PostEvent(new DlConnectRequest());

        await Wait.ForAsync(() =>
        {
            lock (heard) { return heard.ToString().Contains("Welcome", StringComparison.Ordinal); }
        }, "the fresh console greets the station on the restarted link", TimeSpan.FromSeconds(15));
        NodeSent(Ax25FrameType.Disc).Should().BeFalse("the restarted link belongs to the new console; nobody DISCs it");
        link.CurrentState.Should().Be("Connected");

        await host.StopAsync(CancellationToken.None);
    }
}
