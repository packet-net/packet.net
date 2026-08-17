using Microsoft.Extensions.Logging.Abstractions;
using Packet.Core;
using Packet.Node.Core.Configuration;
using Packet.Node.Core.Console;
using Packet.Node.Core.Hosting;
using Packet.Node.Tests.Support;

namespace Packet.Node.Tests.Integration;

/// <summary>
/// The cross-port keys and the one canonical port ordering (packet-net/packet.net#723, port
/// concept PC2). Everything here is a MULTI-PORT behaviour that the previous node-wide keys got
/// wrong and no test could see, so each case runs a real two- or three-port
/// <see cref="PortSupervisor"/> over separate in-memory RF channels and asserts on the air.
/// </summary>
/// <remarks>
/// The port ids are deliberately chosen so <b>configuration order and id order disagree</b>
/// (<c>vhf</c> before <c>hf</c>): every earlier multi-port test used <c>a</c>/<c>b</c> or
/// <c>alpha</c>/<c>bravo</c>, where the two orderings coincide, which is exactly why three
/// different "which port" answers could coexist unnoticed.
/// </remarks>
[Trait("Category", "Node")]
public sealed class PortConceptCrossPortTests
{
    private static readonly Callsign NodeCall = new("NODE", 1);
    private static readonly Callsign RemoteCall = new("REMOTE", 1);
    private static readonly Callsign AppCall = new("DAPPS", 7);

    private static PortConfig Port(string id, string device) => new()
    {
        Id = id,
        Enabled = true,
        Transport = new SerialKissTransport { Device = device },
        Ax25 = new Ax25PortParams { N2 = TestAx25Timing.NodeN2 },
    };

    private static NodeConfig Config(Callsign call, params PortConfig[] ports) => new()
    {
        Identity = new Identity { Callsign = call.ToString(), Alias = "TESTNODE" },
        Ports = ports,
    };

    private static string Endpoint(string device) => $"serial-kiss:{device}";

    // ── item 1: the outbound-dial claim is keyed by (port, remote) ───────────────────

    /// <summary>
    /// The claim used to be <c>Dictionary&lt;Callsign, int&gt;</c> node-wide, and
    /// <c>OnSessionAccepted</c> tested it without looking at the arrival port. So while the node
    /// was dialling a station on one port, that same station calling IN on another port was
    /// accepted by the engine - UA sent, link up - and then dropped on the floor: no console, no
    /// app, no log, dead air until T3/DISC. A dial holds its claim for up to (N2+1)xT1V, so the
    /// window is tens of seconds wide.
    /// </summary>
    [Fact]
    public async Task A_dial_out_on_one_port_does_not_swallow_the_same_callsign_calling_in_on_another()
    {
        var vhfBus = new SharedRadioBus();
        var hfBus = new SharedRadioBus();
        var before = Config(NodeCall, Port("vhf", "/dev/pty-vhf"), Port("hf", "/dev/pty-hf"));
        var config = new TestConfigProvider(before);
        var transports = new FakeTransportFactory()
            .Provide(Endpoint("/dev/pty-vhf"), vhfBus.Attach())
            .Provide(Endpoint("/dev/pty-hf"), hfBus.Attach());

        await using var supervisor = new PortSupervisor(
            config, transports, TimeProvider.System, NullLoggerFactory.Instance);
        await supervisor.StartAsync();
        await Wait.ForAsync(() => supervisor.RunningPortIds.Count == 2, "both ports up");

        // Dial REMOTE-1 on vhf. Nothing answers on that channel, so the dial stays in flight
        // (and the claim held) for the whole (N2+1)xT1V budget - far longer than this test.
        using var dialCts = new CancellationTokenSource();
        var dial = Task.Run(async () =>
        {
            try
            {
                await supervisor.ResolveConnector("vhf")!.ConnectAsync(RemoteCall, dialCts.Token);
            }
            catch
            {
                // The dial never completes; it is cancelled in the finally below.
            }
        });

        // The SAME callsign now calls in on hf. It must reach the node console.
        await using var caller = new RemoteStation(hfBus.Attach(), RemoteCall);
        await caller.StartAsync();
        await caller.ConnectAsync(NodeCall);

        try
        {
            await Wait.ForAsync(() => caller.Saw("Welcome"),
                "a caller on hf gets the node console even while vhf is dialling the same callsign");
        }
        finally
        {
            await dialCts.CancelAsync();
            await dial;
        }
    }

    // ── item 2: the app-callsign registry is keyed by (callsign, port scope) ─────────

    /// <summary>
    /// Two apps, one callsign, two ports - ordinary multi-port practice (a local BBS on VHF, a
    /// gateway BBS on HF) that the callsign-only registry made impossible, and whose routing half
    /// was never asserted: <c>OnAppSessionAccepted</c> resolved by <c>Local</c> alone and never
    /// compared the arrival port it had just computed on the next line.
    /// </summary>
    [Fact]
    public async Task The_same_app_callsign_on_two_ports_routes_each_caller_to_its_own_port_s_app()
    {
        var vhfBus = new SharedRadioBus();
        var hfBus = new SharedRadioBus();
        var config = new TestConfigProvider(
            Config(NodeCall, Port("vhf", "/dev/pty-vhf"), Port("hf", "/dev/pty-hf")));
        var transports = new FakeTransportFactory()
            .Provide(Endpoint("/dev/pty-vhf"), vhfBus.Attach())
            .Provide(Endpoint("/dev/pty-hf"), hfBus.Attach());

        await using var supervisor = new PortSupervisor(
            config, transports, TimeProvider.System, NullLoggerFactory.Instance);
        await supervisor.StartAsync();
        await Wait.ForAsync(() => supervisor.RunningPortIds.Count == 2, "both ports up");

        var onVhf = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var onHf = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var vhfApp = supervisor.RegisterAppCallsign(AppCall, "vhf", async (conn, portId) =>
        {
            onVhf.TrySetResult(portId);
            await conn.WriteAsync("VHF-APP>\r"u8.ToArray());
        });
        using var hfApp = supervisor.RegisterAppCallsign(AppCall, "hf", async (conn, portId) =>
        {
            onHf.TrySetResult(portId);
            await conn.WriteAsync("HF-APP>\r"u8.ToArray());
        });

        await using var vhfCaller = new RemoteStation(vhfBus.Attach(), RemoteCall);
        await vhfCaller.StartAsync();
        await vhfCaller.ConnectAsync(AppCall);
        (await onVhf.Task.WaitAsync(TimeSpan.FromSeconds(20))).Should().Be("vhf");
        await Wait.ForAsync(() => vhfCaller.Saw("VHF-APP>"), "the VHF caller reached the VHF app");
        onHf.Task.IsCompleted.Should().BeFalse("the HF app must not see a caller who arrived on VHF");

        await using var hfCaller = new RemoteStation(hfBus.Attach(), new Callsign("REMOTE", 2));
        await hfCaller.StartAsync();
        await hfCaller.ConnectAsync(AppCall);
        (await onHf.Task.WaitAsync(TimeSpan.FromSeconds(20))).Should().Be("hf");
        await Wait.ForAsync(() => hfCaller.Saw("HF-APP>"), "the HF caller reached the HF app");
    }

    /// <summary>
    /// The negative the suite never had: an app bound to ONE port must not answer a caller who
    /// arrived on another - and must not fall through to the node console either. The listener's
    /// alias filter is the first line (the other port never aliases the callsign at all), so the
    /// caller gets no answer whatsoever, which is the correct on-air behaviour for a callsign
    /// this node does not answer for on this channel.
    /// </summary>
    [Fact]
    public async Task An_app_bound_to_one_port_does_not_answer_on_another()
    {
        var vhfBus = new SharedRadioBus();
        var hfBus = new SharedRadioBus();
        var config = new TestConfigProvider(
            Config(NodeCall, Port("vhf", "/dev/pty-vhf"), Port("hf", "/dev/pty-hf")));
        var transports = new FakeTransportFactory()
            .Provide(Endpoint("/dev/pty-vhf"), vhfBus.Attach())
            .Provide(Endpoint("/dev/pty-hf"), hfBus.Attach());

        await using var supervisor = new PortSupervisor(
            config, transports, TimeProvider.System, NullLoggerFactory.Instance);
        await supervisor.StartAsync();
        await Wait.ForAsync(() => supervisor.RunningPortIds.Count == 2, "both ports up");

        int answered = 0;
        using var vhfOnly = supervisor.RegisterAppCallsign(AppCall, "vhf", (_, _) =>
        {
            Interlocked.Increment(ref answered);
            return Task.CompletedTask;
        });

        await using var wrongPort = new RemoteStation(hfBus.Attach(), RemoteCall);
        await wrongPort.StartAsync();

        // Bound so the test does not sit out the full (N2+1)xT1V connect budget.
        using var attempt = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        try
        {
            await wrongPort.ConnectAsync(AppCall, attempt.Token);
        }
        catch (Exception ex) when (ex is OperationCanceledException or TimeoutException or InvalidOperationException)
        {
            // Expected: nothing on hf answers for this callsign.
        }

        Volatile.Read(ref answered).Should().Be(0, "the app is bound to vhf, and the caller arrived on hf");
        wrongPort.Saw("Welcome").Should().BeFalse("and it must not silently fall through to the node console");
        wrongPort.CurrentState.Should().NotBe("Connected");
    }

    /// <summary>
    /// The scope rule, stated and enforced: a wildcard bind claims every port, so it conflicts
    /// with any other registration of that callsign in either order; two per-port binds conflict
    /// only when they name the same port.
    /// </summary>
    [Fact]
    public async Task A_wildcard_bind_claims_every_port_and_conflicts_in_both_directions()
    {
        var bus = new SharedRadioBus();
        var config = new TestConfigProvider(Config(NodeCall, Port("vhf", "/dev/pty-vhf")));
        var transports = new FakeTransportFactory().Provide(Endpoint("/dev/pty-vhf"), bus.Attach());
        await using var supervisor = new PortSupervisor(
            config, transports, TimeProvider.System, NullLoggerFactory.Instance);
        await supervisor.StartAsync();

        // wildcard first, then a port-scoped bind of the same callsign
        using (supervisor.RegisterAppCallsign(AppCall, portId: null, (_, _) => Task.CompletedTask))
        {
            var scopedAfterWildcard = () => supervisor.RegisterAppCallsign(AppCall, "vhf", (_, _) => Task.CompletedTask);
            scopedAfterWildcard.Should().Throw<InvalidOperationException>()
                .WithMessage("*every port*");
        }

        // port-scoped first, then a wildcard - and a DIFFERENT port, which is legal
        using var onVhf = supervisor.RegisterAppCallsign(AppCall, "vhf", (_, _) => Task.CompletedTask);
        var wildcardAfterScoped = () => supervisor.RegisterAppCallsign(AppCall, portId: null, (_, _) => Task.CompletedTask);
        wildcardAfterScoped.Should().Throw<InvalidOperationException>().WithMessage("*port 'vhf'*");

        var sameAgain = () => supervisor.RegisterAppCallsign(AppCall, "vhf", (_, _) => Task.CompletedTask);
        sameAgain.Should().Throw<InvalidOperationException>();

        using var onHf = supervisor.RegisterAppCallsign(AppCall, "hf", (_, _) => Task.CompletedTask);
        onHf.Should().NotBeNull("the same callsign on a DIFFERENT port is the whole point");
    }

    /// <summary>
    /// Renaming the node onto an SSID an application had already bound used to reroute every
    /// connect for that app to the node prompt, silently: the node-wide reset rebuilt each
    /// listener under the new <c>MyCall</c> and <c>OnSessionAccepted</c>'s split sent the app's
    /// traffic down the console branch. The apply is refused whole, nothing moves, and the app
    /// keeps answering.
    /// </summary>
    [Fact]
    public async Task An_identity_change_onto_a_bound_app_callsign_is_refused_and_nothing_is_applied()
    {
        var bus = new SharedRadioBus();
        var before = Config(NodeCall, Port("vhf", "/dev/pty-vhf"));
        var config = new TestConfigProvider(before);
        var transports = new FakeTransportFactory()
            .Provide(Endpoint("/dev/pty-vhf"), bus.Attach(), bus.Attach());

        await using var supervisor = new PortSupervisor(
            config, transports, TimeProvider.System, NullLoggerFactory.Instance);
        await supervisor.StartAsync();
        await Wait.ForAsync(() => supervisor.RunningPortIds.Contains("vhf"), "port up");

        var reachedApp = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var app = supervisor.RegisterAppCallsign(AppCall, portId: null, async (conn, _) =>
        {
            reachedApp.TrySetResult();
            await conn.WriteAsync("APP>\r"u8.ToArray());
        });

        var after = Config(AppCall, Port("vhf", "/dev/pty-vhf"));   // the node takes the app's call
        supervisor.LiveApplyConflicts(after).Should().ContainSingle()
            .Which.Should().Contain("already bound as an application callsign");

        var plan = ReconcilePlanner.Plan(before, after);
        plan.NodeWideReset.Should().BeTrue();

        var outcome = await supervisor.ApplyAsync(plan, after);

        outcome.WasRefused.Should().BeTrue();
        outcome.Refusals.Should().ContainSingle();
        supervisor.GetPort("vhf")!.Listener.MyCall.Should().Be(NodeCall, "nothing was applied");

        // And the app is still the one answering for its callsign, on the air.
        await using var caller = new RemoteStation(bus.Attach(), RemoteCall);
        await caller.StartAsync();
        await caller.ConnectAsync(AppCall);
        await reachedApp.Task.WaitAsync(TimeSpan.FromSeconds(20));
        await Wait.ForAsync(() => caller.Saw("APP>"), "the app kept its callsign");
        caller.Saw("Welcome").Should().BeFalse("the node console never took it over");
    }

    /// <summary>
    /// A rename plans as remove-then-add (the id IS the reconcile key), so a registration scoped
    /// to the old id would name a port that no longer exists and quietly stop answering. The
    /// registration follows the port.
    /// </summary>
    [Fact]
    public async Task Renaming_a_port_re_scopes_the_app_callsigns_bound_to_it()
    {
        var bus = new SharedRadioBus();
        var before = Config(NodeCall, Port("vhf", "/dev/pty-vhf"));
        var config = new TestConfigProvider(before);
        var transports = new FakeTransportFactory()
            .Provide(Endpoint("/dev/pty-vhf"), bus.Attach(), bus.Attach());

        await using var supervisor = new PortSupervisor(
            config, transports, TimeProvider.System, NullLoggerFactory.Instance);
        await supervisor.StartAsync();
        await Wait.ForAsync(() => supervisor.RunningPortIds.Contains("vhf"), "port up");

        var arrival = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var app = supervisor.RegisterAppCallsign(AppCall, "vhf", async (conn, portId) =>
        {
            arrival.TrySetResult(portId);
            await conn.WriteAsync("APP>\r"u8.ToArray());
        });

        // Same device, new id: a rename.
        var after = Config(NodeCall, Port("2m", "/dev/pty-vhf"));
        var plan = ReconcilePlanner.Plan(before, after);
        plan.ToTearDown.Should().Equal("vhf");
        plan.ToBringUp.Should().ContainSingle().Which.Id.Should().Be("2m");

        config.Apply(after);
        (await supervisor.ApplyAsync(plan, after)).WasRefused.Should().BeFalse();
        await Wait.ForAsync(() => supervisor.RunningPortIds.Contains("2m"), "the renamed port is up");

        await using var caller = new RemoteStation(bus.Attach(), RemoteCall);
        await caller.StartAsync();
        await caller.ConnectAsync(AppCall);

        (await arrival.Task.WaitAsync(TimeSpan.FromSeconds(20))).Should().Be("2m",
            "the registration followed the rename instead of being orphaned on the old id");
    }

    // ── item 3: one canonical port ordering = configuration order ────────────────────

    /// <summary>
    /// Configuration order is the ONE ordering. With ids whose string order disagrees with it
    /// (<c>vhf</c> then <c>hf</c>), a bare <c>C</c> used to leave on <c>hf</c> (ordinal-first)
    /// while <c>C 1</c> left on <c>vhf</c> (config-first) and <c>RunningPortIds</c> answered in
    /// dictionary order - three different answers to "which port".
    /// </summary>
    [Fact]
    public async Task Config_order_is_the_one_port_ordering_even_when_ids_sort_the_other_way()
    {
        var vhfBus = new SharedRadioBus();
        var hfBus = new SharedRadioBus();
        var before = Config(NodeCall, Port("vhf", "/dev/pty-vhf"), Port("hf", "/dev/pty-hf"));
        var config = new TestConfigProvider(before);
        var transports = new FakeTransportFactory()
            .Provide(Endpoint("/dev/pty-vhf"), vhfBus.Attach())
            .Provide(Endpoint("/dev/pty-hf"), hfBus.Attach());

        await using var supervisor = new PortSupervisor(
            config, transports, TimeProvider.System, NullLoggerFactory.Instance);
        await supervisor.StartAsync();
        await Wait.ForAsync(() => supervisor.RunningPortIds.Count == 2, "both ports up");

        supervisor.RunningPortIds.Should().Equal("vhf", "hf");
        supervisor.Snapshot().Select(h => h.Id).Should().Equal("vhf", "hf");

        // The default connector (a bare `C`, POST /sessions with no portId, the telnet console).
        supervisor.ResolveDefaultConnector()!.PortId.Should().Be("vhf",
            "the default is the first ENABLED and SERVING port in config order, not the first by id");

        // The console's explicit 1-indexed port, which was already config order.
        var router = supervisor.CreateConnectRouter(defaultConnector: null);
        var inbound = new DriveableConnection(RemoteCall.ToString(), NodeTransportKind.Ax25);
        router.Resolve(port: 1, RemoteCall, inbound).Connector!.PortId.Should().Be("vhf");
        router.Resolve(port: 2, RemoteCall, inbound).Connector!.PortId.Should().Be("hf");
    }

    /// <summary>
    /// "First in config order" means the first one actually SERVING: a disabled (or faulted)
    /// first port hands the default to the next one along, rather than leaving the node with no
    /// way out.
    /// </summary>
    [Fact]
    public async Task The_default_connector_skips_a_port_that_is_not_serving()
    {
        var vhfBus = new SharedRadioBus();
        var hfBus = new SharedRadioBus();
        var before = Config(NodeCall, Port("vhf", "/dev/pty-vhf"), Port("hf", "/dev/pty-hf"));
        var config = new TestConfigProvider(before);
        var transports = new FakeTransportFactory()
            .Provide(Endpoint("/dev/pty-vhf"), vhfBus.Attach())
            .Provide(Endpoint("/dev/pty-hf"), hfBus.Attach());

        await using var supervisor = new PortSupervisor(
            config, transports, TimeProvider.System, NullLoggerFactory.Instance);
        await supervisor.StartAsync();
        await Wait.ForAsync(() => supervisor.RunningPortIds.Count == 2, "both ports up");

        var after = Config(NodeCall, Port("vhf", "/dev/pty-vhf") with { Enabled = false }, Port("hf", "/dev/pty-hf"));
        config.Apply(after);
        await supervisor.ApplyAsync(ReconcilePlanner.Plan(before, after), after);

        supervisor.RunningPortIds.Should().Equal("hf");
        supervisor.ResolveDefaultConnector()!.PortId.Should().Be("hf");
    }
}
