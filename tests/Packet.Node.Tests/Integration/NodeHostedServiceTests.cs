using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Packet.Core;
using Packet.Node.Core.Configuration;
using Packet.Node.Core.Hosting;
using Packet.Node.Tests.Support;

namespace Packet.Node.Tests.Integration;

/// <summary>
/// The host component test (exit criterion v / vi): the
/// <see cref="NodeHostedService"/> boots, brings up a port, accepts an inbound
/// session that reaches the prompt, then a live config edit reconciles - bringing
/// an added port up and tearing a removed one down - without dropping the
/// unrelated session. Drives the hosted service through its internal reconcile
/// entry point so the test is deterministic (no debounce / semaphore race) and
/// uses a <see cref="FakeTimeProvider"/> as the clock seam.
/// </summary>
[Trait("Category", "Node")]
public sealed class NodeHostedServiceTests
{
    private static readonly Callsign NodeCall = new("NODE", 1);
    private static readonly Callsign RemoteCall = new("REMOTE", 1);

    private static PortConfig Port(string id, int memPort) => new()
    {
        Id = id,
        Enabled = true,
        Transport = new KissTcpTransport { Host = "mem", Port = memPort },
        // Small N2 bounds the connect backstop at 30 s instead of the 66 s spec
        // default under CI load; T1 stays spec default (#47).
        Ax25 = new Ax25PortParams { N2 = TestAx25Timing.NodeN2 },
    };

    private static string Endpoint(int memPort) => $"kiss-tcp:mem:{memPort}";

    private static NodeConfig Config(params PortConfig[] ports) => new()
    {
        Identity = new Identity { Callsign = NodeCall.ToString(), Alias = "HOSTNODE" },
        // Telnet on an ephemeral port so the host's telnet listener binds without
        // clashing; the AX.25 path is what this test exercises.
        Management = new ManagementConfig { Telnet = new TelnetConfig { Enabled = true, Bind = "127.0.0.1", Port = 0 } },
        Ports = ports,
    };

    [Fact]
    public async Task Boots_brings_up_a_port_reaches_the_prompt_then_a_live_edit_reconciles_without_dropping_the_session()
    {
        var busA = new SharedRadioBus();
        var busB = new SharedRadioBus();
        var time = new FakeTimeProvider();

        var config = new TestConfigProvider(Config(Port("a", 1)));
        var factory = new FakeTransportFactory()
            .Provide(Endpoint(1), busA.Attach())
            .Provide(Endpoint(2), busB.Attach());

        using var host = new NodeHostedService(config, factory, time, NullLoggerFactory.Instance);

        // Boot the hosted service (StartAsync runs ExecuteAsync to its first await).
        using var hostCts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await host.StartAsync(hostCts.Token);

        await Wait.ForAsync(() => host.Supervisor?.RunningPortIds.Contains("a") == true, "port a comes up on boot");

        // Inbound connect reaches the prompt.
        await using var remote = new RemoteStation(busA.Attach(), RemoteCall);
        await remote.StartAsync();
        await remote.ConnectAsync(NodeCall);
        await Wait.ForAsync(() => remote.Saw("HOSTNODE"), "inbound session reaches the prompt");

        // Capture the live session's identity on the node side.
        var supervisor = host.Supervisor!;
        var sessionBefore = supervisor.GetPort("a")!.Listener;

        // Live config edit: add port b, keep a. The hosted service is the sole
        // OnChange subscriber; drive its reconcile deterministically.
        var next = Config(Port("a", 1), Port("b", 2));
        config.Apply(next);
        await host.ReconcileOnceAsync(hostCts.Token);

        await Wait.ForAsync(() => supervisor.RunningPortIds.Contains("b"), "the added port comes up on reconcile");
        supervisor.RunningPortIds.Should().BeEquivalentTo("a", "b");
        supervisor.GetPort("a")!.Listener.Should().BeSameAs(sessionBefore, "port a untouched by adding b");
        remote.CurrentState.Should().Be("Connected", "the inbound session survives the reconcile");

        // Now remove port b again - only b is torn down.
        var shrink = Config(Port("a", 1));
        config.Apply(shrink);
        await host.ReconcileOnceAsync(hostCts.Token);
        await Wait.ForAsync(() => !supervisor.RunningPortIds.Contains("b"), "removed port torn down");
        supervisor.RunningPortIds.Should().BeEquivalentTo("a");
        remote.CurrentState.Should().Be("Connected", "removing b still leaves a's session up");

        await host.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task An_invalid_edit_never_reaches_the_host_so_the_running_node_is_unaffected()
    {
        // This pins exit criterion (vi) at the host level: the provider rejects an
        // invalid candidate atomically (tested directly in FileConfigProviderTests),
        // so the host's OnChange never fires and Current never advances. Here we
        // assert the partner half: if Current does NOT change, a reconcile is a
        // no-op and nothing is disturbed.
        var bus = new SharedRadioBus();
        var time = new FakeTimeProvider();
        var config = new TestConfigProvider(Config(Port("a", 1)));
        var factory = new FakeTransportFactory().Provide(Endpoint(1), bus.Attach());

        using var host = new NodeHostedService(config, factory, time, NullLoggerFactory.Instance);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await host.StartAsync(cts.Token);
        await Wait.ForAsync(() => host.Supervisor?.RunningPortIds.Contains("a") == true, "port up");
        var listenerBefore = host.Supervisor!.GetPort("a")!.Listener;

        // No config change applied (an invalid edit would have been rejected by the
        // provider and never surfaced) → reconcile is a no-op.
        await host.ReconcileOnceAsync(cts.Token);

        host.Supervisor!.GetPort("a")!.Listener.Should().BeSameAs(listenerBefore, "an unchanged Current leaves the node untouched");
        await host.StopAsync(CancellationToken.None);
    }

    // ---- the telnet restart branch (C072) -------------------------------------------

    private static NodeConfig ConfigWithTelnet(TelnetConfig telnet, params PortConfig[] ports) => new()
    {
        Identity = new Identity { Callsign = NodeCall.ToString(), Alias = "HOSTNODE" },
        Management = new ManagementConfig { Telnet = telnet },
        Ports = ports,
    };

    /// <summary>
    /// C072: <c>plan.TelnetChanged</c> reaches <c>RestartTelnetAsync</c>, which is the ONLY
    /// thing a telnet-only edit may disturb. Both binds are ephemeral (port 0) so the test
    /// never races another process for a fixed port.
    /// </summary>
    [Fact]
    public async Task A_telnet_bind_change_restarts_only_telnet_and_leaves_the_ports_and_their_session_alone()
    {
        var bus = new SharedRadioBus();
        var time = new FakeTimeProvider();
        var loopbackTelnet = new TelnetConfig { Enabled = true, Bind = "127.0.0.1", Port = 0 };
        var before = ConfigWithTelnet(loopbackTelnet, Port("a", 1));
        var config = new TestConfigProvider(before);
        var factory = new FakeTransportFactory().Provide(Endpoint(1), bus.Attach());

        using var host = new NodeHostedService(config, factory, time, NullLoggerFactory.Instance);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await host.StartAsync(cts.Token);
        await Wait.ForAsync(() => host.Supervisor?.RunningPortIds.Contains("a") == true, "port a up");
        await Wait.ForAsync(() => host.Telnet?.BoundEndpoint is not null, "telnet bound on boot");

        var telnetBefore = host.Telnet!;
        telnetBefore.BoundEndpoint!.Address.Should().Be(IPAddress.Loopback);
        var portBefore = host.Supervisor!.GetPort("a")!;
        var listenerBefore = portBefore.Listener;

        // A live inbound AX.25 session that a telnet restart has no business touching.
        await using var remote = new RemoteStation(bus.Attach(), RemoteCall);
        await remote.StartAsync();
        await remote.ConnectAsync(NodeCall);
        await Wait.ForAsync(() => remote.Saw("HOSTNODE"), "inbound session reaches the prompt");

        // Re-bind telnet to every interface: a telnet-only edit, so the port planner has
        // nothing to do but the host still has work.
        var after = ConfigWithTelnet(loopbackTelnet with { Bind = "0.0.0.0" }, Port("a", 1));
        var plan = ReconcilePlanner.Plan(before, after);
        plan.TelnetChanged.Should().BeTrue();
        plan.IsNoOp.Should().BeFalse("a telnet re-bind is host work even with an empty port delta");
        plan.NodeWideReset.Should().BeFalse();
        plan.ToRestart.Should().BeEmpty();
        plan.ToBringUp.Should().BeEmpty();
        plan.ToTearDown.Should().BeEmpty();

        // Let the host's OWN reconcile worker run this edit - it is the sole OnChange
        // subscriber, so applying the config already schedules exactly one pass. Driving a
        // second one in parallel would race it: RestartTelnetAsync leaves Telnet null between
        // disposing the old listener and binding the new one, and a reader that lands in that
        // window sees no console at all.
        config.Apply(after);
        await Wait.ForAsync(
            () => host.Telnet is { BoundEndpoint: not null } t && !ReferenceEquals(t, telnetBefore),
            "the telnet listener was restarted on the new bind");

        // Telnet is a NEW listener, on the newly-configured bind, and it really serves.
        var boundAfter = host.Telnet!.BoundEndpoint!;
        boundAfter.Address.Should().Be(IPAddress.Any, "the restart honoured the new bind address");
        using var probe = new TcpClient();
        var dialNew = async () => await probe.ConnectAsync(IPAddress.Loopback, boundAfter.Port, cts.Token);
        await dialNew.Should().NotThrowAsync("the re-bound telnet listener accepts connections");

        // The port set is untouched, down to object identity, and the session is still up.
        host.Supervisor!.GetPort("a").Should().BeSameAs(portBefore, "a telnet edit must not touch the port set");
        host.Supervisor!.GetPort("a")!.Listener.Should().BeSameAs(listenerBefore);
        remote.CurrentState.Should().Be("Connected", "the AX.25 session survives a telnet restart");

        await host.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Disabling_telnet_closes_its_socket_and_still_leaves_the_ports_alone()
    {
        var bus = new SharedRadioBus();
        var time = new FakeTimeProvider();
        var telnetOn = new TelnetConfig { Enabled = true, Bind = "127.0.0.1", Port = 0 };
        var before = ConfigWithTelnet(telnetOn, Port("a", 1));
        var config = new TestConfigProvider(before);
        var factory = new FakeTransportFactory().Provide(Endpoint(1), bus.Attach());

        using var host = new NodeHostedService(config, factory, time, NullLoggerFactory.Instance);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await host.StartAsync(cts.Token);
        await Wait.ForAsync(() => host.Supervisor?.RunningPortIds.Contains("a") == true, "port a up");
        await Wait.ForAsync(() => host.Telnet?.BoundEndpoint is not null, "telnet bound on boot");
        var boundBefore = host.Telnet!.BoundEndpoint!;
        var listenerBefore = host.Supervisor!.GetPort("a")!.Listener;

        var after = ConfigWithTelnet(telnetOn with { Enabled = false }, Port("a", 1));
        ReconcilePlanner.Plan(before, after).TelnetChanged.Should().BeTrue();
        config.Apply(after);
        await Wait.ForAsync(() => host.Telnet is null, "the host's reconcile worker stopped the telnet console");

        host.Telnet.Should().BeNull("a disabled telnet console leaves no listener behind");
        using var probe = new TcpClient();
        var dialOld = async () => await probe.ConnectAsync(boundBefore.Address, boundBefore.Port, cts.Token);
        await dialOld.Should().ThrowAsync<SocketException>(
            "the old listen socket was closed, not merely forgotten");

        host.Supervisor!.GetPort("a")!.Listener.Should().BeSameAs(listenerBefore, "the ports are none of telnet's business");
        await host.StopAsync(CancellationToken.None);
    }
}
