using AwesomeAssertions;
using Packet.Ax25.Session;
using Packet.Core;
using Xunit;

namespace Packet.Ax25.Tests.Session;

/// <summary>
/// Tests for the connectionless AX.25 TEST responder (§4.3.4.2) wired into
/// <see cref="Ax25Listener.DispatchInbound"/>: a station receiving a TEST
/// <em>command</em> addressed to it replies with a TEST <em>response</em> echoing
/// the command's information field - the "axping" answer side. The exchange is
/// link-independent: it must never create or disturb a session.
/// </summary>
/// <remarks>
/// Each test wires a <see cref="LoopbackModem"/> in place of a real KISS modem
/// and injects the bytes a remote station would put on the air; the listener
/// parses, detects the TEST command before any session routing, and emits the
/// response on the modem's outbound queue.
/// </remarks>
public sealed class Ax25ListenerTestResponderTests
{
    private static readonly Callsign LocalCall = new("M0LTE", 0);
    private static readonly Callsign PeerCall = new("G7XYZ", 7);

    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(2);

    [Fact]
    public async Task Listener_Answers_Inbound_TEST_Command_With_Echoing_Response()
    {
        var modem = new LoopbackModem();
        await using var listener = new Ax25Listener(modem, new Ax25ListenerOptions { MyCall = LocalCall });
        await listener.StartAsync();

        var info = new byte[] { 0xDE, 0xAD, 0xBE, 0xEF, 0x42 };

        // Inject a TEST COMMAND (P=1) from the peer, addressed to us.
        modem.InjectInbound(Ax25Frame.Test(
            destination: LocalCall, source: PeerCall, info: info, isCommand: true, pollFinal: true));

        await modem.SentFrames.WaitForCountAsync(1, Budget);
        modem.SentFrames.Count.Should().Be(1, "exactly one TEST response should be emitted");

        Ax25Frame.TryParse(modem.SentFrames[0].Span, out var sent).Should().BeTrue();

        // It must be a TEST frame (U-frame control 0xE3, P/F-masked) ...
        (sent!.Control & 0xEF).Should().Be(0xE3, "the reply must be a TEST frame");
        // ... a RESPONSE (not another command) ...
        sent.IsResponse.Should().BeTrue("a TEST command is answered with a TEST response");
        sent.IsCommand.Should().BeFalse();
        // ... addressed back to the sender, sourced from us ...
        sent.Destination.Callsign.Should().Be(PeerCall, "the response goes back to the station that sent the command");
        sent.Source.Callsign.Should().Be(LocalCall, "the response is sourced from our own callsign");
        // ... echoing the command's info field verbatim ...
        sent.Info.ToArray().Should().Equal(info, "§4.3.4.2: the response echoes the command's information field");
        // ... with the F bit mirroring the command's P bit (we sent P=1).
        sent.PollFinal.Should().BeTrue("the response's F bit mirrors the command's P bit");

        // The TEST exchange is connectionless - no session may have been created.
        listener.ActiveSessions.Should().BeEmpty("a TEST command must not create or disturb any session");
    }

    [Fact]
    public async Task Listener_Mirrors_The_Command_P_Bit_Off_In_The_Response_F_Bit()
    {
        var modem = new LoopbackModem();
        await using var listener = new Ax25Listener(modem, new Ax25ListenerOptions { MyCall = LocalCall });
        await listener.StartAsync();

        // TEST command with P=0 - the F bit of the response must also be 0.
        modem.InjectInbound(Ax25Frame.Test(
            destination: LocalCall, source: PeerCall, info: new byte[] { 0x01 }, isCommand: true, pollFinal: false));

        await modem.SentFrames.WaitForCountAsync(1, Budget);
        Ax25Frame.TryParse(modem.SentFrames[0].Span, out var sent).Should().BeTrue();
        sent!.PollFinal.Should().BeFalse("a P=0 TEST command yields an F=0 TEST response");
        sent.IsResponse.Should().BeTrue();
    }

    [Fact]
    public async Task Listener_Answers_Empty_Info_TEST_Command_With_Empty_Info_Response()
    {
        var modem = new LoopbackModem();
        await using var listener = new Ax25Listener(modem, new Ax25ListenerOptions { MyCall = LocalCall });
        await listener.StartAsync();

        modem.InjectInbound(Ax25Frame.Test(
            destination: LocalCall, source: PeerCall, info: ReadOnlySpan<byte>.Empty, isCommand: true, pollFinal: true));

        await modem.SentFrames.WaitForCountAsync(1, Budget);
        Ax25Frame.TryParse(modem.SentFrames[0].Span, out var sent).Should().BeTrue();
        sent!.Info.Length.Should().Be(0, "an empty TEST command echoes back an empty info field");
        sent.IsResponse.Should().BeTrue();
        listener.ActiveSessions.Should().BeEmpty();
    }

    [Fact]
    public async Task Listener_Does_Not_Answer_An_Inbound_TEST_Response_No_Ping_Pong()
    {
        var modem = new LoopbackModem();
        await using var listener = new Ax25Listener(modem, new Ax25ListenerOptions { MyCall = LocalCall });
        await listener.StartAsync();

        var traced = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        listener.FrameTraced += (_, e) =>
        {
            if (e.Direction == FrameDirection.Received)
            {
                traced.TrySetResult();
            }
        };

        // Inject a TEST RESPONSE (the echo to someone's axping) - NOT a command.
        // It must flow as a normal inbound frame (so the AxPinger initiator can
        // correlate it via FrameTraced), and must NOT trigger another response.
        modem.InjectInbound(Ax25Frame.Test(
            destination: LocalCall, source: PeerCall, info: new byte[] { 0x09 }, isCommand: false, pollFinal: true));

        // The frame is received + traced (proving the pump processed it) ...
        await traced.Task.WithTimeout(Budget);

        // ... but nothing is transmitted in reply (no ping-pong), and no session
        // is created. Give the pump a moment to (not) act before asserting.
        await Task.Delay(200);
        modem.SentFrames.Count.Should().Be(0, "a TEST *response* must never be answered — that would ping-pong forever");
        listener.ActiveSessions.Should().BeEmpty("a TEST response is connectionless and must not create a session");
    }

    [Fact]
    public async Task Listener_Ignores_A_TEST_Command_Not_Addressed_To_Us()
    {
        var modem = new LoopbackModem();
        await using var listener = new Ax25Listener(modem, new Ax25ListenerOptions { MyCall = LocalCall });
        await listener.StartAsync();

        var traced = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        listener.FrameTraced += (_, e) =>
        {
            if (e.Direction == FrameDirection.Received)
            {
                traced.TrySetResult();
            }
        };

        // TEST command addressed to a THIRD party, overheard on the shared channel.
        var otherStation = new Callsign("M5ABC", 3);
        modem.InjectInbound(Ax25Frame.Test(
            destination: otherStation, source: PeerCall, info: new byte[] { 0xAA }, isCommand: true, pollFinal: true));

        await traced.Task.WithTimeout(Budget);
        await Task.Delay(200);
        modem.SentFrames.Count.Should().Be(0, "we only answer TEST commands addressed to our own callsign");
    }
}
