using AwesomeAssertions;
using Packet.Ax25.Session;
using Packet.Core;
using Xunit;

namespace Packet.Ax25.Tests.Session;

/// <summary>
/// An outbound connect that fails says why (packet-net/packet.net#849). Awaiting Connection
/// ends a dial two ways that used to look the same: its DM arm raises DL-DISCONNECT indication
/// on its own (the peer refused), and its RC == N2 arm raises DL-ERROR indication (G) and then
/// DL-DISCONNECT indication (the peer never answered).
/// </summary>
public class Ax25ListenerConnectFailureTests
{
    private static readonly Callsign LocalCall = new("M0LTE", 0);
    private static readonly Callsign PeerCall = new("G7XYZ", 7);

    [Fact]
    public async Task A_DM_answer_to_the_SABM_is_Refused()
    {
        var modem = new LoopbackModem();
        await using var listener = new Ax25Listener(modem, new Ax25ListenerOptions
        {
            MyCall = LocalCall,
            PreConnectXidNegotiatesSrej = false,
        });
        await listener.StartAsync();

        var dial = listener.ConnectAsync(PeerCall, LocalCall, extended: false, preConnectXidNegotiatesSrej: false);
        await modem.SentFrames.WaitForCountAsync(1, TimeSpan.FromSeconds(2)); // SABM
        modem.InjectInbound(Ax25Frame.Dm(LocalCall, PeerCall, finalBit: true));

        var act = async () => await dial.WithTimeout(TimeSpan.FromSeconds(5));
        var ex = (await act.Should().ThrowAsync<Ax25ConnectFailedException>()).Which;
        ex.Failure.Should().Be(Ax25ConnectFailure.Refused);
        ex.Remote.Should().Be(PeerCall);
        ex.Should().BeAssignableTo<InvalidOperationException>("existing handlers catch the failure by that type");
    }

    [Fact]
    public async Task A_DM_to_a_v22_dial_degrades_and_a_second_DM_is_Refused()
    {
        // The Ax25Spec48 quirk turns a DM to SABME into a v2.0 retry; a DM to that SABM is
        // still a refusal, not a reset.
        var modem = new LoopbackModem();
        await using var listener = new Ax25Listener(modem, new Ax25ListenerOptions
        {
            MyCall = LocalCall,
            PreConnectXidNegotiatesSrej = false,
        });
        await listener.StartAsync();

        var dial = listener.ConnectAsync(PeerCall, LocalCall, extended: true, preConnectXidNegotiatesSrej: false);
        await modem.SentFrames.WaitForCountAsync(1, TimeSpan.FromSeconds(2)); // SABME
        modem.InjectInbound(Ax25Frame.Dm(LocalCall, PeerCall, finalBit: true));
        await modem.SentFrames.WaitForCountAsync(2, TimeSpan.FromSeconds(2)); // SABM
        modem.InjectInbound(Ax25Frame.Dm(LocalCall, PeerCall, finalBit: true));

        var act = async () => await dial.WithTimeout(TimeSpan.FromSeconds(5));
        (await act.Should().ThrowAsync<Ax25ConnectFailedException>()).Which
            .Failure.Should().Be(Ax25ConnectFailure.Refused);
    }

    [Fact]
    public async Task Silence_until_T1_has_expired_N2_times_is_NoAnswer()
    {
        var modem = new LoopbackModem();
        await using var listener = new Ax25Listener(modem, new Ax25ListenerOptions
        {
            MyCall = LocalCall,
            N2 = 1,
            T1V = TimeSpan.FromMilliseconds(200),
            PreConnectXidNegotiatesSrej = false,
        });
        await listener.StartAsync();

        var act = async () => await listener
            .ConnectAsync(PeerCall, LocalCall, extended: false, preConnectXidNegotiatesSrej: false)
            .WithTimeout(TimeSpan.FromSeconds(10));
        (await act.Should().ThrowAsync<Ax25ConnectFailedException>()).Which
            .Failure.Should().Be(Ax25ConnectFailure.NoAnswer);
    }
}
