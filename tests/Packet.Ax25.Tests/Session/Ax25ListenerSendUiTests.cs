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
