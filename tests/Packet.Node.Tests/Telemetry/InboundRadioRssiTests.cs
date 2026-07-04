using Packet.Ax25;
using Packet.Ax25.Session;
using Packet.Ax25.Transport;
using Packet.Core;
using Packet.Node.Core.Heard;
using Packet.Node.Core.Telemetry;

namespace Packet.Node.Tests.Telemetry;

/// <summary>
/// Per-frame RSSI/SNR reaching the node telemetry surfaces through the node-owned
/// <see cref="IInboundRadioSource"/> — the design that keeps radio metadata OFF the parity-tracked
/// AX.25 listener contract. Drives <see cref="NodeTelemetry.Observe"/> directly with a scripted radio
/// source and asserts the stamped <see cref="Packet.Node.Core.Api.MonitorEvent"/> + the heard log.
/// </summary>
public sealed class InboundRadioRssiTests
{
    private const string Port = "vhf-1";
    private static readonly Callsign Local = Callsign.Parse("M0LTE-1");
    private static readonly Callsign Peer = Callsign.Parse("G7XYZ-2");

    private static Ax25FrameEventArgs Rx(Ax25Frame frame, DateTimeOffset at)
        => new() { Frame = frame, Direction = FrameDirection.Received, Timestamp = at };

    private static Ax25FrameEventArgs Tx(Ax25Frame frame, DateTimeOffset at)
        => new() { Frame = frame, Direction = FrameDirection.Transmitted, Timestamp = at };

    private static DateTimeOffset At(int seconds)
        => new DateTimeOffset(2026, 6, 1, 0, 0, 0, TimeSpan.Zero).AddSeconds(seconds);

    private sealed class FakeRadioSource : IInboundRadioSource
    {
        public RadioMetadata? LatestInboundRadio { get; set; }
    }

    [Fact]
    public void A_received_frame_carries_rssi_snr_and_noise_floor_from_the_radio_source()
    {
        var t = new NodeTelemetry();
        var src = new FakeRadioSource
        {
            LatestInboundRadio = new RadioMetadata(RssiDbm: -84.2f, SnrDb: 26.5f, NoiseFloorDbm: -110.7f),
        };

        t.Observe(Port, Rx(Ax25Frame.Ui(Local, Peer, "hi"u8), At(0)), src);

        var evt = t.RecentFrames(1).Single();
        evt.RssiDbm.Should().Be(-84.2f);
        evt.SnrDb.Should().Be(26.5f);
        evt.NoiseFloorDbm.Should().Be(-110.7f);
    }

    [Fact]
    public void A_transmitted_frame_never_reads_the_inbound_radio_metadata()
    {
        // The radio source only ever holds inbound metadata; a TX trace must not borrow it.
        var t = new NodeTelemetry();
        var src = new FakeRadioSource { LatestInboundRadio = new RadioMetadata(RssiDbm: -84.2f) };

        t.Observe(Port, Tx(Ax25Frame.Ui(Peer, Local, "hi"u8), At(0)), src);

        t.RecentFrames(1).Single().RssiDbm.Should().BeNull();
    }

    [Fact]
    public void With_no_radio_source_the_frame_carries_null_signal_fields()
    {
        var t = new NodeTelemetry();
        t.Observe(Port, Rx(Ax25Frame.Ui(Local, Peer, "hi"u8), At(0)));   // no radio source (port has no radio)

        var evt = t.RecentFrames(1).Single();
        evt.RssiDbm.Should().BeNull();
        evt.SnrDb.Should().BeNull();
        evt.NoiseFloorDbm.Should().BeNull();
    }

    [Fact]
    public void The_heard_log_records_last_heard_rssi_for_received_frames()
    {
        var heard = new HeardLog(store: null);
        var t = new NodeTelemetry(logger: null, heardLog: heard);
        var src = new FakeRadioSource { LatestInboundRadio = new RadioMetadata(RssiDbm: -73.1f) };

        t.Observe(Port, Rx(Ax25Frame.Ui(Local, Peer, "x"u8), At(0)), src);

        var rows = heard.ForPort(Port);
        rows.Should().ContainSingle();
        rows[0].Callsign.Should().Be(Peer.ToString());   // heard station = the RX source
        rows[0].LastRssiDbm.Should().Be(-73.1f);
    }
}
