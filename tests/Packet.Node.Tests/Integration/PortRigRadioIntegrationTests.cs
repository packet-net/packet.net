using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Packet.Ax25;
using Packet.Ax25.Session;
using Packet.Core;
using Packet.Node.Core.Configuration;
using Packet.Node.Core.Hosting;
using Packet.Node.Core.Telemetry;
using Packet.Node.Tests.Support;
using M0LTE.Radio;
using M0LTE.Rig;

namespace Packet.Node.Tests.Integration;

/// <summary>
/// The <c>radio: kind rig</c> slice (plan OQ-011) on a live <see cref="PortSupervisor"/>: a port
/// with a <c>rig:</c> block can ALSO re-present that rig as its radio - the node dials a SECOND,
/// dedicated connection to the same daemon and wraps it in an owning
/// <see cref="RigRadioControl"/>, so hardware DCD gates the listener's CSMA (and calibrated
/// signal strength RSSI-tags inbound frames) with any transport, the headline case being a
/// kiss-tcp soundmodem beside rigctld. The rig-status poller keeps its own connection
/// undisturbed; a DCD-only rig skips the RSSI-tagging wrapper without crashing the bring-up.
/// The deferral is observed positively, never inferred from a sleep: the node's UA is awaited
/// onto the transmit path before the "nothing on the air" assertion, and the listener's own
/// CSMA record (emitted only when the gate really held a frame) closes the loop afterwards.
/// </summary>
[Trait("Category", "Node")]
public sealed class PortRigRadioIntegrationTests
{
    private static readonly Callsign NodeCall = new("NODE", 1);
    private static readonly Callsign RemoteCall = new("REMOTE", 1);

    /// <summary>
    /// How much virtual time each poll of a wait nudges the clock by. Deliberately a fifth of
    /// both the gate's 100 ms slot time and the rig-backed radio's 100 ms DCD poll: the waits
    /// below advance on EVERY poll (a single Advance can be missed if a loop is between timer
    /// arms when it lands, which would hang the test on a starved runner), so the step has to be
    /// small enough that reaching the gate's 10 s fail-open backstop would take thousands of
    /// polls - and the test asserts it never does.
    /// </summary>
    private static readonly TimeSpan ClockNudge = TimeSpan.FromMilliseconds(20);

    /// <summary>The gate's bounded wait (<c>CarrierSenseGateOptions.MaxWait</c>), after which it
    /// transmits anyway (fail-open). The deferral below is asserted to have ended well inside it,
    /// so the release is only ever the rig's DCD clearing.</summary>
    private static readonly TimeSpan GateFailOpenAfter = TimeSpan.FromSeconds(10);

    private static NodeConfig ConfigWithRigBackedRadio() => new()
    {
        Identity = new Identity { Callsign = NodeCall.ToString() },
        Ports =
        [
            new PortConfig
            {
                Id = "hf",
                Enabled = true,
                Transport = new KissTcpTransport { Host = "mem", Port = 1 },
                Rig = new PortRigConfig { Kind = "hamlib", Host = "127.0.0.1", Port = 4532 },
                Radio = new PortRadioConfig { Kind = "rig" },
                Ax25 = new Ax25PortParams { N2 = TestAx25Timing.NodeN2 },
            },
        ],
    };

    [Fact]
    public async Task A_rig_backed_radio_gates_csma_with_the_rigs_dcd_and_the_status_arm_dials_its_own_connection()
    {
        var time = new FakeTimeProvider();
        var bus = new SharedRadioBus();
        // Captures what the node logs, so the deferral can be read off the listener's own
        // rendered record rather than inferred from silence (see the assertion at the end).
        var logs = new CapturingLoggerFactory();
        var config = new TestConfigProvider(ConfigWithRigBackedRadio());
        var transports = new FakeTransportFactory().Provide("kiss-tcp:mem:1", bus.Attach());
        // Two dedicated connections to the same daemon: the radio arm dials FIRST (bring-up
        // order), the status poller second - so the radio arm's fake leads the queue. DCD is
        // asserted before bring-up so the bridge's first poll sample reads busy.
        var radioRig = new FakeRigControl(name: "radio-rig")
        {
            Capabilities = RigCapabilities.DcdRead | RigCapabilities.SignalStrengthRead | RigCapabilities.PttSet,
            Dcd = true,
        };
        var statusRig = new FakeRigControl(name: "status-rig");
        var rigs = new FakeRigControlFactory().Provide(radioRig, statusRig);

        // Observe what the node transmits, and hand-inject a SABM from the "air".
        var observer = bus.Attach();
        var heard = new List<byte[]>();
        var heardGate = new object();
        using var readerCts = new CancellationTokenSource();
        var reader = Task.Run(async () =>
        {
            await foreach (var f in observer.ReceiveAsync(readerCts.Token))
            {
                lock (heardGate)
                {
                    heard.Add(f.Ax25.ToArray());
                }
            }
        });

        await using (var supervisor = new PortSupervisor(
            config, transports, time, logs, rigFactory: rigs))
        {
            await supervisor.StartAsync();
            await Wait.ForAsync(() => supervisor.RunningPortIds.Contains("hf"), "port hf up");

            var port = supervisor.GetPort("hf")!;
            port.Radio.Should().BeOfType<RigRadioControl>("kind rig re-presents the rig through the radio seam");
            port.Transport.Should().BeOfType<InboundRadioTap>(
                "a strength-capable rig-backed radio gets the RSSI-tagging wrap like any other radio");
            // The rig-status side is undisturbed: its own connection, its own poller.
            rigs.Requests.Should().HaveCount(2, "the radio arm and the status poller each dial a dedicated connection");
            port.Rig.Should().BeSameAs(statusRig, "the status poller owns the second connection");
            port.RigStatus.Should().NotBeNull();

            // The bridge's first DCD sample (taken at construction, no clock advance needed)
            // saw the asserted carrier.
            await Wait.ForAsync(() => port.Radio!.ChannelBusy == true, "the bridge sampled the rig's DCD");

            // POSITIVE proof the UA reached the transmit path, awaited before any negative is
            // asserted: the listener traces a frame as Transmitted at the moment it hands it to
            // the carrier-sense-gated send (Ax25Listener's SendGatedAsync). A rig-backed radio
            // caches DCD behind its own poll loop, so - unlike the FakeRadioControl port, whose
            // fake counts the gate's reads directly - the gate's sampling is not observable at
            // the fake rig here; the deferral itself is proven from the listener's CSMA record
            // once the frame is released, below. Waiting on a Task.Delay instead, as this test
            // used to, proved nothing: a gate that leaked the UA immediately still left `heard`
            // empty on a starved runner, because the frame had not been built yet.
            var uaOnTheTransmitPath = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            void OnFrameTraced(object? sender, Ax25FrameEventArgs e)
            {
                if (e.Direction == FrameDirection.Transmitted && (e.Frame.Control & 0xEF) == 0x63)
                {
                    uaOnTheTransmitPath.TrySetResult();
                }
            }

            var deferralStartedAt = time.GetUtcNow();
            port.Listener.FrameTraced += OnFrameTraced;
            try
            {
                await observer.SendAsync(Ax25Frame.Sabm(NodeCall, RemoteCall).ToBytes());
                await uaOnTheTransmitPath.Task.WaitAsync(Wait.DefaultBudget);
            }
            finally
            {
                port.Listener.FrameTraced -= OnFrameTraced;
            }

            lock (heardGate)
            {
                heard.Should().BeEmpty("a busy channel holds the node's UA off the air (hardware DCD via the rig)");
            }

            // Hold it there over several slot times of virtual clock - the gate re-samples every
            // slot and the rig's DCD poll every interval, and the UA still never reaches the air.
            for (var nudge = 0; nudge < 15; nudge++)
            {
                time.Advance(ClockNudge);
            }
            lock (heardGate)
            {
                heard.Should().BeEmpty("the keyup stays deferred for as long as the rig reports carrier");
            }

            // Channel clears: the next poll tick re-samples DCD, and one slot later the gate
            // releases the keyup.
            radioRig.Dcd = false;
            await Wait.ForAsync(() =>
            {
                time.Advance(ClockNudge);
                lock (heardGate)
                {
                    return heard.Any(IsUa);
                }
            }, "the node keys up its UA once the rig's DCD clears");

            // ...and it was the DCD clear that released it, not the gate's fail-open backstop.
            (time.GetUtcNow() - deferralStartedAt).Should().BeLessThan(
                GateFailOpenAfter, "the DCD clearing released the UA, not the gate's fail-open backstop");

            // And the UA was HELD by the gate, not merely late. The listener renders
            // "AX.25 [{Port}] CSMA: waited {Ms}ms for clear channel" only after its gate actually
            // parked a frame for more than 50 ms of the injected clock, so the record is positive
            // evidence of the deferral: a gate that let the UA straight through waits zero and
            // logs nothing at all.
            logs.Lines.Should().Contain(
                line => line.Contains("CSMA: waited", StringComparison.Ordinal),
                "the gate held the UA off the air until the rig's DCD cleared, and said so");
        }

        radioRig.Disposed.Should().BeTrue("the owning bridge closes its dedicated connection on teardown");
        statusRig.Disposed.Should().BeTrue("the status arm's connection closes with the port");

        await readerCts.CancelAsync();
        try { await reader; } catch (OperationCanceledException) { }
    }

    [Fact]
    public async Task A_dcd_only_rig_backed_radio_brings_the_port_up_without_the_tagging_wrapper()
    {
        var bus = new SharedRadioBus();
        var config = new TestConfigProvider(ConfigWithRigBackedRadio());
        var transports = new FakeTransportFactory().Provide("kiss-tcp:mem:1", bus.Attach());
        // The rig's DCD is calibrated but its strength meter isn't advertised: carrier-sense
        // only. RssiTaggingTransport requires RssiRead, so the wrap must be skipped, not crash.
        var radioRig = new FakeRigControl(name: "radio-rig") { Capabilities = RigCapabilities.DcdRead };
        var statusRig = new FakeRigControl(name: "status-rig");
        var rigs = new FakeRigControlFactory().Provide(radioRig, statusRig);

        await using var supervisor = new PortSupervisor(
            config, transports, TimeProvider.System, NullLoggerFactory.Instance, rigFactory: rigs);
        await supervisor.StartAsync();
        await Wait.ForAsync(() => supervisor.RunningPortIds.Contains("hf"), "port hf up with a DCD-only radio");

        var port = supervisor.GetPort("hf")!;
        port.Radio.Should().BeOfType<RigRadioControl>();
        port.Radio!.Capabilities.Should().Be(RadioCapabilities.CarrierSense, "DcdRead maps to carrier-sense only");
        // No RSSI ⇒ no tagging wrap: the inbound path is wired exactly as a no-radio port's.
        port.Transport.Should().NotBeOfType<InboundRadioTap>("a DCD-only radio cannot feed the RSSI tagger");
        port.InnerTransport.Should().BeNull("without the wrap, Transport IS the modem chain");

        // And the port still carries traffic (the gate is clear - the fake's DCD reads false).
        await using var remote = new RemoteStation(bus.Attach(), RemoteCall);
        await remote.StartAsync();
        await remote.ConnectAsync(NodeCall);
        await Wait.ForAsync(() => remote.Saw("Welcome"), "the session reached the prompt without radio metadata");
    }

    /// <summary>UA U-frame test (§4.3.3): control 0x63, P/F bit masked off. Frames off the bus
    /// are FCS-less AX.25 bodies, so a plain parse suffices.</summary>
    private static bool IsUa(byte[] ax25) =>
        Ax25Frame.TryParse(ax25, out var f) && (f!.Control & 0xEF) == 0x63;
}
