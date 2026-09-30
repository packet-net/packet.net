using AwesomeAssertions;
using Packet.Ax25.Session;
using Packet.Ax25.Xid;
using Packet.Core;
using Xunit;

namespace Packet.Ax25.Tests.Session;

/// <summary>
/// A peer whose XID response advertises an I Field Length Rx under eight bits (#894). The
/// parameter is a limit a transmitter may not exceed (AX.25 v2.2 §4.3.3.7) and the spec
/// sets no minimum, so the link comes up with N1 = 0 and carries no information field:
/// every send is refused with a reason, rather than floored past the peer's limit or the
/// parameter ignored.
/// </summary>
public class Ax25ListenerXidN1Tests
{
    private static readonly Callsign LocalCall = new("M0LTE", 0);
    private static readonly Callsign PeerCall = new("G7XYZ", 7);

    private const byte XidControl = 0xAF;
    private static bool IsXidCommand(Ax25Frame f) => (f.Control & 0x03) == 0x03 && (f.Control & 0xEF) == XidControl && f.IsCommand;

    [Fact]
    public async Task A_peer_advertising_under_one_octet_gets_a_link_that_carries_no_information_field()
    {
        var modem = new LoopbackModem();
        await using var listener = new Ax25Listener(modem, new Ax25ListenerOptions
        {
            MyCall = LocalCall,
            PreferExtendedConnect = false,   // mod-8, so the dial's pre-connect XID probe runs
        });
        await listener.StartAsync();

        var connectTask = listener.ConnectAsync(PeerCall);

        // The dial opens with its XID command; the peer answers with a 4-bit I-field limit.
        await modem.SentFrames.WaitForCountAsync(1, TimeSpan.FromSeconds(2));
        Ax25Frame.TryParse(modem.SentFrames[0].Span, out var probe).Should().BeTrue();
        IsXidCommand(probe!).Should().BeTrue("the dial probes with an XID command first");
        modem.InjectInbound(Ax25Frame.Xid(
            destination: LocalCall,
            source: PeerCall,
            info: XidInfoField.Encode(new XidParameters
            {
                HdlcOptionalFunctions = new HdlcOptionalFunctions { Reject = RejectMode.SelectiveReject, SrejMultiframe = true, Modulo128 = false },
                IFieldLengthRxBits = 4,
            }),
            isCommand: false,
            pollFinal: true));

        // The SABM follows; answer it and the link is up.
        await modem.SentFrames.WaitForCountAsync(2, TimeSpan.FromSeconds(2));
        modem.InjectInbound(Ax25Frame.Ua(LocalCall, PeerCall, finalBit: true));
        var session = await connectTask.WithTimeout(TimeSpan.FromSeconds(2));

        session.CurrentState.Should().Be("Connected", "an N1 of zero is a limit, not a fault: the link comes up");
        session.Context.ParametersNegotiated.Should().BeTrue();
        session.Context.N1.Should().Be(0, "4 bits is under one octet, and the spec sets no minimum to floor it at");

        // Nothing can be sent over it, and the refusal says why.
        var act = () => listener.SendData(session, new byte[] { 0x48, 0x69 });
        act.Should().Throw<InvalidOperationException>().WithMessage("*N1 is 0*");
        modem.SentFrames.Count.Should().Be(2, "no I frame went out: the XID command and the SABM are all the dial sent");
    }
}
