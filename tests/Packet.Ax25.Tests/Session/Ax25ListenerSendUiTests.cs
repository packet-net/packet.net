using AwesomeAssertions;
using Packet.Ax25.Session;
using Packet.Core;
using Xunit;

namespace Packet.Ax25.Tests.Session;

/// <summary>
/// Tests for <see cref="Ax25Listener.SendUiAsync"/> — the connectionless UI
/// (unproto) send path added for NET/ROM NODES origination. It bypasses the
/// session layer: the source is the listener's own callsign and the frame is a UI
/// frame with the supplied destination + PID + info.
/// </summary>
public sealed class Ax25ListenerSendUiTests
{
    private static readonly Callsign LocalCall = new("M0LTE", 0);

    [Fact]
    public async Task SendUiAsync_emits_a_UI_frame_with_the_given_dest_pid_and_info()
    {
        var modem = new LoopbackModem();
        await using var listener = new Ax25Listener(modem, new Ax25ListenerOptions { MyCall = LocalCall });
        await listener.StartAsync();

        var nodesDest = new Callsign("NODES", 0);
        var info = new byte[] { 0xFF, (byte)'R', (byte)'D', (byte)'G' };
        await listener.SendUiAsync(nodesDest, info, Ax25Frame.PidNetRom);

        modem.SentFrames.Count.Should().Be(1);
        Ax25Frame.TryParse(modem.SentFrames[0].Span, out var sent).Should().BeTrue();
        sent!.IsUi.Should().BeTrue("a NODES broadcast rides a UI frame");
        sent.Pid.Should().Be(Ax25Frame.PidNetRom);
        sent.Destination.Callsign.Should().Be(nodesDest);
        sent.Source.Callsign.Should().Be(LocalCall, "the source is the listener's own callsign");
        sent.Info.ToArray().Should().Equal(info);
    }

    [Fact]
    public async Task SendUiAsync_with_an_explicit_source_emits_a_UI_frame_from_that_callsign()
    {
        // The RHPv2 dgram sendto path: originate a UI frame AS an application's bound callsign,
        // not the listener's own MyCall (e.g. IP-over-AX.25 pid 0xCC).
        var modem = new LoopbackModem();
        await using var listener = new Ax25Listener(modem, new Ax25ListenerOptions { MyCall = LocalCall });
        await listener.StartAsync();

        var appCall = new Callsign("2E0APP", 7);
        var dest = new Callsign("GB7RDG", 0);
        var info = "hello"u8.ToArray();
        await listener.SendUiAsync(appCall, dest, info, pid: 0xCC);

        modem.SentFrames.Count.Should().Be(1);
        Ax25Frame.TryParse(modem.SentFrames[0].Span, out var sent).Should().BeTrue();
        sent!.IsUi.Should().BeTrue();
        sent.Pid.Should().Be(0xCC);
        sent.Destination.Callsign.Should().Be(dest);
        sent.Source.Callsign.Should().Be(appCall, "the source is the explicit callsign, not MyCall");
        sent.Info.ToArray().Should().Equal(info);
    }

    [Fact]
    public async Task SendUiAsync_traces_the_frame_as_transmitted()
    {
        var modem = new LoopbackModem();
        await using var listener = new Ax25Listener(modem, new Ax25ListenerOptions { MyCall = LocalCall });
        await listener.StartAsync();

        FrameDirection? traced = null;
        listener.FrameTraced += (_, e) => traced = e.Direction;

        await listener.SendUiAsync(new Callsign("NODES", 0), new byte[] { 0xFF }, Ax25Frame.PidNetRom);

        traced.Should().Be(FrameDirection.Transmitted, "the monitor should see the originated NODES broadcast");
    }
}
