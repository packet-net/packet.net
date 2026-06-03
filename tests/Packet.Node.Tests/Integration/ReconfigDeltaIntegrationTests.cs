using Microsoft.Extensions.Logging.Abstractions;
using Packet.Ax25.Session;
using Packet.Core;
using Packet.Node.Core.Configuration;
using Packet.Node.Core.Hosting;
using Packet.Node.Tests.Support;

namespace Packet.Node.Tests.Integration;

/// <summary>
/// The marquee invariant on a live <see cref="PortSupervisor"/>: a reconcile
/// disrupts exactly the ports whose restart-class fields changed; hot-class
/// changes restart nothing; a session on an untouched port survives with the
/// SAME object identity; idempotence holds. Real <c>Ax25Listener</c>s over the
/// in-memory radio.
/// </summary>
[Trait("Category", "Node")]
public sealed class ReconfigDeltaIntegrationTests
{
    private static readonly Callsign NodeCall = new("NODE", 1);
    private static readonly Callsign RemoteCall = new("REMOTE", 1);

    private static PortConfig Port(string id, int memPort, bool enabled = true,
        KissParams? kiss = null, Ax25PortParams? ax25 = null) => new()
    {
        Id = id,
        Enabled = enabled,
        Transport = new KissTcpTransport { Host = "mem", Port = memPort },
        Kiss = kiss,
        Ax25 = ax25,
    };

    private static string Endpoint(int memPort) => $"kiss-tcp:mem:{memPort}";

    private static NodeConfig Config(params PortConfig[] ports) => new()
    {
        Identity = new Identity { Callsign = NodeCall.ToString() },
        Ports = ports,
    };

    [Fact]
    public async Task Adding_a_port_brings_it_up_without_touching_the_existing_one()
    {
        var busA = new SharedRadioBus();
        var busB = new SharedRadioBus();
        var config = new TestConfigProvider(Config(Port("a", 1)));
        var factory = new FakeTransportFactory()
            .Provide(Endpoint(1), busA.Attach())
            .Provide(Endpoint(2), busB.Attach());

        await using var supervisor = new PortSupervisor(config, factory, TimeProvider.System, NullLoggerFactory.Instance);
        await supervisor.StartAsync();
        await Wait.ForAsync(() => supervisor.RunningPortIds.Contains("a"), "port a up");

        var listenerA = supervisor.GetPort("a")!.Listener;

        // Edit the config to ADD port b.
        var next = Config(Port("a", 1), Port("b", 2));
        config.Apply(next);
        await supervisor.ApplyAsync(ReconcilePlanner.Plan(Config(Port("a", 1)), next), next);

        supervisor.RunningPortIds.Should().BeEquivalentTo("a", "b");
        // Port a's listener object is the very same instance — it wasn't touched.
        supervisor.GetPort("a")!.Listener.Should().BeSameAs(listenerA, "adding b must not disturb a");
    }

    [Fact]
    public async Task Removing_a_port_tears_it_down_and_leaves_the_other_running()
    {
        var busA = new SharedRadioBus();
        var busB = new SharedRadioBus();
        var before = Config(Port("a", 1), Port("b", 2));
        var config = new TestConfigProvider(before);
        var factory = new FakeTransportFactory()
            .Provide(Endpoint(1), busA.Attach())
            .Provide(Endpoint(2), busB.Attach());

        await using var supervisor = new PortSupervisor(config, factory, TimeProvider.System, NullLoggerFactory.Instance);
        await supervisor.StartAsync();
        await Wait.ForAsync(() => supervisor.RunningPortIds.Count == 2, "both ports up");
        var listenerB = supervisor.GetPort("b")!.Listener;

        var after = Config(Port("b", 2));
        config.Apply(after);
        await supervisor.ApplyAsync(ReconcilePlanner.Plan(before, after), after);

        supervisor.RunningPortIds.Should().BeEquivalentTo("b");
        supervisor.GetPort("b")!.Listener.Should().BeSameAs(listenerB, "removing a must not disturb b");
    }

    [Fact]
    public async Task A_session_on_an_untouched_port_survives_a_reconcile_of_another_port_same_object_identity()
    {
        // Two ports. A peer is connected on port a. We add port b. The session on
        // port a must survive — the SAME Ax25Session object identity.
        var busA = new SharedRadioBus();
        var busB = new SharedRadioBus();
        var before = Config(Port("a", 1));
        var config = new TestConfigProvider(before);
        var factory = new FakeTransportFactory()
            .Provide(Endpoint(1), busA.Attach())
            .Provide(Endpoint(2), busB.Attach());

        await using var supervisor = new PortSupervisor(config, factory, TimeProvider.System, NullLoggerFactory.Instance);
        await supervisor.StartAsync();
        await Wait.ForAsync(() => supervisor.RunningPortIds.Contains("a"), "port a up");

        // Connect a remote into port a and capture its session object identity from
        // the node's listener.
        Ax25Session? nodeSideSession = null;
        supervisor.GetPort("a")!.Listener.SessionAccepted += (_, e) => nodeSideSession = e.Session;

        await using var remote = new RemoteStation(busA.Attach(), RemoteCall);
        await remote.StartAsync();
        await remote.ConnectAsync(NodeCall);
        await Wait.ForAsync(() => remote.Saw("Welcome"), "the session reached the prompt");
        await Wait.ForAsync(() => nodeSideSession is not null, "node observed the session");
        var sessionBefore = nodeSideSession!;
        sessionBefore.CurrentState.Should().Be("Connected");

        // Add an unrelated port b — a reconcile that must not touch port a.
        var after = Config(Port("a", 1), Port("b", 2));
        config.Apply(after);
        await supervisor.ApplyAsync(ReconcilePlanner.Plan(before, after), after);

        // Port a's listener AND the live session are the same objects, still Connected.
        nodeSideSession.Should().BeSameAs(sessionBefore, "the session object identity must survive an unrelated reconcile");
        sessionBefore.CurrentState.Should().Be("Connected", "the untouched port's session stays up");
        remote.CurrentState.Should().Be("Connected");
    }

    [Fact]
    public async Task Kiss_param_change_applies_live_without_restarting_the_port_or_dropping_the_session()
    {
        // An InMemoryRadio endpoint records the KISS params pushed to it, so we can
        // assert they were applied live. A single-peer pair is enough here.
        var (nodeModem, remoteModem) = InMemoryRadio.CreatePair();

        var before = Config(Port("a", 1, kiss: new KissParams { TxDelay = 10 }));
        var config = new TestConfigProvider(before);
        var factory = new FakeTransportFactory().Provide(Endpoint(1), nodeModem);

        await using var supervisor = new PortSupervisor(config, factory, TimeProvider.System, NullLoggerFactory.Instance);
        await supervisor.StartAsync();
        await Wait.ForAsync(() => supervisor.RunningPortIds.Contains("a"), "port up");
        nodeModem.Applied.TxDelay.Should().Be((byte)10, "initial KISS params applied on bring-up");
        var listenerBefore = supervisor.GetPort("a")!.Listener;

        // Connect a remote in so there's a live session to protect.
        await using var remote = new RemoteStation(remoteModem, RemoteCall);
        await remote.StartAsync();
        await remote.ConnectAsync(NodeCall);
        await Wait.ForAsync(() => remote.Saw("Welcome"), "session up");

        // Hot KISS change: TXDELAY 10 → 50.
        var after = Config(Port("a", 1, kiss: new KissParams { TxDelay = 50 }));
        config.Apply(after);
        await supervisor.ApplyAsync(ReconcilePlanner.Plan(before, after), after);

        nodeModem.Applied.TxDelay.Should().Be((byte)50, "the new KISS param applied live");
        supervisor.GetPort("a")!.Listener.Should().BeSameAs(listenerBefore, "a hot KISS change must not restart the port");
        remote.CurrentState.Should().Be("Connected", "the session survives a hot KISS change");
    }

    [Fact]
    public async Task Idempotent_reapply_of_the_same_config_is_a_noop()
    {
        var bus = new SharedRadioBus();
        var cfg = Config(Port("a", 1));
        var config = new TestConfigProvider(cfg);
        var factory = new FakeTransportFactory().Provide(Endpoint(1), bus.Attach());

        await using var supervisor = new PortSupervisor(config, factory, TimeProvider.System, NullLoggerFactory.Instance);
        await supervisor.StartAsync();
        await Wait.ForAsync(() => supervisor.RunningPortIds.Contains("a"), "port up");
        var listenerBefore = supervisor.GetPort("a")!.Listener;

        // Re-apply the identical config.
        var plan = ReconcilePlanner.Plan(cfg, cfg);
        plan.IsNoOp.Should().BeTrue();
        await supervisor.ApplyAsync(plan, cfg);

        supervisor.GetPort("a")!.Listener.Should().BeSameAs(listenerBefore, "an idempotent re-apply changes nothing");
    }

    [Fact]
    public async Task A_faulting_port_is_isolated_and_the_healthy_port_still_comes_up()
    {
        var bus = new SharedRadioBus();
        var config = new TestConfigProvider(Config(Port("good", 1), Port("bad", 2)));
        var factory = new FakeTransportFactory()
            .Provide(Endpoint(1), bus.Attach())
            .Fault(Endpoint(2));   // port "bad" can't open its transport

        await using var supervisor = new PortSupervisor(config, factory, TimeProvider.System, NullLoggerFactory.Instance);
        await supervisor.StartAsync();

        await Wait.ForAsync(() => supervisor.RunningPortIds.Contains("good"), "the healthy port comes up");
        supervisor.RunningPortIds.Should().NotContain("bad", "the faulting port is skipped, not fatal");
    }
}
