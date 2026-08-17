using Microsoft.Extensions.Logging.Abstractions;
using Packet.Ax25.Session;
using Packet.Core;
using Packet.Node.Api;
using Packet.Node.Core.Configuration;
using Packet.Node.Core.Hosting;
using Packet.Node.Tests.Support;

namespace Packet.Node.Tests.Integration;

/// <summary>
/// What <c>/status</c>, <c>/ports</c> and <c>/sessions</c> actually report (review items C052
/// and C063, #694).
/// </summary>
/// <remarks>
/// <para>
/// <b>C052.</b> <c>Ax25Listener.ActiveSessions</c> is an engine CACHE: a disconnected peer stays
/// in it (state <c>Disconnected</c>) until LRU eviction at 64 peers, so a returning peer resumes
/// against the same state machine. Projecting it unfiltered made the dashboard's "Active
/// sessions" climb forever and left dead rows in <c>GET /sessions</c> after a successful
/// <c>DELETE</c>. The engine keeps its cache; the API view is filtered to live states.
/// </para>
/// <para>
/// <b>C063.</b> A session row's uptime/bytes/last-activity come from the <c>(port, peer)</c>
/// telemetry LINK, which spans reconnects. That is the documented contract (see
/// <c>SessionInfo</c>), so this pins it rather than pretending the counters are per-circuit.
/// </para>
/// </remarks>
[Trait("Category", "Node")]
public sealed class SessionProjectionTests
{
    private static readonly Callsign NodeCall = new("NODE", 1);
    private static readonly Callsign RemoteCall = new("REMOTE", 1);

    private static PortConfig Port() => new()
    {
        Id = "a",
        Enabled = true,
        Transport = new KissTcpTransport { Host = "mem", Port = 4001 },
        Ax25 = new Ax25PortParams { N2 = TestAx25Timing.NodeN2 },
    };

    private static NodeConfig Config() => new()
    {
        Identity = new Identity { Callsign = NodeCall.ToString(), Alias = "HOSTNODE" },
        Management = new ManagementConfig { Telnet = new TelnetConfig { Enabled = false } },
        Ports = [Port()],
    };

    [Fact]
    public async Task A_disconnected_peer_stays_in_the_engine_cache_but_leaves_the_api_view()
    {
        var bus = new SharedRadioBus();
        var config = new TestConfigProvider(Config());
        var factory = new FakeTransportFactory().Provide("kiss-tcp:mem:4001", bus.Attach());

        using var host = new NodeHostedService(config, factory, TimeProvider.System, NullLoggerFactory.Instance);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        await host.StartAsync(cts.Token);
        await Wait.ForAsync(() => host.Supervisor?.RunningPortIds.Contains("a") == true, "port a comes up");

        var listener = host.Supervisor!.GetPort("a")!.Listener;

        await using (var remote = new RemoteStation(bus.Attach(), RemoteCall))
        {
            await remote.StartAsync();
            await remote.ConnectAsync(NodeCall, cts.Token);
            await Wait.ForAsync(() => remote.Saw("HOSTNODE"), "the inbound session reaches the prompt");

            // Live: the session shows up in all three projections.
            PdnReadApi.BuildSessions(host, TimeProvider.System).Should().ContainSingle()
                .Which.Peer.Should().Be(RemoteCall.ToString());
            PdnReadApi.BuildStatus(host, config, TimeProvider.System, traffic: null).SessionCount.Should().Be(1);
            PdnReadApi.BuildPorts(host, config).Should().ContainSingle()
                .Which.SessionCount.Should().Be(1);

            // Tear the link down the way DELETE /sessions/{id} does.
            listener.ActiveSessions.Should().ContainSingle().Which.PostEvent(new DlDisconnectRequest());
            await Wait.ForAsync(
                () => listener.ActiveSessions.All(s => s.CurrentState == "Disconnected"),
                "the node-side session reaches Disconnected");
        }

        // The engine still caches the peer (that is deliberate) ...
        listener.ActiveSessions.Should().ContainSingle()
            .Which.CurrentState.Should().Be("Disconnected");

        // ... but nothing an operator reads counts it any more.
        PdnReadApi.BuildSessions(host, TimeProvider.System).Should().BeEmpty("a dead circuit is not an active session");
        PdnReadApi.BuildStatus(host, config, TimeProvider.System, traffic: null).SessionCount.Should().Be(0);
        PdnReadApi.BuildPorts(host, config).Should().ContainSingle().Which.SessionCount.Should().Be(0);

        await host.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task A_reconnecting_peers_row_carries_the_link_lifetime_counters()
    {
        // C063: uptime/bytes/lastActivity are per-(port, peer) LINK figures, so they survive a
        // session ending and a new one starting. Documented on SessionInfo + node-api.yaml +
        // types.ts rather than silently implied to be per-circuit; this test is what pins it.
        var bus = new SharedRadioBus();
        var config = new TestConfigProvider(Config());
        var factory = new FakeTransportFactory().Provide("kiss-tcp:mem:4001", bus.Attach());

        using var host = new NodeHostedService(config, factory, TimeProvider.System, NullLoggerFactory.Instance);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        await host.StartAsync(cts.Token);
        await Wait.ForAsync(() => host.Supervisor?.RunningPortIds.Contains("a") == true, "port a comes up");
        var listener = host.Supervisor!.GetPort("a")!.Listener;

        long bytesOutFirstSession;
        await using (var first = new RemoteStation(bus.Attach(), RemoteCall))
        {
            await first.StartAsync();
            await first.ConnectAsync(NodeCall, cts.Token);
            await Wait.ForAsync(() => first.Saw("HOSTNODE"), "the first session reaches the prompt");

            bytesOutFirstSession = PdnReadApi.BuildSessions(host, TimeProvider.System).Single().BytesOut;
            bytesOutFirstSession.Should().BeGreaterThan(0, "the node sent this peer a banner");

            listener.ActiveSessions.Single().PostEvent(new DlDisconnectRequest());
            await Wait.ForAsync(
                () => listener.ActiveSessions.All(s => s.CurrentState == "Disconnected"), "first session ends");
        }

        await using var second = new RemoteStation(bus.Attach(), RemoteCall);
        await second.StartAsync();
        await second.ConnectAsync(NodeCall, cts.Token);
        await Wait.ForAsync(() => second.Saw("HOSTNODE"), "the peer reconnects and reaches the prompt again");

        var row = PdnReadApi.BuildSessions(host, TimeProvider.System).Should().ContainSingle().Subject;
        row.State.Should().Be("Connected");
        row.BytesOut.Should().BeGreaterThanOrEqualTo(bytesOutFirstSession,
            "the byte counters are the (port, peer) link's, which spans reconnects - see SessionInfo's remarks");

        await host.StopAsync(CancellationToken.None);
    }
}
