using Microsoft.Extensions.Logging.Abstractions;
using Packet.Ax25.Session;
using Packet.Core;
using Packet.Ax25.Transport;
using Packet.NetRom.Wire;
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
    private static readonly Callsign CNodeCall = new("GB7CCC", 0);
    private static readonly Callsign UserCall = new("M0LTE", 7);

    private static NodeConfig NodeConfig(Callsign call, string alias) => new()
    {
        Identity = new Identity { Callsign = call.ToString(), Alias = alias },
        NetRom = new NetRomConfig
        {
            Enabled = true,
            Broadcast = true,
            Connect = true,
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
            // Dispose NET/ROM first (its DisposeAsync DISCs interlinks while the
            // supervisor's listeners are still alive — mirrors NodeHostedService).
            await NetRom.DisposeAsync();
            await Supervisor.DisposeAsync();
        }
    }

    private static async Task<Node> StartNodeAsync(SharedRadioBus bus, Callsign call, string alias)
    {
        var modem = bus.Attach();
        var netRom = new NetRomService(NodeConfig(call, alias).NetRom, TimeProvider.System, NullLogger<NetRomService>.Instance, nodeAlias: alias);
        var config = new TestConfigProvider(NodeConfig(call, alias));
        var factory = new FakeTransportFactory().Provide($"kiss-tcp:{call.Base}:1", modem);
        var supervisor = new PortSupervisor(config, factory, TimeProvider.System, NullLoggerFactory.Instance, netRom);
        await supervisor.StartAsync();
        await Wait.ForAsync(() => supervisor.RunningPortIds.Contains("p1"), $"{alias} port p1 should come up");
        return new Node(supervisor, netRom);
    }

    /// <summary>
    /// Start a node with one or more ports, each on a different software-RF channel —
    /// so a node can <b>bridge</b> two channels (the transit topology). Each port maps
    /// to a distinct transport endpoint (<c>kiss-tcp:{base}:{port}</c>) backed by the
    /// supplied modem.
    /// </summary>
    private static async Task<Node> StartBridgeNodeAsync(
        Callsign call, string alias, params (string PortId, int Port, IAx25Transport Modem)[] ports)
    {
        var nodeConfig = new NodeConfig
        {
            Identity = new Identity { Callsign = call.ToString(), Alias = alias },
            NetRom = new NetRomConfig
            {
                Enabled = true,
                Broadcast = true,
                Connect = true,   // Forward defaults on under Connect — the transit role
                TransportTimeoutSeconds = 2,
            },
            Ports = [.. ports.Select(p => new PortConfig
            {
                Id = p.PortId,
                Enabled = true,
                Transport = new KissTcpTransport { Host = call.Base, Port = p.Port },
                Ax25 = new Ax25PortParams { N2 = TestAx25Timing.NodeN2 },
            })],
        };

        var netRom = new NetRomService(nodeConfig.NetRom, TimeProvider.System, NullLogger<NetRomService>.Instance, nodeAlias: alias);
        var config = new TestConfigProvider(nodeConfig);
        var factory = new FakeTransportFactory();
        foreach (var p in ports)
        {
            factory.Provide($"kiss-tcp:{call.Base}:{p.Port}", p.Modem);
        }
        var supervisor = new PortSupervisor(config, factory, TimeProvider.System, NullLoggerFactory.Instance, netRom);
        await supervisor.StartAsync();
        foreach (var p in ports)
        {
            await Wait.ForAsync(() => supervisor.RunningPortIds.Contains(p.PortId), $"{alias} port {p.PortId} should come up");
        }
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
    public async Task An_opt_in_app_alias_is_advertised_in_NODES_and_a_neighbour_learns_it()
    {
        // Slice 3 (docs/app-packages.md § Application packet identity): node A sets an app
        // NET/ROM advert (alias RDGBBS → the app's resolved callsign GB7AAA-7, via A). When A
        // broadcasts NODES, neighbour B should learn RDGBBS as a routable destination pointing
        // at the app callsign — composing with A's own node-self advert. (Absent advert = the
        // default, exercised by every other broadcast test in this file: none learns an app alias.)
        var bus = new SharedRadioBus();
        await using var a = await StartNodeAsync(bus, ANodeCall, "ANODE");
        await using var b = await StartNodeAsync(bus, BNodeCall, "BNODE");

        var appCall = new Callsign(ANodeCall.Base, 7);
        a.NetRom.AppAdvertSource = () =>
            [new NodesBroadcastBuilder.Entry(appCall, "RDGBBS", ANodeCall, Quality: 255)];

        var generous = TimeSpan.FromSeconds(60);
        a.NetRom.BroadcastNodes();

        await Wait.ForAsync(
            () => b.NetRom.Snapshot().ResolveDestination("RDGBBS") is { } d && d.Destination.Equals(appCall),
            "neighbour B should learn the app alias RDGBBS → the app callsign from A's NODES advert",
            generous);

        // A node that set NO app advert never floods an app alias (the off-by-default contract):
        // B advertises only its own identity, so A never learns RDGBBS from B.
        b.NetRom.BroadcastNodes();
        await Wait.ForAsync(() => a.NetRom.Snapshot().ResolveDestination("BNODE") is not null,
            "A hears B's node-self advert", generous);
        a.NetRom.Snapshot().ResolveDestination("RDGBBS").Should().BeNull(
            "B set no app advert, so the alias is never on the mesh from B");
    }

    [Fact]
    public async Task A_transit_node_forwards_an_L4_circuit_between_two_channels_it_bridges_without_terminating_it()
    {
        // The transit topology: A is on channel 1, C is on channel 2, and B bridges
        // the two (a port on each). A can NOT hear C directly — it learns C only from
        // B's NODES, so A's only route to C is *via B*. When a user routes `C CNODE`
        // from A, A originates an L4 circuit whose datagrams are addressed to C; B
        // forwards them across the ch1↔ch2 bridge (the network-layer routing role)
        // without ever terminating the circuit, and C accepts it. This is the thing a
        // leaf node cannot do.
        var ch1 = new SharedRadioBus();   // A ↔ B
        var ch2 = new SharedRadioBus();   // B ↔ C

        await using var a = await StartNodeAsync(ch1, ANodeCall, "ANODE");
        await using var b = await StartBridgeNodeAsync(BNodeCall, "BNODE", ("p1", 1, ch1.Attach()), ("p2", 2, ch2.Attach()));
        await using var c = await StartNodeAsync(ch2, CNodeCall, "CNODE");

        var generous = TimeSpan.FromSeconds(60);

        // NODES propagation must settle BOTH ways so the circuit can establish and
        // its acks return: B hears A (ch1) + C (ch2) directly, then re-advertises so
        // A learns C-via-B and C learns A-via-B (neither hears the other directly).
        // (Broadcasts driven directly, not via the real-time NODESINTERVAL timer.)
        a.NetRom.BroadcastNodes();
        b.NetRom.BroadcastNodes();
        c.NetRom.BroadcastNodes();
        await Wait.ForAsync(
            () => b.NetRom.Snapshot().ResolveDestination("ANODE") is not null
               && b.NetRom.Snapshot().ResolveDestination("CNODE") is not null,
            "node B (the bridge) should hear node A on channel 1 and node C on channel 2", generous);

        b.NetRom.BroadcastNodes();   // re-advertise both ends now B knows both
        await Wait.ForAsync(
            () => a.NetRom.Snapshot().ResolveDestination("CNODE")?.BestRoute?.Neighbour.Equals(BNodeCall) == true
               && c.NetRom.Snapshot().ResolveDestination("ANODE")?.BestRoute?.Neighbour.Equals(BNodeCall) == true,
            "A learns C via B (ch1) and C learns A via B (ch2) — neither hears the other directly", generous);

        // A user dials node A and routes to C by alias.
        await using var user = new RemoteStation(ch1.Attach(), UserCall);
        await user.StartAsync();
        await user.ConnectAsync(ANodeCall);
        await Wait.ForAsync(() => user.Saw("ANODE"), "node A's banner should arrive");

        user.SendLine("C CNODE");
        await Wait.ForAsync(() => user.Saw("CNODE") && user.Saw("Connected"),
            "the user is routed across a NET/ROM L4 circuit — forwarded by B — to node C's prompt", generous);

        // Data round-trips end-to-end through the transit node.
        user.SendLine("I");
        await Wait.ForAsync(() => user.Saw("Software: Packet.NET"),
            "an Info command runs on node C over the forwarded L4 circuit and the reply relays back", generous);

        // The headline: A holds the originating circuit and C the accepted one, but
        // B holds NONE — it forwarded the circuit's datagrams as a transit node,
        // never terminating one. (B's OnInterlinkData routes a datagram addressed to
        // C onward instead of handing it to B's circuit manager.)
        a.NetRom.Circuits!.Circuits.Should().NotBeEmpty("node A holds the originating circuit");
        await Wait.ForAsync(() => c.NetRom.Circuits!.Circuits.Count > 0, "node C holds the accepted circuit", generous);
        b.NetRom.Circuits!.Circuits.Should().BeEmpty(
            "node B is a transit node — it forwarded the circuit's datagrams between its two channels without terminating one");
    }

    [Fact]
    public async Task Disposing_the_service_cleanly_DISCs_its_interlink_so_the_neighbour_does_not_keep_a_half_open_link()
    {
        // Regression for the #309 interop-contamination class: the node that opens a
        // NET/ROM interlink must DISC that AX.25 session on teardown, otherwise the
        // neighbour is left with a half-open connected-mode link it keeps polling onto
        // the shared channel (which flakes timing-sensitive AX.25 tests, and on real
        // RF leaves a peer like LinBPQ holding a phantom session). Here we open an
        // interlink A→B, capture B's accepted interlink session, dispose A's
        // NetRomService, and assert B's session reaches Disconnected — i.e. A's DISC
        // crossed the wire and B tore its half down.
        var bus = new SharedRadioBus();
        var a = await StartNodeAsync(bus, ANodeCall, "ANODE");
        await using var b = await StartNodeAsync(bus, BNodeCall, "BNODE");

        // Capture the interlink session B accepts from A (remote == A's node call).
        Ax25Session? bInterlink = null;
        var bPort = b.Supervisor.GetPort("p1");
        bPort.Should().NotBeNull();
        bPort!.Listener.SessionAccepted += (_, e) =>
        {
            if (e.Session.Context.Remote.Equals(ANodeCall))
            {
                bInterlink = e.Session;
            }
        };

        var generous = TimeSpan.FromSeconds(60);
        a.NetRom.BroadcastNodes();
        b.NetRom.BroadcastNodes();
        await Wait.ForAsync(() => a.NetRom.Snapshot().ResolveDestination("BNODE") is not null,
            "node A should hear node B's NODES so it has a route to open the interlink", generous);

        // Open the interlink + L4 circuit A→B directly (no user needed for this test).
        var dest = a.NetRom.Snapshot().ResolveDestination("BNODE")!;
        await using (var conn = await a.NetRom.ConnectCircuitAsync(dest, UserCall))
        {
            await Wait.ForAsync(() => bInterlink is not null,
                "node B should accept node A's interlink AX.25 session", generous);
            bInterlink!.CurrentState.Should().Be("Connected", "the interlink AX.25 session is up on B");
        }

        // Tear node A's NET/ROM service down gracefully. This must DISC the interlink.
        await a.NetRom.DisposeAsync();
        await a.Supervisor.DisposeAsync();

        await Wait.ForAsync(() => bInterlink!.CurrentState == "Disconnected",
            "node A's DisposeAsync must DISC its interlink so B's AX.25 session returns to Disconnected (no half-open link left polling)",
            generous);
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

    [Fact]
    public async Task A_connect_marks_a_down_next_hop_down_and_fails_over_to_the_alternate_route()
    {
        // The link-down failover signal at the connect path: a `connect <alias>`
        // whose best next-hop neighbour can't be reached marks that neighbour down
        // (dropping its routes) and re-routes to the destination's next-best route —
        // instead of failing outright or re-dialling a dead link. See
        // NetRomService.ConnectCircuitAsync + EnsureInterlinkAsync, and the routing
        // primitive NetRomRoutingTable.MarkNeighbourDown.
        var bus = new SharedRadioBus();
        await using var a = await StartNodeAsync(bus, ANodeCall, "ANODE");

        // Prime A's table with TWO routes to GB7CCC, as if it had heard NODES from
        // both neighbours: via GB7NB1 (higher quality → best) and via GB7NB2.
        var nb1 = new Callsign("GB7NB1", 0);
        var nb2 = new Callsign("GB7NB2", 0);
        IngestRoute(a.NetRom, originator: nb1, dest: CNodeCall, quality: 250);
        IngestRoute(a.NetRom, originator: nb2, dest: CNodeCall, quality: 150);

        // Both interlinks are dead: the dial hook throws immediately (deterministic —
        // no real N2 timeout). Record the dial order to prove the failover walked
        // from the best route to the alternate.
        var dialed = new List<Callsign>();
        a.NetRom.OpenInterlink = (_, neighbour, _, _) =>
        {
            dialed.Add(neighbour);
            throw new IOException($"{neighbour} did not answer");
        };

        var dest = a.NetRom.Snapshot().ResolveDestination(CNodeCall.ToString());
        dest.Should().NotBeNull();

        Func<Task> connect = async () => await a.NetRom.ConnectCircuitAsync(dest!, UserCall);
        await connect.Should().ThrowAsync<InvalidOperationException>(
            "with every next hop down, the connect exhausts its routes");

        dialed.Should().Equal([nb1, nb2],
            "the best route is tried first, then it fails over to the alternate once nb1 is marked down");
        a.NetRom.Snapshot().ResolveDestination(CNodeCall.ToString())
            .Should().BeNull("both dead next hops were marked down, so the destination has no routes left");
    }

    // Prime a route to <paramref name="dest"/> via <paramref name="originator"/> by
    // ingesting a NODES broadcast as if heard firsthand from that neighbour.
    private static void IngestRoute(NetRomService svc, Callsign originator, Callsign dest, byte quality)
    {
        var info = NodesBroadcastBuilder.Build(
            originator.Base,
            [new NodesBroadcastBuilder.Entry(dest, dest.Base, originator, quality)])[0];
        NodesBroadcast.TryParse(info, out var bc).Should().BeTrue();
        svc.RoutingTable.Ingest(originator, ANodeCall, "p1", bc!);
    }
}
