using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging.Abstractions;
using Packet.Ax25.Transport;
using Packet.Core;
using Packet.Node.Core.Configuration;
using Packet.Node.Core.Hosting;
using Packet.Node.Core.Rigs;
using Packet.Node.Tests.Support;

namespace Packet.Node.Tests.Integration;

/// <summary>
/// The two <see cref="PortSupervisor.ApplyAsync"/> branches nothing else reaches
/// (C072): the <b>node-wide reset</b> a callsign change triggers, and the
/// <b>single-port restart</b> a transport change triggers. Both are the disruptive
/// half of reconfiguration - they destroy and rebuild live objects - so both are
/// pinned by object identity (what is the SAME instance afterwards, what is a NEW
/// one), by disposal of exactly the instances that were replaced, and by the
/// behaviour on the air either side of the apply.
/// </summary>
[Trait("Category", "Node")]
public sealed class SupervisorApplyBranchTests
{
    private static readonly Callsign OldCall = new("NODE", 1);
    private static readonly Callsign NewCall = new("NODE", 2);
    private static readonly Callsign RemoteCall = new("REMOTE", 1);
    private static readonly Callsign SecondCall = new("REMOTE", 2);
    private static readonly Callsign AppCall = new("APP", 3);

    private static PortConfig Port(
        string id, string device, PortRigConfig? rig = null, PortRadioConfig? radio = null) => new()
        {
            Id = id,
            Enabled = true,
            Transport = new SerialKissTransport { Device = device },
            Rig = rig,
            Radio = radio,
            // Bounded connect budget - the in-memory channel is instant (#47).
            Ax25 = new Ax25PortParams { N2 = TestAx25Timing.NodeN2 },
        };

    private static string Endpoint(string device) => $"serial-kiss:{device}";

    private static PortRigConfig Rig(int port) =>
        new() { Kind = "hamlib", Host = "127.0.0.1", Port = port };

    private static PortRadioConfig Radio(string device) =>
        new() { Kind = "tait-ccdi", Port = device, Baud = 28800 };

    private static NodeConfig Config(Callsign call, params PortConfig[] ports) => new()
    {
        Identity = new Identity { Callsign = call.ToString() },
        Ports = ports,
    };

    // ---- the node-wide reset branch (identity callsign changed) ---------------------

    [Fact]
    public async Task A_callsign_change_recreates_every_listener_under_the_new_call_and_disposes_the_old_ones()
    {
        var busA = new SharedRadioBus();
        var busB = new SharedRadioBus();
        var transportA1 = new TrackedTransport(busA.Attach());
        var transportA2 = new TrackedTransport(busA.Attach());
        var transportB1 = new TrackedTransport(busB.Attach());
        var transportB2 = new TrackedTransport(busB.Attach());

        // Port a carries a rig AND a radio so the reset's blast radius covers every
        // per-port object the supervisor owns, not just the listener.
        var before = Config(OldCall,
            Port("a", "/dev/pty-a", rig: Rig(4532), radio: Radio("/dev/ttyUSB0")),
            Port("b", "/dev/pty-b"));
        var config = new TestConfigProvider(before);
        var transports = new FakeTransportFactory()
            .Provide(Endpoint("/dev/pty-a"), transportA1, transportA2)
            .Provide(Endpoint("/dev/pty-b"), transportB1, transportB2);
        var rig1 = new FakeRigControl(name: "rig-1");
        var rig2 = new FakeRigControl(name: "rig-2");
        var rigs = new FakeRigControlFactory().Provide(rig1, rig2);
        var radio1 = new FakeRadioControl(name: "radio-1");
        var radio2 = new FakeRadioControl(name: "radio-2");
        var radios = new FakeRadioControlFactory().Provide(radio1, radio2);

        await using var supervisor = new PortSupervisor(
            config, transports, TimeProvider.System, NullLoggerFactory.Instance,
            radioFactory: radios, rigFactory: rigs, rigTelemetry: new RigTelemetry());
        await supervisor.StartAsync();
        await Wait.ForAsync(() => supervisor.RunningPortIds.Count == 2, "both ports up");

        var portA = supervisor.GetPort("a")!;
        var portB = supervisor.GetPort("b")!;
        var listenerA = portA.Listener;
        var listenerB = portB.Listener;
        var rigStatusA = portA.RigStatus;
        listenerA.MyCall.Should().Be(OldCall);
        listenerB.MyCall.Should().Be(OldCall);
        portA.Rig.Should().BeSameAs(rig1);
        portA.Radio.Should().BeSameAs(radio1);
        portA.ModemTransport.Should().BeSameAs(transportA1,
            "an RSSI-capable radio wraps the modem, so the modem chain is what the port keeps");

        var after = Config(NewCall,
            Port("a", "/dev/pty-a", rig: Rig(4532), radio: Radio("/dev/ttyUSB0")),
            Port("b", "/dev/pty-b"));
        var plan = ReconcilePlanner.Plan(before, after);
        plan.NodeWideReset.Should().BeTrue("an identity callsign change is the node-wide reset class");
        plan.ToRestart.Should().BeEmpty("the reset subsumes every per-port class");

        config.Apply(after);
        await supervisor.ApplyAsync(plan, after);

        // Every port is back up, and every one of them is a NEW stack under the new call.
        supervisor.RunningPortIds.Should().BeEquivalentTo("a", "b");
        var newA = supervisor.GetPort("a")!;
        var newB = supervisor.GetPort("b")!;
        newA.Should().NotBeSameAs(portA, "a node-wide reset rebuilds the RunningPort, it does not rebaseline it");
        newB.Should().NotBeSameAs(portB, "port b's own config did not change, but a callsign reset recreates it too");
        newA.Listener.Should().NotBeSameAs(listenerA);
        newB.Listener.Should().NotBeSameAs(listenerB, "EVERY listener is recreated, not only the changed ports'");
        newA.Listener.MyCall.Should().Be(NewCall, "the new MyCall is in force on port a");
        newB.Listener.MyCall.Should().Be(NewCall, "the new MyCall is in force on port b");

        // ... and the old ones are disposed, not merely dropped on the floor.
        listenerA.IsRunning.Should().BeFalse("the old listener was stopped");
        listenerB.IsRunning.Should().BeFalse();
        var sendOnOldListener = async () => await listenerA.SendUiAsync(RemoteCall, new byte[] { 0x01 });
        await sendOnOldListener.Should().ThrowAsync<ObjectDisposedException>(
            "the reset disposed the old listener - a leaked one would still be keying the radio");
        transportA1.Disposed.Should().BeTrue("the old modem chain went with its listener");
        transportB1.Disposed.Should().BeTrue();
        rig1.Disposed.Should().BeTrue("the old rig connection was closed");
        radio1.Disposed.Should().BeTrue("the old radio control channel was closed");

        newA.ModemTransport.Should().BeSameAs(transportA2, "port a re-opened its transport");
        newB.Transport.Should().BeSameAs(transportB2);
        newA.Rig.Should().BeSameAs(rig2);
        newA.Radio.Should().BeSameAs(radio2);
        newA.RigStatus.Should().NotBeNull("the rebuilt port re-attached its rig poller");
        newA.RigStatus.Should().NotBeSameAs(rigStatusA);
        transportA2.Disposed.Should().BeFalse("the replacement stack is live, not torn down");
        rig2.Disposed.Should().BeFalse();
        radio2.Disposed.Should().BeFalse();

        // On the air: the OLD call answers nothing. Dial it first and wait until that SABM
        // has demonstrably reached the rebuilt port's transport; then dial the NEW call and
        // watch that one reach the prompt. Frame ordering on the shared bus is the proof -
        // anything the node was going to say to the stale dial would have been said before
        // it answered the later one.
        await using var stale = new RemoteStation(busA.Attach(), SecondCall);
        await stale.StartAsync();
        using var staleCts = new CancellationTokenSource();
        int deliveredBeforeStale = transportA2.InboundFrames;
        var staleDial = Task.Run(async () =>
        {
            try
            {
                await stale.ConnectAsync(OldCall, staleCts.Token);
                return "connected";
            }
            catch (OperationCanceledException)
            {
                return "still waiting when the test cancelled it";
            }
            catch (TimeoutException)
            {
                return "gave up unanswered";
            }
        }, CancellationToken.None);
        await Wait.ForAsync(() => transportA2.InboundFrames > deliveredBeforeStale,
            "the stale SABM for the old call reached the rebuilt port");

        await using var remote = new RemoteStation(busA.Attach(), RemoteCall);
        await remote.StartAsync();
        await remote.ConnectAsync(NewCall);
        await Wait.ForAsync(() => remote.Saw("Welcome"), "the rebuilt port answers on the NEW call");

        await staleCts.CancelAsync();
        var staleOutcome = await staleDial;
        staleOutcome.Should().NotBe("connected", "nothing answers the old callsign once the reset has run");
    }

    // ---- the single-port restart branch (transport changed on one port) --------------

    [Fact]
    public async Task A_transport_change_on_one_port_rebuilds_only_that_ports_transport_rig_and_radio()
    {
        var busA = new SharedRadioBus();
        var busB = new SharedRadioBus();
        var transportA1 = new TrackedTransport(busA.Attach());
        var transportA2 = new TrackedTransport(busA.Attach());   // the NEW device, same channel
        var transportB1 = new TrackedTransport(busB.Attach());

        var before = Config(OldCall,
            Port("a", "/dev/pty-a", rig: Rig(4532), radio: Radio("/dev/ttyUSB0")),
            Port("b", "/dev/pty-b", rig: Rig(4533), radio: Radio("/dev/ttyUSB1")));
        var config = new TestConfigProvider(before);
        var transports = new FakeTransportFactory()
            .Provide(Endpoint("/dev/pty-a"), transportA1)
            .Provide(Endpoint("/dev/pty-a2"), transportA2)
            .Provide(Endpoint("/dev/pty-b"), transportB1);
        // Handed out in bring-up order: a, b on start; a again on the restart.
        var rigA1 = new FakeRigControl(name: "rig-a1");
        var rigB1 = new FakeRigControl(name: "rig-b1");
        var rigA2 = new FakeRigControl(name: "rig-a2");
        var rigs = new FakeRigControlFactory().Provide(rigA1, rigB1, rigA2);
        var radioA1 = new FakeRadioControl(name: "radio-a1");
        var radioB1 = new FakeRadioControl(name: "radio-b1");
        var radioA2 = new FakeRadioControl(name: "radio-a2");
        var radios = new FakeRadioControlFactory().Provide(radioA1, radioB1, radioA2);

        await using var supervisor = new PortSupervisor(
            config, transports, TimeProvider.System, NullLoggerFactory.Instance,
            radioFactory: radios, rigFactory: rigs, rigTelemetry: new RigTelemetry());
        await supervisor.StartAsync();
        await Wait.ForAsync(() => supervisor.RunningPortIds.Count == 2, "both ports up");

        var portA = supervisor.GetPort("a")!;
        var portB = supervisor.GetPort("b")!;
        var listenerA = portA.Listener;
        var listenerB = portB.Listener;
        var rigStatusB = portB.RigStatus;
        var radioStatusB = portB.RadioStatus;
        portA.Rig.Should().BeSameAs(rigA1);
        portB.Rig.Should().BeSameAs(rigB1);

        // An RHP-attached app callsign bound to port a: the restart must leave the fresh
        // listener answering for it (bring-up re-applies the live registrations).
        var appHandled = false;
        using var registration = supervisor.RegisterAppCallsign(AppCall, "a", (_, _) =>
        {
            Volatile.Write(ref appHandled, true);
            return Task.CompletedTask;
        });

        // Re-point port a's KISS device: a transport change, i.e. a single-port restart.
        var after = Config(OldCall,
            Port("a", "/dev/pty-a2", rig: Rig(4532), radio: Radio("/dev/ttyUSB0")),
            Port("b", "/dev/pty-b", rig: Rig(4533), radio: Radio("/dev/ttyUSB1")));
        var plan = ReconcilePlanner.Plan(before, after);
        plan.NodeWideReset.Should().BeFalse();
        plan.ToRestart.Should().ContainSingle().Which.Id.Should().Be("a");
        plan.ToBringUp.Should().BeEmpty();
        plan.ToTearDown.Should().BeEmpty();

        config.Apply(after);
        await supervisor.ApplyAsync(plan, after);

        supervisor.RunningPortIds.Should().BeEquivalentTo("a", "b");

        // Port b is untouched, down to the object identity of everything it owns.
        var newB = supervisor.GetPort("b")!;
        newB.Should().BeSameAs(portB, "restarting a must not rebuild b's RunningPort");
        newB.Listener.Should().BeSameAs(listenerB);
        newB.ModemTransport.Should().BeSameAs(transportB1);
        newB.Rig.Should().BeSameAs(rigB1);
        newB.RigStatus.Should().BeSameAs(rigStatusB);
        newB.Radio.Should().BeSameAs(radioB1);
        newB.RadioStatus.Should().BeSameAs(radioStatusB);
        transportB1.Disposed.Should().BeFalse();
        rigB1.Disposed.Should().BeFalse("b's rig connection was never touched");
        radioB1.Disposed.Should().BeFalse();
        listenerB.IsRunning.Should().BeTrue();

        // Port a is a new stack, and exactly the replaced objects were disposed.
        var newA = supervisor.GetPort("a")!;
        newA.Should().NotBeSameAs(portA);
        newA.Listener.Should().NotBeSameAs(listenerA);
        newA.ModemTransport.Should().BeSameAs(transportA2, "the restart opened the NEW device");
        newA.Rig.Should().BeSameAs(rigA2, "a restart re-dials the rig");
        newA.Radio.Should().BeSameAs(radioA2, "a restart re-opens the radio control channel");
        newA.RigStatus.Should().NotBeNull();
        newA.RadioStatus.Should().NotBeNull();
        supervisor.GetPortConfig("a")!.Transport.Should().Be(new SerialKissTransport { Device = "/dev/pty-a2" });
        listenerA.IsRunning.Should().BeFalse();
        transportA1.Disposed.Should().BeTrue();
        rigA1.Disposed.Should().BeTrue();
        radioA1.Disposed.Should().BeTrue();

        rigs.Requests.Should().HaveCount(3, "a dialled twice (start + restart), b once");
        radios.Requests.Should().HaveCount(3);

        // The rebuilt port carries traffic on its own call AND on the app alias it
        // inherited from the registration that outlived the restart.
        await using var remote = new RemoteStation(busA.Attach(), RemoteCall);
        await remote.StartAsync();
        await remote.ConnectAsync(OldCall);
        await Wait.ForAsync(() => remote.Saw("Welcome"), "the restarted port reaches the prompt");

        await using var app = new RemoteStation(busA.Attach(), SecondCall);
        await app.StartAsync();
        await app.ConnectAsync(AppCall);
        await Wait.ForAsync(() => Volatile.Read(ref appHandled),
            "the restarted port's fresh listener still answers for the registered app callsign");
    }

    // ---- two-phase apply: all teardowns, THEN all bring-ups ------------------------

    [Fact]
    public async Task Swapping_one_device_between_two_ports_works_because_every_teardown_precedes_every_bring_up()
    {
        // The hazard (#722): restart used to be per port, down-then-up, in CONFIG order. An
        // operator moving one serial device from port a to port b writes a legal, validated
        // edit (uniqueness is checked on the TARGET config only) - and a's bring-up then dialled
        // the device b was still holding. The failure landed as a per-port fault with no retry,
        // so a valid edit left a port permanently down.
        var busA = new SharedRadioBus();
        var busB = new SharedRadioBus();
        var before = Config(OldCall, Port("a", "/dev/pty-a"), Port("b", "/dev/pty-b"));
        var config = new TestConfigProvider(before);
        var transports = new FakeTransportFactory()
            // Both devices are exclusive: the second opener gets "already open", exactly like a
            // real serial port.
            .Exclusive(Endpoint("/dev/pty-a"))
            .Exclusive(Endpoint("/dev/pty-b"))
            .Provide(Endpoint("/dev/pty-a"), busA.Attach(), busA.Attach())
            .Provide(Endpoint("/dev/pty-b"), busB.Attach(), busB.Attach());

        await using var supervisor = new PortSupervisor(
            config, transports, TimeProvider.System, NullLoggerFactory.Instance);
        await supervisor.StartAsync();
        await Wait.ForAsync(() => supervisor.RunningPortIds.Count == 2, "both ports up");

        // The swap: a takes b's device and b takes a's.
        var after = Config(OldCall, Port("a", "/dev/pty-b"), Port("b", "/dev/pty-a"));
        var plan = ReconcilePlanner.Plan(before, after);
        // Both ports are restart-class, in config order - which is what used to collide.
        plan.ToRestart.Select(p => p.Id).Should().Equal(["a", "b"]);

        config.Apply(after);
        await supervisor.ApplyAsync(plan, after);

        supervisor.RunningPortIds.Should().BeEquivalentTo(["a", "b"],
            "with all teardowns before any bring-up, neither port dials a device the other still holds");
        supervisor.GetHealth("a")!.State.Should().Be(PortState.Up);
        supervisor.GetHealth("b")!.State.Should().Be(PortState.Up);
        supervisor.GetPortConfig("a")!.Transport.Should().Be(new SerialKissTransport { Device = "/dev/pty-b" });
        supervisor.GetPortConfig("b")!.Transport.Should().Be(new SerialKissTransport { Device = "/dev/pty-a" });
    }

    /// <summary>
    /// A transport that records whether it was disposed and how many frames it delivered
    /// inbound - the two things a rebuild test needs, and which the shared in-memory
    /// endpoints (deliberately dispose-inert) cannot report.
    /// </summary>
    private sealed class TrackedTransport(IAx25Transport inner) : IAx25Transport
    {
        private int disposed;
        private int inboundFrames;

        /// <summary>True once the supervisor disposed this transport.</summary>
        public bool Disposed => Volatile.Read(ref disposed) != 0;

        /// <summary>How many frames this transport has handed up to its listener.</summary>
        public int InboundFrames => Volatile.Read(ref inboundFrames);

        public Task SendAsync(ReadOnlyMemory<byte> ax25, CancellationToken cancellationToken = default) =>
            inner.SendAsync(ax25, cancellationToken);

        public async IAsyncEnumerable<Ax25InboundFrame> ReceiveAsync(
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await foreach (var frame in inner.ReceiveAsync(cancellationToken).ConfigureAwait(false))
            {
                Interlocked.Increment(ref inboundFrames);
                yield return frame;
            }
        }

        public async ValueTask DisposeAsync()
        {
            Interlocked.Exchange(ref disposed, 1);
            await inner.DisposeAsync().ConfigureAwait(false);
        }
    }
}
