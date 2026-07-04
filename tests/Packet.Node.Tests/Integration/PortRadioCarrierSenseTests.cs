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
/// SABM is held off the air while the radio reports busy, and keys up once it clears.
/// </summary>
[Trait("Category", "Node")]
public sealed class PortRadioCarrierSenseTests
{
    private static readonly Callsign NodeCall = new("NODE", 1);
    private static readonly Callsign RemoteCall = new("REMOTE", 1);

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
        await observer.SendAsync(Ax25Frame.Sabm(NodeCall, RemoteCall).ToBytes());

        // Give the node's inbound pump time to process the SABM and reach the medium-access
        // gate. The gate then holds the UA on the (un-advanced) virtual clock: the node cannot
        // key up while the channel is busy, so the observer hears nothing.
        await Task.Delay(300);
        lock (heardGate)
        {
            heard.Should().BeEmpty("a busy channel holds the node's UA off the air (native carrier-sense CSMA)");
        }

        // Channel clears: one slot later the gate re-samples and releases the keyup.
        radio.RaiseCarrierSense(false, time.GetUtcNow());
        time.Advance(TimeSpan.FromMilliseconds(100));

        await Wait.ForAsync(() =>
        {
            lock (heardGate)
            {
                return heard.Any(IsUa);
            }
        }, "the node keys up its UA once the channel clears");

        await readerCts.CancelAsync();
        try { await reader; } catch (OperationCanceledException) { }
    }

    /// <summary>UA U-frame test (§4.3.3): control 0x63, P/F bit masked off. Frames off the bus
    /// are FCS-less AX.25 bodies, so a plain parse suffices.</summary>
    private static bool IsUa(byte[] ax25) =>
        Ax25Frame.TryParse(ax25, out var f) && (f!.Control & 0xEF) == 0x63;
}
