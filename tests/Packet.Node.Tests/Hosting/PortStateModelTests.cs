using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Packet.Core;
using Packet.Node.Core.Api;
using Packet.Node.Core.Configuration;
using Packet.Node.Core.Console;
using Packet.Node.Core.Hosting;
using Packet.Node.Tests.Support;

namespace Packet.Node.Tests.Hosting;

/// <summary>
/// The port state model (packet-net/packet.net#722). Before it, membership of the supervisor's
/// port dictionary WAS the state: present meant up, absent meant disabled / never-attempted /
/// faulted / mid-restart, indistinguishable, so a port that never came up and a port that died
/// on the air both read the same, <c>lastError</c> was permanently null, and a degraded port
/// (running with a dead radio) was structurally unrepresentable.
///
/// <para>These pin the transitions each path produces, that every transition a real path
/// produces is one the machine calls legal, and that the retry recovers a port of ANY transport
/// kind with no config edit.</para>
/// </summary>
[Trait("Category", "Node")]
public sealed class PortStateModelTests
{
    private static readonly Callsign NodeCall = new("NODE", 1);

    private static PortConfig Port(string id, int memPort, bool enabled = true) => new()
    {
        Id = id,
        Enabled = enabled,
        Transport = new KissTcpTransport { Host = "mem", Port = memPort },
        Ax25 = new Ax25PortParams { N2 = TestAx25Timing.NodeN2 },
    };

    private static string Endpoint(int memPort) => $"kiss-tcp:mem:{memPort}";

    private static NodeConfig Config(params PortConfig[] ports) => new()
    {
        Identity = new Identity { Callsign = NodeCall.ToString() },
        Ports = ports,
    };

    // Every transition a test observes, so each case can assert BOTH the path it cares about and
    // the standing invariant that no path invents an illegal move.
    private static List<PortStateChange> Record(PortSupervisor supervisor)
    {
        var seen = new List<PortStateChange>();
        supervisor.PortStateChanged += change =>
        {
            lock (seen)
            {
                seen.Add(change);
            }
        };
        return seen;
    }

    private static void AssertAllLegal(List<PortStateChange> seen)
    {
        lock (seen)
        {
            foreach (var c in seen)
            {
                PortStateMachine.IsLegal(c.From, c.To).Should().BeTrue(
                    "the supervisor moved port '{0}' {1} -> {2}, which the state machine calls illegal",
                    c.PortId, c.From, c.To);
            }
        }
    }

    [Fact]
    public async Task A_configured_port_goes_Configured_then_Starting_then_Up()
    {
        var bus = new SharedRadioBus();
        var config = new TestConfigProvider(Config(Port("a", 1)));
        var factory = new FakeTransportFactory().Provide(Endpoint(1), bus.Attach());

        await using var supervisor = new PortSupervisor(config, factory, TimeProvider.System, NullLoggerFactory.Instance);
        // Before StartAsync the port already has an entry, projected from config alone: the API
        // used to answer "faulted" for every port during boot.
        supervisor.GetHealth("a")!.State.Should().Be(PortState.Configured);
        supervisor.GetHealth("a")!.LastError.Should().BeNull();

        var seen = Record(supervisor);
        await supervisor.StartAsync();
        await Wait.ForAsync(() => supervisor.RunningPortIds.Contains("a"), "port a up");

        supervisor.GetHealth("a")!.State.Should().Be(PortState.Up);
        supervisor.GetHealth("a")!.Degraded.Should().BeEmpty();
        lock (seen)
        {
            seen.Select(c => c.To).Should().Equal(PortState.Starting, PortState.Up);
        }
        AssertAllLegal(seen);
    }

    [Fact]
    public async Task A_disabled_port_reads_Disabled_and_is_never_started()
    {
        var config = new TestConfigProvider(Config(Port("a", 1, enabled: false)));
        var factory = new FakeTransportFactory();

        await using var supervisor = new PortSupervisor(config, factory, TimeProvider.System, NullLoggerFactory.Instance);
        await supervisor.StartAsync();

        supervisor.GetHealth("a")!.State.Should().Be(PortState.Disabled);
        supervisor.RunningPortIds.Should().BeEmpty();
        supervisor.GetPort("a").Should().BeNull();
    }

    [Fact]
    public async Task A_transport_that_will_not_open_goes_Faulted_with_the_reason_then_Retrying_then_Up()
    {
        var clock = new FakeTimeProvider();
        var bus = new SharedRadioBus();
        var config = new TestConfigProvider(Config(Port("a", 1)));
        var factory = new FakeTransportFactory().Fault(Endpoint(1), new IOException("device busy"));

        await using var supervisor = new PortSupervisor(config, factory, clock, NullLoggerFactory.Instance);
        var seen = Record(supervisor);
        await supervisor.StartAsync();

        // Faulted, with WHY: the field that was hard-coded null on every surface before #722.
        var faulted = supervisor.GetHealth("a")!;
        faulted.LastError.Should().Be("device busy");
        // The retry arms immediately after the fault, so the settled state is Retrying.
        await Wait.ForAsync(() => supervisor.GetHealth("a")!.State == PortState.Retrying,
            "a failed bring-up arms the bounded-backoff retry");
        lock (seen)
        {
            seen.Select(c => c.To).Should().StartWith(new[] { PortState.Starting, PortState.Faulted, PortState.Retrying });
        }

        // A plain kiss-tcp port, NOT head-end-bound: the old policy only ever retried head-end
        // transports, so this port would have stayed down until a human edited config.
        factory.ClearFault(Endpoint(1)).Provide(Endpoint(1), bus.Attach());
        for (int i = 0; i < 40 && !supervisor.RunningPortIds.Contains("a"); i++)
        {
            clock.Advance(TimeSpan.FromSeconds(5));
            await Task.Delay(25);
        }

        await Wait.ForAsync(() => supervisor.RunningPortIds.Contains("a"),
            "the retry brings the port up with no config edit");
        supervisor.GetHealth("a")!.State.Should().Be(PortState.Up);
        AssertAllLegal(seen);
    }

    [Fact]
    public async Task A_port_whose_listener_dies_mid_run_is_detected_torn_down_and_restarted_with_no_config_edit()
    {
        var clock = new FakeTimeProvider();
        var bus = new SharedRadioBus();
        var dying = new KillableTransport(bus.Attach());
        // A LOCAL serial-kiss port on purpose: the networked kinds (kiss-tcp, nino-tnc-tcp) are
        // wrapped in the reconnect decorator and heal themselves, so the class of port with no
        // supervision at all was exactly this one - a USB TNC unplugged, or any local pump that
        // faults (the evidence's largest gap).
        var port = new PortConfig
        {
            Id = "a",
            Enabled = true,
            Transport = new SerialKissTransport { Device = "/dev/pty-a" },
            Ax25 = new Ax25PortParams { N2 = TestAx25Timing.NodeN2 },
        };
        var config = new TestConfigProvider(Config(port));
        var factory = new FakeTransportFactory()
            .Provide("serial-kiss:/dev/pty-a", dying, bus.Attach());

        await using var supervisor = new PortSupervisor(config, factory, clock, NullLoggerFactory.Instance);
        var seen = Record(supervisor);
        await supervisor.StartAsync();
        await Wait.ForAsync(() => supervisor.GetHealth("a")!.State == PortState.Up, "port a up");
        var first = supervisor.GetPort("a")!;

        // The USB TNC is unplugged / the pump faults: the listener marks itself not-running and
        // (before #722) NOTHING observed it; the port stayed green on /ports, in metrics and in
        // PORTS while being deaf and dumb on the air.
        dying.Kill();
        await Wait.ForAsync(() => !first.Listener.IsRunning, "the listener notices its pump died");

        // The watchdog notices within one supervision tick.
        for (int i = 0; i < 40 && supervisor.GetHealth("a")!.State == PortState.Up; i++)
        {
            clock.Advance(PortSupervisor.SupervisionInterval);
            await Task.Delay(25);
        }

        await Wait.ForAsync(() => supervisor.GetHealth("a")!.State is PortState.Faulted or PortState.Retrying,
            "the watchdog faults a port that died on the air");
        supervisor.GetHealth("a")!.LastError.Should().Be("listener stopped");
        first.IsAlive.Should().BeFalse("the dead port is torn down, and holders can see it");

        // ... and it comes back on the retry, with no config edit.
        for (int i = 0; i < 60 && !supervisor.RunningPortIds.Contains("a"); i++)
        {
            clock.Advance(TimeSpan.FromSeconds(5));
            await Task.Delay(25);
        }

        await Wait.ForAsync(() => supervisor.GetHealth("a")!.State == PortState.Up,
            "the retry rebuilds the port on a fresh transport");
        supervisor.GetPort("a").Should().NotBeSameAs(first, "the port was rebuilt, not resurrected");
        AssertAllLegal(seen);
    }

    [Fact]
    public async Task A_radio_that_will_not_open_leaves_the_port_Degraded_naming_the_component()
    {
        var bus = new SharedRadioBus();
        var port = Port("a", 1) with
        {
            Radio = new PortRadioConfig { Kind = "tait-ccdi", Port = "/dev/ttyUSB9", Baud = 28800 },
        };
        var config = new TestConfigProvider(Config(port));
        var factory = new FakeTransportFactory().Provide(Endpoint(1), bus.Attach());
        var radios = new FakeRadioControlFactory();
        radios.Fault(new IOException("no such device"));

        await using var supervisor = new PortSupervisor(
            config, factory, TimeProvider.System, NullLoggerFactory.Instance, radioFactory: radios);
        var seen = Record(supervisor);
        await supervisor.StartAsync();
        await Wait.ForAsync(() => supervisor.RunningPortIds.Contains("a"),
            "a dead radio must never take the packet channel down");

        var health = supervisor.GetHealth("a")!;
        health.State.Should().Be(PortState.Degraded, "the port serves, but with a piece missing");
        health.Degraded.Should().Equal(PortComponents.Radio);
        health.LastError.Should().Contain("no such device");
        health.IsServing.Should().BeTrue("a degraded port is still on the air; pdn_port_up stays 1");
        AssertAllLegal(seen);
    }

    [Fact]
    public async Task A_disable_then_enable_walks_Up_Stopping_Disabled_Starting_Up()
    {
        var bus = new SharedRadioBus();
        var before = Config(Port("a", 1));
        var config = new TestConfigProvider(before);
        var factory = new FakeTransportFactory()
            .Provide(Endpoint(1), bus.Attach(), bus.Attach());

        await using var supervisor = new PortSupervisor(config, factory, TimeProvider.System, NullLoggerFactory.Instance);
        await supervisor.StartAsync();
        await Wait.ForAsync(() => supervisor.RunningPortIds.Contains("a"), "port a up");

        var seen = Record(supervisor);
        var off = Config(Port("a", 1, enabled: false));
        config.Apply(off);
        await supervisor.ApplyAsync(ReconcilePlanner.Plan(before, off), off);
        supervisor.GetHealth("a")!.State.Should().Be(PortState.Disabled);

        var on = Config(Port("a", 1));
        config.Apply(on);
        await supervisor.ApplyAsync(ReconcilePlanner.Plan(off, on), on);
        await Wait.ForAsync(() => supervisor.GetHealth("a")!.State == PortState.Up, "the port comes back");

        lock (seen)
        {
            seen.Select(c => c.To).Should().Equal(
                PortState.Stopping, PortState.Disabled, PortState.Starting, PortState.Up);
        }
        AssertAllLegal(seen);
    }

    [Fact]
    public async Task Snapshot_covers_every_configured_port_in_config_order()
    {
        var bus = new SharedRadioBus();
        // Ids deliberately NOT in ordinal order, so "config order" is distinguishable from
        // "sorted by id" (the canonical ordering PC2 builds on).
        var cfg = Config(Port("vhf", 1), Port("hf", 2, enabled: false), Port("uhf", 3));
        var config = new TestConfigProvider(cfg);
        var factory = new FakeTransportFactory()
            .Provide(Endpoint(1), bus.Attach())
            .Fault(Endpoint(3), new IOException("nope"));

        await using var supervisor = new PortSupervisor(config, factory, TimeProvider.System, NullLoggerFactory.Instance);
        await supervisor.StartAsync();

        var snapshot = supervisor.Snapshot();
        snapshot.Select(h => h.Id).Should().Equal("vhf", "hf", "uhf");
        snapshot[0].State.Should().Be(PortState.Up);
        snapshot[1].State.Should().Be(PortState.Disabled);
        snapshot[2].IsServing.Should().BeFalse("the third port could not open its transport");
        supervisor.GetHealth("nosuchport").Should().BeNull();
    }

    [Fact]
    public void The_transition_table_is_reachable_and_terminal_free()
    {
        // Every state must be reachable from Configured, so no state in the model is a fiction,
        // and every state must have a way out, so no port can be permanently stuck.
        var reachable = new HashSet<PortState> { PortState.Configured };
        var queue = new Queue<PortState>([PortState.Configured]);
        while (queue.Count > 0)
        {
            foreach (var next in PortStateMachine.Next(queue.Dequeue()))
            {
                if (reachable.Add(next))
                {
                    queue.Enqueue(next);
                }
            }
        }

        reachable.Should().BeEquivalentTo(Enum.GetValues<PortState>());
        foreach (var state in Enum.GetValues<PortState>())
        {
            PortStateMachine.Next(state).Should().NotBeEmpty("port state {0} must have a way out", state);
            PortStateMachine.IsLegal(state, state).Should().BeTrue("re-asserting a state is always legal");
        }

        // The names are the wire contract (PortStatus.state, the SPA's union, the metrics label).
        PortStates.All.Should().Equal(
            "configured", "disabled", "starting", "up", "degraded", "faulted", "retrying", "stopping");
        Enum.GetValues<PortState>().Select(PortStates.Name).Should().Equal(PortStates.All);
    }

    [Fact]
    public async Task One_derivation_backs_the_API_the_console_and_the_metrics_state()
    {
        var bus = new SharedRadioBus();
        var cfg = Config(Port("vhf", 1), Port("hf", 2, enabled: false));
        var config = new TestConfigProvider(cfg);
        var factory = new FakeTransportFactory().Provide(Endpoint(1), bus.Attach());

        await using var supervisor = new PortSupervisor(config, factory, TimeProvider.System, NullLoggerFactory.Instance);
        await supervisor.StartAsync();
        await Wait.ForAsync(() => supervisor.RunningPortIds.Contains("vhf"), "port vhf up");

        // The API projection (what /ports, /ports/{id} and the metrics collector all consume).
        PortStatus[] api = PortStatusProjector.ProjectAll(supervisor, cfg.Ports, telemetry: null);
        api.Select(p => p.Id).Should().Equal("vhf", "hf");
        api[0].State.Should().Be(PortStates.Up);
        api[1].State.Should().Be(PortStates.Disabled);

        // The console PORTS verb, which used to derive [up]/[down] from the config's enabled
        // flag alone and never consulted the supervisor at all.
        var env = new NodeConsoleEnvironment(config, outboundConnector: null, portHealth: supervisor);
        var service = new NodeCommandService(env, NullLogger<NodeCommandService>.Instance, TimeProvider.System);
        var connection = new ScriptedConnection("M0LTE-7", NodeTransportKind.Ax25, ["PORTS", "B"]);
        await service.RunAsync(connection);

        foreach (var p in api)
        {
            connection.Text.Should().Contain($"{p.Id} [{p.State}]",
                "the console must print the same state the API serves");
            // And the state model itself is what both read.
            supervisor.GetHealth(p.Id)!.StateName.Should().Be(p.State);
        }
    }

    // Drives the command loop: each scripted line as its own CR-terminated read, then EOF.
    // (The same shape the MH / CAP console tests use.)
    private sealed class ScriptedConnection(string peerId, NodeTransportKind kind, string[] lines)
        : INodeConnection
    {
        private readonly StringBuilder output = new();
        private readonly TaskCompletionSource completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int read;

        public string Text => output.ToString();
        public string PeerId => peerId;
        public NodeTransportKind TransportKind => kind;
        public Task Completion => completion.Task;

        public ValueTask<ReadOnlyMemory<byte>> ReadAsync(CancellationToken cancellationToken = default)
        {
            if (read >= lines.Length)
            {
                completion.TrySetResult();
                return new ValueTask<ReadOnlyMemory<byte>>(ReadOnlyMemory<byte>.Empty);
            }
            var bytes = Encoding.UTF8.GetBytes(lines[read] + "\r");
            read++;
            return new ValueTask<ReadOnlyMemory<byte>>(bytes);
        }

        public ValueTask WriteAsync(ReadOnlyMemory<byte> bytes, CancellationToken cancellationToken = default)
        {
            output.Append(Encoding.UTF8.GetString(bytes.Span));
            return ValueTask.CompletedTask;
        }

        public ValueTask DisposeAsync()
        {
            completion.TrySetResult();
            return ValueTask.CompletedTask;
        }
    }
}
