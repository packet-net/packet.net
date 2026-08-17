using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Packet.Ax25;
using Packet.Ax25.Transport;
using Packet.Core;
using Packet.Node.Core.Configuration;
using Packet.Node.Core.Hosting;
using Packet.Node.Tests.Support;

namespace Packet.Node.Tests.Integration;

/// <summary>
/// OQ-012 wiring: a radio-attached port feeds its hardware carrier-sense (DCD) into the AX.25
/// stack's native medium-access gate (via the parity-tracked <c>Ax25ListenerOptions.CarrierSense</c>
/// option), so the node itself defers a keyup while the channel is busy and releases it when the
/// channel clears — the native seam, owned by the stack rather than an opaque transport wrapper.
/// Proven end-to-end through a live <see cref="PortSupervisor"/>: the node's reply to an inbound
/// SABM is held off the air while the radio reports busy, and keys up once it clears. The
/// deferral itself is observed positively - the fake radio counts the gate's carrier-sense
/// reads (<see cref="FakeRadioControl.BusyChannelBusyReads"/>), so "nothing on the air" is only
/// asserted once the UA is provably parked in the gate's slot-time wait.
/// </summary>
[Trait("Category", "Node")]
public sealed class PortRadioCarrierSenseTests
{
    private static readonly Callsign NodeCall = new("NODE", 1);
    private static readonly Callsign RemoteCall = new("REMOTE", 1);

    /// <summary>
    /// How much virtual time each poll of a wait nudges the clock by. Deliberately a fifth of
    /// the gate's 100 ms slot time: the waits below advance on EVERY poll (a single Advance can
    /// be missed if the gate is between timer arms when it lands, which would hang the test on a
    /// starved runner), so the step has to be small enough that reaching the gate's 10 s
    /// fail-open backstop would take thousands of polls - and the tests assert it never does.
    /// </summary>
    private static readonly TimeSpan ClockNudge = TimeSpan.FromMilliseconds(20);

    /// <summary>The gate's bounded wait (<c>CarrierSenseGateOptions.MaxWait</c>), after which it
    /// transmits anyway (fail-open). Every deferral below is asserted to have ended well inside
    /// it, so a release is only ever the carrier clearing.</summary>
    private static readonly TimeSpan GateFailOpenAfter = TimeSpan.FromSeconds(10);

    private static NodeConfig ConfigWithRadioPort() => new()
    {
        Identity = new Identity { Callsign = NodeCall.ToString() },
        Ports =
        [
            new PortConfig
            {
                Id = "a",
                Enabled = true,
                Transport = new SerialKissTransport { Device = "/dev/pty-a" },
                Radio = new PortRadioConfig { Kind = "tait-ccdi", Port = "/dev/ttyUSB0", Baud = 28800 },
                Ax25 = new Ax25PortParams { N2 = TestAx25Timing.NodeN2 },
            },
        ],
    };

    [Fact]
    public async Task A_radio_attached_port_defers_its_reply_while_carrier_sense_is_busy()
    {
        var time = new FakeTimeProvider();
        var bus = new SharedRadioBus();
        var config = new TestConfigProvider(ConfigWithRadioPort());
        var transports = new FakeTransportFactory().Provide("serial-kiss:/dev/pty-a", bus.Attach());
        var radio = new FakeRadioControl();   // advertises RssiRead | CarrierSense
        var radios = new FakeRadioControlFactory().Provide(radio);

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

        await using var supervisor = new PortSupervisor(
            config, transports, time, NullLoggerFactory.Instance, radioFactory: radios);
        await supervisor.StartAsync();
        await Wait.ForAsync(() => supervisor.RunningPortIds.Contains("a"), "port a up");

        // Channel is busy: the radio asserts DCD before the peer's SABM arrives.
        radio.RaiseCarrierSense(true, time.GetUtcNow());
        var deferralStartedAt = time.GetUtcNow();
        var busyReadsBeforeSabm = radio.BusyChannelBusyReads;
        await observer.SendAsync(Ax25Frame.Sabm(NodeCall, RemoteCall).ToBytes());

        // POSITIVE proof the UA reached the medium-access gate and is being HELD there, before
        // any negative is asserted. CarrierSenseGate.WaitForClearAsync reads carrier-sense once
        // on entry (the fast path that would have let a clear channel through) and once more at
        // the top of its slot-time wait loop; both reads reach the fake through the node's
        // RadioCarrierSense adapter, a pure read-through that caches nothing. Two busy reads on
        // an UN-ADVANCED virtual clock can therefore only be the gate sitting in its wait: its
        // own slot timer runs on this FakeTimeProvider, and the only other reader of the seam
        // (the generic radio-status monitor) reads it inside Snapshot(), which nothing calls
        // here. Waiting on a Task.Delay instead - as this test used to - proved nothing: a gate
        // that leaked the frame immediately still left `heard` empty on a starved runner.
        await Wait.ForAsync(
            () => radio.BusyChannelBusyReads - busyReadsBeforeSabm >= 2,
            "the node's UA reached the carrier-sense gate, which sampled a busy channel and entered its slot-time wait");
        lock (heardGate)
        {
            heard.Should().BeEmpty("a busy channel holds the node's UA off the air (native carrier-sense CSMA)");
        }

        // Still busy a slot later: the gate re-samples, finds the channel busy, and keeps
        // holding - the deferral is sustained, not a one-shot check.
        var busyReadsWhileDeferred = radio.BusyChannelBusyReads;
        await Wait.ForAsync(
            () =>
            {
                time.Advance(ClockNudge);
                return radio.BusyChannelBusyReads > busyReadsWhileDeferred;
            },
            "the gate re-samples carrier-sense every slot time while it defers");
        lock (heardGate)
        {
            heard.Should().BeEmpty("the keyup stays deferred for as long as the radio reports carrier");
        }

        // Channel clears: the gate's slot expires, it re-samples clear, and releases the keyup.
        radio.RaiseCarrierSense(false, time.GetUtcNow());
        await Wait.ForAsync(() =>
        {
            time.Advance(ClockNudge);
            lock (heardGate)
            {
                return heard.Any(IsUa);
            }
        }, "the node keys up its UA once the channel clears");

        // ...and it was the clear that released it, not the gate's fail-open backstop: the whole
        // deferral fitted inside a fraction of the bounded wait.
        (time.GetUtcNow() - deferralStartedAt).Should().BeLessThan(
            GateFailOpenAfter, "the carrier clearing released the UA, not the gate's fail-open backstop");

        await readerCts.CancelAsync();
        try { await reader; } catch (OperationCanceledException) { }
    }

    /// <summary>UA U-frame test (§4.3.3): control 0x63, P/F bit masked off. Frames off the bus
    /// are FCS-less AX.25 bodies, so a plain parse suffices.</summary>
    private static bool IsUa(byte[] ax25) =>
        Ax25Frame.TryParse(ax25, out var f) && (f!.Control & 0xEF) == 0x63;
}
