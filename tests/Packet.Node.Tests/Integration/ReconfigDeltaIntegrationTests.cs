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
    private static readonly Callsign SecondCall = new("REMOTE", 2);

    private static PortConfig Port(string id, int memPort, bool enabled = true,
        KissParams? kiss = null, Ax25PortParams? ax25 = null) => new()
    {
        Id = id,
        Enabled = enabled,
        Transport = new KissTcpTransport { Host = "mem", Port = memPort },
        Kiss = kiss,
        // Default to a bounded connect budget (small N2; the in-memory channel is
        // instant) so a starved handshake can't burn the 66 s spec default under CI
        // load (#47); a test that cares about the AX.25 params passes them explicitly.
        Ax25 = ax25 ?? new Ax25PortParams { N2 = TestAx25Timing.NodeN2 },
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
        nodeModem.Applied.TxTail.Should().Be((byte)0,
            "TXTAIL has an implicit-0 default and is sent on bring-up even when txTail is unset (#465)");
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
    public async Task Tx_tail_is_sent_to_the_modem_on_bring_up_even_with_no_kiss_block()
    {
        // #465: TXTAIL defaults to an implicit 0 sent to the modem UNCONDITIONALLY on
        // bring-up — even for a port that sets no KISS params at all (no kiss block).
        var (nodeModem, _) = InMemoryRadio.CreatePair();

        var config = new TestConfigProvider(Config(Port("a", 1)));   // no kiss block
        var factory = new FakeTransportFactory().Provide(Endpoint(1), nodeModem);

        await using var supervisor = new PortSupervisor(config, factory, TimeProvider.System, NullLoggerFactory.Instance);
        await supervisor.StartAsync();
        await Wait.ForAsync(() => supervisor.RunningPortIds.Contains("a"), "port up");

        nodeModem.Applied.TxTailSendCount.Should().BeGreaterThanOrEqualTo(1,
            "TXTAIL is sent on bring-up regardless of whether the port sets any KISS params");
        nodeModem.Applied.TxTail.Should().Be((byte)0, "the implicit default is 0");
    }

    [Fact]
    public async Task A_non_zero_tx_tail_override_is_sent_to_the_modem_and_re_applied_on_change()
    {
        // #465: the per-port non-zero override (software-modem / latency-audio-path
        // configs) is sent to the modem, and a hot change re-applies it live.
        var (nodeModem, remoteModem) = InMemoryRadio.CreatePair();

        var before = Config(Port("a", 1, kiss: new KissParams { TxTail = 5 }));
        var config = new TestConfigProvider(before);
        var factory = new FakeTransportFactory().Provide(Endpoint(1), nodeModem);

        await using var supervisor = new PortSupervisor(config, factory, TimeProvider.System, NullLoggerFactory.Instance);
        await supervisor.StartAsync();
        await Wait.ForAsync(() => supervisor.RunningPortIds.Contains("a"), "port up");
        nodeModem.Applied.TxTail.Should().Be((byte)5, "the explicit non-zero TX tail override is sent on bring-up");

        await using var remote = new RemoteStation(remoteModem, RemoteCall);
        await remote.StartAsync();
        await remote.ConnectAsync(NodeCall);
        await Wait.ForAsync(() => remote.Saw("Welcome"), "session up");

        // Hot change: override 5 → 8.
        var after = Config(Port("a", 1, kiss: new KissParams { TxTail = 8 }));
        config.Apply(after);
        await supervisor.ApplyAsync(ReconcilePlanner.Plan(before, after), after);

        nodeModem.Applied.TxTail.Should().Be((byte)8, "the new TX tail override is applied live");
        remote.CurrentState.Should().Be("Connected", "the session survives a hot TX-tail change");
    }

    [Fact]
    public async Task Ax25_param_change_reseeds_live_new_sessions_pick_it_up_existing_session_survives()
    {
        // The slice-1 follow-up: an AX.25-params-only change is HOT (live-reseed),
        // not restart-class. The marquee invariant, EXTENDED to the *changed* port:
        // the listener keeps its identity, the existing session keeps its object
        // identity AND stays Connected, and a NEW session built after the change
        // picks up the new params.
        var nodeBus = new SharedRadioBus();
        var nodeModem = nodeBus.Attach();

        var before = Config(Port("a", 1, ax25: new Ax25PortParams { N2 = 7, T1Ms = 4000 }));
        var config = new TestConfigProvider(before);
        var factory = new FakeTransportFactory().Provide(Endpoint(1), nodeModem);

        await using var supervisor = new PortSupervisor(config, factory, TimeProvider.System, NullLoggerFactory.Instance);
        await supervisor.StartAsync();
        await Wait.ForAsync(() => supervisor.RunningPortIds.Contains("a"), "port up");

        var listenerBefore = supervisor.GetPort("a")!.Listener;
        listenerBefore.CurrentSessionParameters.N2.Should().Be(7, "initial AX.25 params seeded on bring-up");
        listenerBefore.CurrentSessionParameters.T1V.Should().Be(TimeSpan.FromMilliseconds(4000));

        // Capture the node-side session object for the FIRST peer.
        Ax25Session? firstNodeSession = null;
        listenerBefore.SessionAccepted += (_, e) => firstNodeSession = e.Session;

        await using var first = new RemoteStation(nodeBus.Attach(), RemoteCall);
        await first.StartAsync();
        await first.ConnectAsync(NodeCall);
        await Wait.ForAsync(() => first.Saw("Welcome"), "first session up");
        await Wait.ForAsync(() => firstNodeSession is not null, "node observed the first session");
        var firstSessionBefore = firstNodeSession!;
        firstSessionBefore.CurrentState.Should().Be("Connected");
        firstSessionBefore.Context.N2.Should().Be(7, "the first session was built with the OLD params");

        // Live AX.25 reseed: N2 7 → 12, T1 4000 → 8000 ms.
        var after = Config(Port("a", 1, ax25: new Ax25PortParams { N2 = 12, T1Ms = 8000 }));
        config.Apply(after);
        await supervisor.ApplyAsync(ReconcilePlanner.Plan(before, after), after);

        // Invariant: the listener AND the live session keep their object identity,
        // and the session is still Connected — the reseed did NOT restart the port
        // or drop the session ON THE CHANGED PORT.
        supervisor.GetPort("a")!.Listener.Should().BeSameAs(listenerBefore, "an AX.25 reseed must not restart the port");
        firstNodeSession.Should().BeSameAs(firstSessionBefore, "the live session object identity survives the reseed");
        firstSessionBefore.CurrentState.Should().Be("Connected", "the existing session is not dropped");
        first.CurrentState.Should().Be("Connected");
        firstSessionBefore.Context.N2.Should().Be(7, "the EXISTING session keeps the params it was built with");

        // The listener's live parameters now reflect the new config.
        listenerBefore.CurrentSessionParameters.N2.Should().Be(12, "the reseed updated the live params");
        listenerBefore.CurrentSessionParameters.T1V.Should().Be(TimeSpan.FromMilliseconds(8000));

        // A NEW peer that connects in after the reseed gets a session built with the
        // NEW params (object-distinct from the first, with the new N2).
        Ax25Session? secondNodeSession = null;
        listenerBefore.SessionAccepted += (_, e) =>
        {
            if (!ReferenceEquals(e.Session, firstSessionBefore)) secondNodeSession = e.Session;
        };

        await using var second = new RemoteStation(nodeBus.Attach(), SecondCall);
        await second.StartAsync();
        await second.ConnectAsync(NodeCall);
        await Wait.ForAsync(() => second.Saw("Welcome"), "second session up");
        await Wait.ForAsync(() => secondNodeSession is not null, "node observed the second session");

        secondNodeSession!.Should().NotBeSameAs(firstSessionBefore, "a fresh peer gets a fresh session");
        secondNodeSession.Context.N2.Should().Be(12, "the NEW session picked up the reseeded N2");
        // The first session is still alive and unchanged through all of this.
        firstSessionBefore.CurrentState.Should().Be("Connected", "the original QSO survives a new peer connecting under new params");
    }

    [Fact]
    public async Task Compat_change_reseeds_live_without_restarting_the_port()
    {
        // A compat-only change is HOT (#366): the listener keeps its identity and
        // its live parameter record now carries the resolved parse options +
        // session quirks. (The behavioural halves — parse options gating the very
        // next inbound frame, quirks seeding the next-built session — are pinned
        // at the listener layer in Ax25ListenerCompatTests.)
        var bus = new SharedRadioBus();
        var before = Config(Port("a", 1));
        var config = new TestConfigProvider(before);
        var factory = new FakeTransportFactory().Provide(Endpoint(1), bus.Attach());

        await using var supervisor = new PortSupervisor(config, factory, TimeProvider.System, NullLoggerFactory.Instance);
        await supervisor.StartAsync();
        await Wait.ForAsync(() => supervisor.RunningPortIds.Contains("a"), "port up");

        var listenerBefore = supervisor.GetPort("a")!.Listener;
        listenerBefore.CurrentSessionParameters.ParseOptions.Should().Be(Ax25ParseOptions.Lenient,
            "no compat block = the historical lenient default");
        listenerBefore.CurrentSessionParameters.Quirks.Should().BeSameAs(Ax25SessionQuirks.Default);

        var after = Config(Port("a", 1) with
        {
            Compat = new PortCompatConfig { Preset = "strict", Quirks = "strictly-faithful" },
        });
        config.Apply(after);
        await supervisor.ApplyAsync(ReconcilePlanner.Plan(before, after), after);

        supervisor.GetPort("a")!.Listener.Should().BeSameAs(listenerBefore,
            "a compat reseed must not restart the port");
        listenerBefore.CurrentSessionParameters.ParseOptions.Should().Be(Ax25ParseOptions.Strict,
            "the reseed resolved and applied the new preset");
        listenerBefore.CurrentSessionParameters.Quirks.Should().BeSameAs(Ax25SessionQuirks.StrictlyFaithful);
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
