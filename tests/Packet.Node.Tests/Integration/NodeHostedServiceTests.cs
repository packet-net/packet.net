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
/// session that reaches the prompt, then a live config edit reconciles — bringing
/// an added port up and tearing a removed one down — without dropping the
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

        // Now remove port b again — only b is torn down.
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
}
