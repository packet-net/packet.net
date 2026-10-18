using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Packet.Core;
using Packet.Node.Core.Configuration;
using Packet.Node.Core.Heard;
using Packet.Node.Core.Hosting;
using Packet.Node.Core.NetRom;
using Packet.Node.Tests.Support;
using Xunit;

namespace Packet.Node.Tests.Integration;

/// <summary>
/// packet.net#933: <c>MH</c> typed by a user who reached the node over RF (an AX.25 connect) or over
/// a NET/ROM circuit shows the same heard log the telnet and web consoles show. Both console
/// construction paths in the port supervisor used to be built with no heard log.
/// </summary>
[Trait("Category", "Node")]
public sealed class MhOverRfAndNetRomTests
{
    private static readonly Callsign ANodeCall = new("GB7AAA", 0);
    private static readonly Callsign BNodeCall = new("GB7BBB", 0);
    private static readonly Callsign UserAtA = new("M0LTE", 7);

    private static NodeConfig NodeConfig(Callsign call, string alias) => new()
    {
        Identity = new Identity { Callsign = call.ToString(), Alias = alias },
        NetRom = new NetRomConfig { Enabled = true, Broadcast = true, Connect = true, TransportTimeoutSeconds = 2 },
        Ports =
        [
            new PortConfig
            {
                Id = "p1",
                Enabled = true,
                Transport = new KissTcpTransport { Host = call.Base, Port = 1 },
                Ax25 = new Ax25PortParams { N2 = TestAx25Timing.NodeN2 },
                NodesBroadcast = true,
            },
        ],
    };

    private sealed record Node(PortSupervisor Supervisor, NetRomService NetRom) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            await NetRom.DisposeAsync();
            await Supervisor.DisposeAsync();
        }
    }

    private static async Task<Node> StartNodeAsync(SharedRadioBus bus, Callsign call, string alias, HeardLog heard)
    {
        var netRom = new NetRomService(NodeConfig(call, alias).NetRom, TimeProvider.System, NullLogger<NetRomService>.Instance, nodeAlias: alias);
        var config = new TestConfigProvider(NodeConfig(call, alias));
        var factory = new FakeTransportFactory().Provide($"kiss-tcp:{call.Base}:1", bus.Attach());
        var supervisor = new PortSupervisor(config, factory, TimeProvider.System, NullLoggerFactory.Instance, netRom, heardLog: heard);
        await supervisor.StartAsync();
        await Wait.ForAsync(() => supervisor.RunningPortIds.Contains("p1"), $"{alias} port p1 should come up");
        return new Node(supervisor, netRom);
    }

    private static HeardLog SeededLog()
    {
        var log = new HeardLog();
        log.Record("p1", "G9HRD-1", DateTimeOffset.UtcNow.AddMinutes(-3));
        return log;
    }

    [Fact]
    public async Task Mh_over_an_rf_connect_lists_the_heard_stations()
    {
        var bus = new SharedRadioBus();
        var generous = TimeSpan.FromSeconds(60);
        await using var a = await StartNodeAsync(bus, ANodeCall, "ANODE", SeededLog());

        await using var user = new RemoteStation(bus.Attach(), UserAtA);
        await user.StartAsync();
        await user.ConnectAsync(ANodeCall);
        await Wait.ForAsync(() => user.Saw("ANODE"), "the banner reaches the RF user", generous);
        user.SendLine("MH");

        await Wait.ForAsync(() => user.Saw("G9HRD-1"), "MH lists the heard station to an RF user", generous);
        user.Saw("not available").Should().BeFalse();
    }

    [Fact]
    public async Task Mh_over_a_netrom_circuit_lists_the_heard_stations()
    {
        var bus = new SharedRadioBus();
        var generous = TimeSpan.FromSeconds(60);
        await using var a = await StartNodeAsync(bus, ANodeCall, "ANODE", new HeardLog());
        await using var b = await StartNodeAsync(bus, BNodeCall, "BNODE", SeededLog());
        a.NetRom.BroadcastNodes();
        b.NetRom.BroadcastNodes();
        await Wait.ForAsync(() => a.NetRom.Snapshot().ResolveDestination("BNODE") is not null, "A learns BNODE", generous);

        await using var user = new RemoteStation(bus.Attach(), UserAtA);
        await user.StartAsync();
        await user.ConnectAsync(ANodeCall);
        await Wait.ForAsync(() => user.Saw("ANODE"), "A's banner reaches its user", generous);
        user.SendLine("C BNODE");
        await Wait.ForAsync(() => user.Saw("Connected"), "the user is routed over an L4 circuit to B's prompt", generous);
        user.SendLine("MH");

        await Wait.ForAsync(() => user.Saw("G9HRD-1"), "B's MH lists its heard station to a NET/ROM user", generous);
        user.Saw("not available").Should().BeFalse();
    }
}
