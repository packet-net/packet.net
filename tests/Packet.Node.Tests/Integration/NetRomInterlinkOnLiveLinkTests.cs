using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Packet.Ax25;
using Packet.Ax25.Session;
using Packet.Core;
using Packet.Node.Core.Configuration;
using Packet.Node.Core.Hosting;
using Packet.Node.Core.NetRom;
using Packet.Node.Tests.Support;
using Xunit;

namespace Packet.Node.Tests.Integration;

/// <summary>
/// packet.net#889: an interlink dial that finds a link to the neighbour already up takes it,
/// and sends nothing. Before, the dial went to the listener as a fresh connect, which posted a
/// DL-CONNECT request on the live link: SABM on an up link, a reset at both ends, whatever was
/// in flight discarded, and (since #885) the neighbour's console user on that link told so.
/// </summary>
[Trait("Category", "Node")]
public sealed class NetRomInterlinkOnLiveLinkTests
{
    private static readonly Callsign ANodeCall = new("GB7AAA", 0);
    private static readonly Callsign BNodeCall = new("GB7BBB", 0);
    private static readonly Callsign UserAtA = new("M0LTE", 7);
    private static readonly Callsign UserAtB = new("G4XYZ", 1);

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

    private static async Task<Node> StartNodeAsync(SharedRadioBus bus, Callsign call, string alias)
    {
        var netRom = new NetRomService(NodeConfig(call, alias).NetRom, TimeProvider.System, NullLogger<NetRomService>.Instance, nodeAlias: alias);
        var config = new TestConfigProvider(NodeConfig(call, alias));
        var factory = new FakeTransportFactory().Provide($"kiss-tcp:{call.Base}:1", bus.Attach());
        var supervisor = new PortSupervisor(config, factory, TimeProvider.System, NullLoggerFactory.Instance, netRom);
        await supervisor.StartAsync();
        await Wait.ForAsync(() => supervisor.RunningPortIds.Contains("p1"), $"{alias} port p1 should come up");
        return new Node(supervisor, netRom);
    }

    [Fact]
    public async Task A_circuit_to_a_neighbour_whose_call_already_brought_the_link_up_goes_over_that_link()
    {
        var bus = new SharedRadioBus();
        var generous = TimeSpan.FromSeconds(60);

        // Everything on the air, so the SABMs A sends B can be counted.
        await using var monitor = bus.Attach();
        int sabmsAtoB = 0;
        using var stop = new CancellationTokenSource();
        var watching = Task.Run(async () =>
        {
            try
            {
                await foreach (var f in monitor.ReceiveAsync(stop.Token))
                {
                    if (Ax25Frame.TryParse(f.Ax25.Span, Ax25ParseOptions.Lenient, out var frame)
                        && frame.FrameType is Ax25FrameType.Sabm or Ax25FrameType.Sabme
                        && frame.Source.Callsign.Equals(ANodeCall) && frame.Destination.Callsign.Equals(BNodeCall))
                    {
                        Interlocked.Increment(ref sabmsAtoB);
                    }
                }
            }
            catch (OperationCanceledException)
            {
            }
        });
        try
        {
            await using var a = await StartNodeAsync(bus, ANodeCall, "ANODE");
            await using var b = await StartNodeAsync(bus, BNodeCall, "BNODE");
            a.NetRom.BroadcastNodes();
            b.NetRom.BroadcastNodes();
            await Wait.ForAsync(() => a.NetRom.Snapshot().ResolveDestination("BNODE") is not null, "A learns BNODE", generous);
            await Wait.ForAsync(() => b.NetRom.Snapshot().ResolveDestination("ANODE") is not null, "B learns ANODE", generous);

            // A user at B has B's node call A's console over AX.25: the link B -> A is up, and at A
            // it is a console session for GB7BBB, carrying no NET/ROM.
            await using var userAtB = new RemoteStation(bus.Attach(), UserAtB);
            await userAtB.StartAsync();
            await userAtB.ConnectAsync(BNodeCall);
            await Wait.ForAsync(() => userAtB.Saw("BNODE"), "B's banner reaches its user", generous);
            userAtB.SendLine($"C 1 {ANodeCall}");
            await Wait.ForAsync(() => userAtB.Saw("ANODE"), "A's banner reaches B's user through B's console", generous);
            Volatile.Read(ref sabmsAtoB).Should().Be(0, "B dialled A, not the other way");

            // Now a user at A routes to BNODE: A needs an interlink to B, and the link is already up.
            await using var userAtA = new RemoteStation(bus.Attach(), UserAtA);
            await userAtA.StartAsync();
            await userAtA.ConnectAsync(ANodeCall);
            await Wait.ForAsync(() => userAtA.Saw("ANODE"), "A's banner reaches its user", generous);
            userAtA.SendLine("C BNODE");
            await Wait.ForAsync(() => userAtA.Saw("BNODE") && userAtA.Saw("Connected"),
                "A's user is routed over an L4 circuit to B's prompt", generous);

            Volatile.Read(ref sabmsAtoB).Should().Be(0, "the interlink took the link B's call brought up; nothing was dialled");
            userAtB.Saw("Disconnected from").Should().BeFalse("B's console on A rode on; the link was not reset under it");

            // The console link still works for B's user: a command runs on A and its reply comes back.
            userAtB.SendLine("I");
            await Wait.ForAsync(() => userAtB.Saw("Software: Packet.NET"), "B's user still has A's console over the same link", generous);
        }
        finally
        {
            await stop.CancelAsync();
            await watching;
        }
    }
}
