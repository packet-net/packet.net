using Microsoft.Extensions.Logging.Abstractions;
using Packet.Ax25.Session;
using Packet.Core;
using Packet.Node.Core.Configuration;
using Packet.Node.Core.Hosting;
using Packet.Node.Tests.Support;

namespace Packet.Node.Tests.Integration;

/// <summary>
/// End-to-end over an in-memory radio bus: a node (a real
/// <see cref="PortSupervisor"/> with one in-memory AX.25 port) accepts an
/// inbound connect, drives the console, and relays a connect-OUT to a third
/// station - exit criteria (ii) and (iv). All three stations are real
/// <c>Ax25Listener</c>s on a shared broadcast bus (the <c>TwoStationHarness</c>
/// shape, extended to three).
/// </summary>
[Trait("Category", "Node")]
public sealed class Ax25ConsoleIntegrationTests
{
    private static readonly Callsign NodeCall = new("NODE", 1);
    private static readonly Callsign RemoteCall = new("REMOTE", 1);
    private static readonly Callsign ThirdCall = new("THIRD", 1);

    private static NodeConfig NodeConfig(int memPort = 1) => new()
    {
        Identity = new Identity { Callsign = NodeCall.ToString(), Alias = "TESTNODE" },
        Ports =
        [
            new PortConfig
            {
                Id = "p1",
                Enabled = true,
                Transport = new KissTcpTransport { Host = "mem", Port = memPort },
                // Small N2 bounds the node's own connect-OUT (the relay test) at 30 s
                // instead of the 66 s spec default under CI load; T1 stays spec
                // default so the banner-count test sees no retransmit (#47).
                Ax25 = new Ax25PortParams { N2 = TestAx25Timing.NodeN2 },
            },
        ],
    };

    private static string Endpoint(int memPort = 1) => $"kiss-tcp:mem:{memPort}";

    [Fact]
    public async Task Inbound_connect_reaches_the_prompt_and_Info_Nodes_Help_Bye_work()
    {
        var bus = new SharedRadioBus();
        var nodeModem = bus.Attach();

        var config = new TestConfigProvider(NodeConfig());
        var factory = new FakeTransportFactory().Provide(Endpoint(), nodeModem);
        await using var supervisor = new PortSupervisor(config, factory, TimeProvider.System, NullLoggerFactory.Instance);
        await supervisor.StartAsync();
        await Wait.ForAsync(() => supervisor.RunningPortIds.Contains("p1"), "port p1 should come up");

        await using var remote = new RemoteStation(bus.Attach(), RemoteCall);
        await remote.StartAsync();
        await remote.ConnectAsync(NodeCall);

        // Banner on connect.
        await Wait.ForAsync(() => remote.Saw("TESTNODE"), "banner should arrive on connect");
        remote.Saw("Packet.NET").Should().BeTrue("the banner carries the version");

        // Info.
        remote.SendLine("I");
        await Wait.ForAsync(() => remote.Saw("Software: Packet.NET"), "Info should reply with the version");
        remote.Saw("NODE-1").Should().BeTrue();

        // Nodes - the node identity (+ NET/ROM table); ports moved to their own command.
        remote.SendLine("N");
        await Wait.ForAsync(() => remote.Saw("Node "), "Nodes should name the node");

        // Ports - lists the configured port with its 1-indexed CONNECT port number.
        remote.SendLine("PORTS");
        await Wait.ForAsync(() => remote.Saw("Ports:"), "Ports should list ports");
        remote.Saw("1  p1").Should().BeTrue("Ports shows the 1-indexed CONNECT port number next to the id");

        // Help.
        remote.SendLine("H");
        await Wait.ForAsync(() => remote.Saw("Commands:"), "Help should list commands");

        // Bye - node says 73 and disconnects.
        remote.SendLine("B");
        await Wait.ForAsync(() => remote.Saw("73"), "Bye should be acknowledged");
        await Wait.ForAsync(() => remote.CurrentState == "Disconnected", "the link should drop after Bye");
    }

    [Fact]
    public async Task Banner_and_first_prompt_go_out_as_a_single_I_frame()
    {
        // #292: on a slow half-duplex channel the node bursting banner THEN prompt
        // as two back-to-back I-frames doubles its air occupancy at the moment the
        // freshly-connected peer wants to send its first command. They must leave
        // the node as ONE I-frame.
        var bus = new SharedRadioBus();
        var nodeModem = bus.Attach();

        var config = new TestConfigProvider(NodeConfig());
        var factory = new FakeTransportFactory().Provide(Endpoint(), nodeModem);
        await using var supervisor = new PortSupervisor(config, factory, TimeProvider.System, NullLoggerFactory.Instance);
        await supervisor.StartAsync();
        await Wait.ForAsync(() => supervisor.RunningPortIds.Contains("p1"), "port p1 should come up");

        // Count the I-frames the node transmits (banner/prompt are I-frames; the UA
        // and any RR poll are not).
        var nodeIFramesTx = 0;
        var listener = supervisor.GetPort("p1")!.Listener;
        listener.FrameTraced += (_, e) =>
        {
            if (e.Direction == FrameDirection.Transmitted &&
                Ax25FrameClassifier.Classify(e.Frame) is IFrameReceived)
            {
                Interlocked.Increment(ref nodeIFramesTx);
            }
        };

        await using var remote = new RemoteStation(bus.Attach(), RemoteCall);
        await remote.StartAsync();
        await remote.ConnectAsync(NodeCall);

        // Banner + prompt arrive (the prompt is "NODE-1> ", expanded from "{call}> ").
        await Wait.ForAsync(() => remote.Saw("TESTNODE") && remote.Saw("NODE-1>"),
            "both banner and prompt should arrive on connect");

        // Settle briefly to be sure no second I-frame is in flight, then assert the
        // node sent exactly one I-frame for the whole banner+prompt.
        await Task.Delay(100);
        nodeIFramesTx.Should().Be(1,
            "the banner and the first prompt must be combined into a single I-frame (#292)");
    }

    [Fact]
    public async Task Unknown_command_re_prompts_without_disconnecting()
    {
        var bus = new SharedRadioBus();
        var config = new TestConfigProvider(NodeConfig());
        var factory = new FakeTransportFactory().Provide(Endpoint(), bus.Attach());
        await using var supervisor = new PortSupervisor(config, factory, TimeProvider.System, NullLoggerFactory.Instance);
        await supervisor.StartAsync();
        await Wait.ForAsync(() => supervisor.RunningPortIds.Contains("p1"), "port up");

        await using var remote = new RemoteStation(bus.Attach(), RemoteCall);
        await remote.StartAsync();
        await remote.ConnectAsync(NodeCall);
        await Wait.ForAsync(() => remote.Saw("TESTNODE"), "banner");

        remote.SendLine("FLOOByGADGET");
        await Wait.ForAsync(() => remote.Saw("Unknown command"), "unknown command is reported");
        remote.CurrentState.Should().Be("Connected", "an unknown command must not disconnect the user");
    }

    [Fact]
    public async Task Connect_out_relays_both_ways_to_a_third_station()
    {
        var bus = new SharedRadioBus();

        // Node.
        var config = new TestConfigProvider(NodeConfig());
        var factory = new FakeTransportFactory().Provide(Endpoint(), bus.Attach());
        await using var supervisor = new PortSupervisor(config, factory, TimeProvider.System, NullLoggerFactory.Instance);
        await supervisor.StartAsync();
        await Wait.ForAsync(() => supervisor.RunningPortIds.Contains("p1"), "port up");

        // Third station: a bare listener that accepts inbound and echoes a line so
        // we can prove both-way relay.
        await using var third = new EchoStation(bus.Attach(), ThirdCall, reply: "HELLO FROM THIRD\r");
        await third.StartAsync();

        // Remote dials into the node, then asks the node to connect out to THIRD.
        await using var remote = new RemoteStation(bus.Attach(), RemoteCall);
        await remote.StartAsync();
        await remote.ConnectAsync(NodeCall);
        await Wait.ForAsync(() => remote.Saw("TESTNODE"), "banner");

        remote.SendLine("C THIRD-1");
        await Wait.ForAsync(() => remote.Saw("Connected to THIRD-1"), "node reports the outbound connect");

        // Relay both ways: the remote's line reaches THIRD (which echoes), and the
        // echo relays back through the node to the remote.
        await Wait.ForAsync(() => third.SawConnect, "third station saw the node connect in");
        remote.SendLine("ping");
        await Wait.ForAsync(() => remote.Saw("HELLO FROM THIRD"), "third's reply relays back to the remote");
    }
}
