using Microsoft.Extensions.Logging.Abstractions;
using Packet.Core;
using Packet.Node.Core.Configuration;
using Packet.Node.Core.Hosting;
using Packet.Node.Core.NetRom;
using Packet.Node.Tests.Support;

namespace Packet.Node.Tests.Integration;

/// <summary>
/// End-to-end NET/ROM L3+L4 over the in-memory radio bus, mirroring the AX.25
/// conformance <c>TwoStationHarness</c> idea at the node-host level: two real
/// nodes — each a <see cref="PortSupervisor"/> + <see cref="NetRomService"/> on a
/// shared software-RF channel — exchange NODES routing broadcasts (L3 origination),
/// and a user connects to node A and is routed by <c>connect &lt;alias&gt;</c>
/// across a NET/ROM L4 circuit to node B's prompt (interlink + circuit + bridge).
/// </summary>
/// <remarks>
/// Real-time over the async bus (the <see cref="Wait"/> idiom) because the AX.25
/// listener pumps are inherently real-time; the deterministic FakeTimeProvider path
/// for the L4 transport + NODES exchange lives in <c>Packet.NetRom.Tests</c>
/// (<c>CircuitPairHarness</c> / <c>NodesExchangeTests</c>).
/// </remarks>
[Trait("Category", "Node")]
public sealed class NetRomL3L4IntegrationTests
{
    private static readonly Callsign ANodeCall = new("GB7AAA", 0);
    private static readonly Callsign BNodeCall = new("GB7BBB", 0);
    private static readonly Callsign UserCall = new("M0LTE", 7);

    private static NodeConfig NodeConfig(Callsign call, string alias) => new()
    {
        Identity = new Identity { Callsign = call.ToString(), Alias = alias },
        NetRom = new NetRomConfig
        {
            Enabled = true,
            Broadcast = true,
            Connect = true,
            Alias = alias,
            // Fast retransmit so a circuit settles inside the test budget.
            TransportTimeoutSeconds = 2,
        },
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
            await Supervisor.DisposeAsync();
            NetRom.Dispose();
        }
    }

    private static async Task<Node> StartNodeAsync(SharedRadioBus bus, Callsign call, string alias)
    {
        var modem = bus.Attach();
        var netRom = new NetRomService(NodeConfig(call, alias).NetRom, TimeProvider.System, NullLogger<NetRomService>.Instance);
        var config = new TestConfigProvider(NodeConfig(call, alias));
        var factory = new FakeTransportFactory().Provide($"kiss-tcp:{call.Base}:1", modem);
        var supervisor = new PortSupervisor(config, factory, TimeProvider.System, NullLoggerFactory.Instance, netRom);
        await supervisor.StartAsync();
        await Wait.ForAsync(() => supervisor.RunningPortIds.Contains("p1"), $"{alias} port p1 should come up");
        return new Node(supervisor, netRom);
    }

    [Fact]
    public async Task Two_nodes_exchange_NODES_and_a_user_routes_across_an_L4_circuit_to_the_distant_node()
    {
        var bus = new SharedRadioBus();
        await using var a = await StartNodeAsync(bus, ANodeCall, "ANODE");
        await using var b = await StartNodeAsync(bus, BNodeCall, "BNODE");

        // L3 origination: each node broadcasts its NODES; the other hears it and
        // learns it as a neighbour. (Broadcast is driven directly here rather than
        // waiting on the real-time NODESINTERVAL timer.)
        var generous = TimeSpan.FromSeconds(60);
        a.NetRom.BroadcastNodes();
        b.NetRom.BroadcastNodes();
        await Wait.ForAsync(() => a.NetRom.Snapshot().ResolveDestination("BNODE") is not null,
            "node A should hear node B's NODES broadcast and learn it as a destination", generous);
        await Wait.ForAsync(() => b.NetRom.Snapshot().ResolveDestination("ANODE") is not null,
            "node B should hear node A's NODES broadcast", generous);

        // A user dials in to node A over the air and reaches A's prompt.
        await using var user = new RemoteStation(bus.Attach(), UserCall);
        await user.StartAsync();
        await user.ConnectAsync(ANodeCall);
        await Wait.ForAsync(() => user.Saw("ANODE"), "node A's banner should arrive");

        // The headline: the user types `C BNODE` (B's alias). Node A resolves it in
        // the routing table → opens an interlink to B → originates an L4 circuit →
        // B accepts + bridges its console → the user reaches NODE B's prompt.
        // Generous budget (reused from above): this step chains several real-time
        // AX.25 handshakes (user dial-in, the A→B interlink) plus the L4 circuit +
        // relay over the async bus, so it does more real-time work than the simpler
        // console tests and needs more headroom under CI cross-assembly scheduling
        // load (the #47 rationale).
        user.SendLine("C BNODE");
        await Wait.ForAsync(() => user.Saw("BNODE") && user.Saw("Connected"),
            "the user should be routed across a NET/ROM L4 circuit to node B's prompt", generous);

        // Prove data flows over the circuit end-to-end: run a command on node B
        // through the relayed circuit and see B's reply come back.
        user.SendLine("I");
        await Wait.ForAsync(() => user.Saw("Software: Packet.NET"),
            "an Info command runs on node B over the L4 circuit and the reply relays back", generous);

        // The interlink + circuit are live on both nodes.
        a.NetRom.Circuits!.Circuits.Should().NotBeEmpty("node A holds the originating circuit");
        await Wait.ForAsync(() => b.NetRom.Circuits!.Circuits.Count > 0, "node B holds the accepted circuit");
    }

    [Fact]
    public async Task Connect_to_an_unknown_alias_reports_failure_without_crashing()
    {
        var bus = new SharedRadioBus();

        // Tight AX.25 timing on this node's port so the fallback dial (NET/ROM has no
        // route → defer to a direct AX.25 dial) fails FAST instead of leaving a
        // 30 s background connect that could starve sibling tests under CI load.
        var cfg = NodeConfig(ANodeCall, "ANODE") with
        {
            Ports =
            [
                new PortConfig
                {
                    Id = "p1",
                    Enabled = true,
                    Transport = new KissTcpTransport { Host = ANodeCall.Base, Port = 1 },
                    Ax25 = new Ax25PortParams { N2 = 1, T1Ms = 200 },   // (1+1)·200 ms ≈ 0.4 s budget
                },
            ],
        };
        var modem = bus.Attach();
        var netRom = new NetRomService(cfg.NetRom, TimeProvider.System, NullLogger<NetRomService>.Instance);
        var config = new TestConfigProvider(cfg);
        var factory = new FakeTransportFactory().Provide($"kiss-tcp:{ANodeCall.Base}:1", modem);
        using var _ = netRom;
        await using var supervisor = new PortSupervisor(config, factory, TimeProvider.System, NullLoggerFactory.Instance, netRom);
        await supervisor.StartAsync();
        await Wait.ForAsync(() => supervisor.RunningPortIds.Contains("p1"), "ANODE port p1 should come up");

        await using var user = new RemoteStation(bus.Attach(), UserCall);
        await user.StartAsync();
        await user.ConnectAsync(ANodeCall);
        await Wait.ForAsync(() => user.Saw("ANODE"), "node A's banner should arrive");

        // No route to NOWHER and no station of that callsign on the channel → the
        // connect path engages, reports, and (after the fast fallback dial fails)
        // the prompt survives. NOWHER is 6 chars so it parses as a callsign (the
        // connect-target parser caps at 6).
        user.SendLine("C NOWHER");
        await Wait.ForAsync(() => user.Saw("Connecting to NOWHER"),
            "the connect path engages and reports the attempt");
        await Wait.ForAsync(() => user.Saw("timed out") || user.Saw("failed"),
            "the routing-miss fallback dial fails (fast) and is reported");
        user.CurrentState.Should().Be("Connected", "a routing miss must not drop the user's session");
    }
}
