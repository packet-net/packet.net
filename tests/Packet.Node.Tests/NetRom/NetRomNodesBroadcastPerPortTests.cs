using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Packet.Ax25;
using Packet.Ax25.Session;
using Packet.Core;
using Packet.NetRom.Wire;
using Packet.Node.Core.Configuration;
using Packet.Node.Core.Hosting;
using Packet.Node.Core.NetRom;
using Packet.Node.Core.Transports;
using Packet.Node.Tests.Support;

namespace Packet.Node.Tests.NetRom;

/// <summary>
/// #929: the per-port switch for sending our NET/ROM NODES broadcast (<c>nodesBroadcast</c>,
/// default off). A port with it off never carries a NODES broadcast, on any send path (an
/// explicit <see cref="NetRomService.BroadcastNodes"/>, the NODESINTERVAL timer, a port
/// attached through the supervisor from config, the live reconcile of an edit, the multipoint
/// AXUDP fan-out); the node-wide <c>netRom.broadcast</c> stays the master; and hearing NODES
/// on a port with it off still learns routes.
/// </summary>
/// <remarks>
/// Absence is proven with a sentinel rather than a sleep: the listener's connectionless send is
/// synchronous to the in-memory medium (no carrier sense wired), so once
/// <see cref="NetRomService.BroadcastNodes"/> returns, any NODES frame it sent is already on the
/// medium. A sentinel UI frame sent on the same listener afterwards therefore lands behind it,
/// and "the sentinel arrived with no NODES frame" is a complete observation.
/// </remarks>
[Trait("Category", "Node")]
public sealed class NetRomNodesBroadcastPerPortTests
{
    private static readonly Callsign NodeCall = new("GB7AAA", 0);
    private static readonly Callsign Neighbour = new("GB7RDG", 0);
    private static readonly Callsign Sentinel = new("SENTNL", 0);

    private static NetRomConfig Broadcasting() => new() { Enabled = true, Broadcast = true };

    // A started listener on one side of an in-memory medium, with a collector on the far side.
    private static async Task<(Ax25Listener Listener, FarSide Far)> PortAsync()
    {
        var (near, far) = InMemoryRadio.CreatePair();
        var listener = new Ax25Listener(near, new Ax25ListenerOptions { MyCall = NodeCall, N2 = TestAx25Timing.StationN2 }, TimeProvider.System);
        await listener.StartAsync();
        return (listener, new FarSide(far));
    }

    private static Task SendSentinelAsync(Ax25Listener listener)
        => listener.SendUiAsync(Sentinel, "sentinel"u8.ToArray());

    [Fact]
    public async Task A_broadcast_goes_out_only_on_ports_with_nodesBroadcast_on()
    {
        var (onListener, onFar) = await PortAsync();
        var (offListener, offFar) = await PortAsync();
        await using var _ = onListener;
        await using var __ = offListener;
        await using var ___ = onFar;
        await using var ____ = offFar;
        await using var svc = new NetRomService(Broadcasting(), TimeProvider.System, NullLogger<NetRomService>.Instance, nodeAlias: "AAA");

        svc.AttachPort("on", NodeCall, onListener, nodesBroadcast: true);
        svc.AttachPort("off", NodeCall, offListener);   // the default: off

        svc.BroadcastNodes();
        await SendSentinelAsync(offListener);

        await Wait.ForAsync(() => onFar.NodesCount > 0, "the ticked port carries the NODES broadcast");
        await Wait.ForAsync(() => offFar.HeardSentinel, "the unticked port's sentinel arrives");
        offFar.NodesCount.Should().Be(0, "a port with nodesBroadcast off never transmits NODES");
    }

    [Fact]
    public async Task The_node_wide_switch_is_the_master_a_ticked_port_sends_nothing_with_it_off()
    {
        var (listener, far) = await PortAsync();
        await using var _ = listener;
        await using var __ = far;
        await using var svc = new NetRomService(new NetRomConfig { Enabled = true, Broadcast = false }, TimeProvider.System, NullLogger<NetRomService>.Instance);

        svc.AttachPort("p1", NodeCall, listener, nodesBroadcast: true);

        svc.BroadcastNodes();
        await SendSentinelAsync(listener);

        await Wait.ForAsync(() => far.HeardSentinel, "the sentinel arrives");
        far.NodesCount.Should().Be(0, "netRom.broadcast off stops NODES on every port, ticked or not");
    }

    [Fact]
    public async Task The_NODESINTERVAL_timer_honours_the_per_port_switch()
    {
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 10, 17, 12, 0, 0, TimeSpan.Zero));
        var (onListener, onFar) = await PortAsync();
        var (offListener, offFar) = await PortAsync();
        await using var _ = onListener;
        await using var __ = offListener;
        await using var ___ = onFar;
        await using var ____ = offFar;
        await using var svc = new NetRomService(Broadcasting() with { SweepIntervalSeconds = 60 }, clock, NullLogger<NetRomService>.Instance);

        svc.AttachPort("on", NodeCall, onListener, nodesBroadcast: true);
        svc.AttachPort("off", NodeCall, offListener, nodesBroadcast: false);

        clock.Advance(TimeSpan.FromSeconds(60));   // the scheduled tick, not a direct call
        await Wait.ForAsync(() => onFar.NodesCount > 0, "the timer's broadcast reaches the ticked port");
        await SendSentinelAsync(offListener);

        await Wait.ForAsync(() => offFar.HeardSentinel, "the unticked port's sentinel arrives");
        offFar.NodesCount.Should().Be(0, "the timer path is gated per port too");
    }

    [Fact]
    public async Task Ticking_and_unticking_a_port_applies_to_the_next_broadcast()
    {
        var (listener, far) = await PortAsync();
        await using var _ = listener;
        await using var __ = far;
        await using var svc = new NetRomService(Broadcasting(), TimeProvider.System, NullLogger<NetRomService>.Instance);
        svc.AttachPort("p1", NodeCall, listener);

        svc.UpdatePortQuality("p1", neighbourQuality: null, nodesBroadcast: true);
        svc.BroadcastNodes();
        await Wait.ForAsync(() => far.NodesCount == 1, "ticked live: the next broadcast goes out");

        svc.UpdatePortQuality("p1", neighbourQuality: null, nodesBroadcast: false);
        svc.BroadcastNodes();
        await SendSentinelAsync(listener);
        await Wait.ForAsync(() => far.HeardSentinel, "the sentinel arrives");
        far.NodesCount.Should().Be(1, "unticked live: no further NODES on the port");
    }

    [Fact]
    public async Task A_port_with_nodesBroadcast_off_still_hears_NODES_and_learns_routes()
    {
        var (listener, far) = await PortAsync();
        await using var _ = listener;
        await using var __ = far;
        await using var svc = new NetRomService(Broadcasting(), TimeProvider.System, NullLogger<NetRomService>.Instance);
        svc.AttachPort("p1", NodeCall, listener);   // nodesBroadcast off

        var info = NodesBroadcastBuilder.Build("RDGBPQ",
            [new NodesBroadcastBuilder.Entry(new Callsign("GB7SOT", 0), "SOT", new Callsign("GB7XYZ", 0), 200)])[0];
        var frame = Ax25Frame.Ui(new Callsign("NODES", 0), Neighbour, info, Ax25Frame.PidNetRom, isCommand: true);
        await far.Endpoint.SendAsync(frame.ToBytes());

        await Wait.ForAsync(() => svc.Snapshot().ResolveDestination("SOT") is not null,
            "receiving and learning NODES is unaffected by the per-port send switch");
        svc.Snapshot().Neighbours.Should().ContainSingle(n => n.Neighbour == Neighbour && n.PortId == "p1");
    }

    [Fact]
    public async Task The_multipoint_AXUDP_fan_out_sends_nothing_from_a_port_with_it_off()
    {
        // One broadcast peer on a raw socket: BPQAXIP's B suffix. With the port unticked the
        // fan-out never happens; ticked, the peer gets our NODES.
        using var peer = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        var peerCall = new Callsign("GB7BBB", 0);
        var peerEndpoint = new IPEndPoint(IPAddress.Loopback, ((IPEndPoint)peer.Client.LocalEndPoint!).Port);
        var transport = new AxudpMultipointFrameTransport([new(peerCall, peerEndpoint, Broadcast: true)], localPort: 0);
        await using var listener = new Ax25Listener(transport, new Ax25ListenerOptions { MyCall = NodeCall, N2 = TestAx25Timing.StationN2 }, TimeProvider.System);
        await listener.StartAsync();
        await using var svc = new NetRomService(Broadcasting(), TimeProvider.System, NullLogger<NetRomService>.Instance);
        svc.AttachPort("mp", NodeCall, listener);   // nodesBroadcast off
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        svc.BroadcastNodes();
        // A frame addressed to the peer's own call routes to it alone, so it is the peer's
        // first datagram exactly when the broadcast above sent nothing.
        await listener.SendUiAsync(peerCall, "sentinel"u8.ToArray());
        var first = Parse((await peer.ReceiveAsync(cts.Token)).Buffer);
        first.Destination.Callsign.Should().Be(peerCall, "no NODES fanned out ahead of the directed frame");

        svc.UpdatePortQuality("mp", neighbourQuality: null, nodesBroadcast: true);
        svc.BroadcastNodes();
        var next = Parse((await peer.ReceiveAsync(cts.Token)).Buffer);
        next.Destination.Callsign.Base.Should().Be("NODES", "ticked, the broadcast fans out to the B peer");
        next.Pid.Should().Be(Ax25Frame.PidNetRom);
    }

    [Fact]
    public async Task Config_reaches_the_wire_through_the_supervisor_and_a_live_edit_ticks_a_port()
    {
        var (nearA, farA) = InMemoryRadio.CreatePair();
        var (nearB, farB) = InMemoryRadio.CreatePair();
        await using var collectA = new FarSide(farA);
        await using var collectB = new FarSide(farB);

        var before = new NodeConfig
        {
            Identity = new Identity { Callsign = NodeCall.ToString(), Alias = "AAA" },
            NetRom = Broadcasting(),
            Ports =
            [
                new PortConfig { Id = "a", Transport = new KissTcpTransport { Host = "mem", Port = 1 }, NodesBroadcast = true },
                new PortConfig { Id = "b", Transport = new KissTcpTransport { Host = "mem", Port = 2 } },
            ],
        };
        var config = new TestConfigProvider(before);
        var factory = new FakeTransportFactory().Provide("kiss-tcp:mem:1", nearA).Provide("kiss-tcp:mem:2", nearB);
        await using var netRom = new NetRomService(before.NetRom, TimeProvider.System, NullLogger<NetRomService>.Instance, nodeAlias: "AAA");
        await using var supervisor = new PortSupervisor(config, factory, TimeProvider.System, NullLoggerFactory.Instance, netRom);
        await supervisor.StartAsync();
        await Wait.ForAsync(() => supervisor.RunningPortIds.Contains("a") && supervisor.RunningPortIds.Contains("b"), "both ports up");

        netRom.BroadcastNodes();
        await SendSentinelAsync(supervisor.GetPort("b")!.Listener);
        await Wait.ForAsync(() => collectA.NodesCount == 1, "port a (ticked in config) carries NODES");
        await Wait.ForAsync(() => collectB.HeardSentinel, "port b's sentinel arrives");
        collectB.NodesCount.Should().Be(0, "port b (default off) carries none");

        // The operator ticks port b: a live edit, no restart.
        var after = before with { Ports = [before.Ports[0], before.Ports[1] with { NodesBroadcast = true }] };
        var plan = ReconcilePlanner.Plan(before, after);
        plan.NetRomQualityChanged.Select(p => p.Id).Should().Equal("b");
        plan.ToRestart.Should().BeEmpty("the switch is applied live");
        config.Apply(after);
        await supervisor.ApplyAsync(plan, after);

        netRom.BroadcastNodes();
        await Wait.ForAsync(() => collectB.NodesCount == 1, "port b carries the next broadcast after the edit");
    }

    [Fact]
    public void The_start_up_warning_fires_when_broadcast_is_on_and_no_enabled_port_sends_NODES()
    {
        static PortConfig Port(string id, bool enabled = true, bool nodes = false)
            => new() { Id = id, Enabled = enabled, NodesBroadcast = nodes, Transport = new KissTcpTransport { Host = "h", Port = 1 } };
        static NodeConfig Cfg(bool broadcast, params PortConfig[] ports) => new()
        {
            Identity = new Identity { Callsign = "GB7AAA" },
            NetRom = new NetRomConfig { Enabled = true, Broadcast = broadcast },
            Ports = ports,
        };

        NodeHostedService.BroadcastsNodesOnNoPort(Cfg(true, Port("a"), Port("b")))
            .Should().BeTrue("the upgraded-node case: the master is on but nothing is ticked");
        NodeHostedService.BroadcastsNodesOnNoPort(Cfg(true, Port("a", enabled: false, nodes: true)))
            .Should().BeTrue("a ticked port that is disabled sends nothing either");
        NodeHostedService.BroadcastsNodesOnNoPort(Cfg(true, Port("a"), Port("b", nodes: true)))
            .Should().BeFalse("one ticked, enabled port is enough");
        NodeHostedService.BroadcastsNodesOnNoPort(Cfg(false, Port("a")))
            .Should().BeFalse("with broadcast off the node is not meant to send NODES at all");
    }

    private static Ax25Frame Parse(byte[] datagram)
    {
        // The multipoint transport appends the AXUDP FCS; strip it to read the frame.
        var body = datagram.AsSpan(0, datagram.Length - 2);
        Ax25Frame.TryParse(body, out var frame).Should().BeTrue();
        return frame!;
    }

    // Collects every frame that arrives on the far side of an in-memory medium.
    private sealed class FarSide : IAsyncDisposable
    {
        private readonly CancellationTokenSource cts = new();
        private readonly Task pump;
        private readonly ConcurrentQueue<Ax25Frame> heard = new();

        public FarSide(InMemoryRadio.Endpoint endpoint)
        {
            Endpoint = endpoint;
            pump = Task.Run(async () =>
            {
                try
                {
                    await foreach (var f in endpoint.ReceiveAsync(cts.Token))
                    {
                        if (Ax25Frame.TryParse(f.Ax25.Span, out var frame))
                        {
                            heard.Enqueue(frame);
                        }
                    }
                }
                catch (OperationCanceledException)
                {
                    // disposal
                }
            });
        }

        public InMemoryRadio.Endpoint Endpoint { get; }

        public int NodesCount => heard.Count(f => f.IsUi && f.Destination.Callsign.Base == NodesBroadcast.NodesDestination);

        public bool HeardSentinel => heard.Any(f => f.Destination.Callsign == Sentinel);

        public async ValueTask DisposeAsync()
        {
            await cts.CancelAsync();
            await pump;
            cts.Dispose();
        }
    }
}
