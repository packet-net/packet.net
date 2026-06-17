using AwesomeAssertions;
using Packet.Ax25.Session;
using Packet.Ax25.Xid;
using Packet.Core;
using Xunit;

namespace Packet.Ax25.Tests.Session;

/// <summary>
/// Pre-session XID-command handling on <see cref="Ax25Listener"/>.
///
/// A peer that does pre-SABM XID negotiation to us (the §6.3.2 "negotiate before
/// the connection" pattern — what a PDN NET/ROM mod-8 interlink initiator does)
/// sends an XID *command* with no active link yet. §4.3.3.7 makes answering an XID
/// command unconditional ("A station receiving an XID command returns an XID
/// response unless a UA is pending or a FRMR condition exists"); the MDL (Annex
/// C5.3) is a connection-independent machine, so the listener must answer even
/// with no cached session. The subsequent SABM then establishes a link that has
/// adopted the negotiated parameters (SREJ when both sides offered it).
///
/// Before the fix the pre-session XID fell through to a transient session, got
/// reclassified to all_other_commands, and was answered with a DM — stalling the
/// initiator.
/// </summary>
public class Ax25ListenerPreSessionXidTests
{
    private static readonly Callsign LocalCall = new("M0LTE", 0);
    private static readonly Callsign PeerCall  = new("G7XYZ", 7);

    // U-frame control bytes with the P/F bit (0x10) masked out.
    private const byte XidControl = 0xAF;
    private const byte UaControl  = 0x63;
    private const byte DmControl  = 0x0F;

    // U-frame: the low two control bits are both set (§4.3.3).
    private static bool IsUFrame(Ax25Frame f) => (f.Control & 0x03) == 0x03;
    private static bool IsXid(Ax25Frame f) => IsUFrame(f) && (f.Control & 0xEF) == XidControl;
    private static bool IsUa(Ax25Frame f)  => IsUFrame(f) && (f.Control & 0xEF) == UaControl;
    private static bool IsDm(Ax25Frame f)  => IsUFrame(f) && (f.Control & 0xEF) == DmControl;

    /// <summary>A mod-8 XID command offering SREJ — the offer a PDN interlink
    /// initiator puts on the wire before its SABM.</summary>
    private static Ax25Frame Mod8SrejXidCommand() => Ax25Frame.Xid(
        destination: LocalCall,
        source: PeerCall,
        info: XidInfoField.Encode(new XidParameters
        {
            HdlcOptionalFunctions = new HdlcOptionalFunctions
            {
                Reject = RejectMode.SelectiveReject,
                SrejMultiframe = true,
                Modulo128 = false,           // mod-8
            },
        }),
        isCommand: true,
        pollFinal: true);

    [Fact]
    public async Task Pre_session_XID_command_for_unknown_peer_is_answered_with_an_XID_response()
    {
        var modem = new LoopbackModem();
        await using var listener = new Ax25Listener(modem, new Ax25ListenerOptions
        {
            MyCall = LocalCall,
        });
        await listener.StartAsync();

        // No prior session — the peer opens with a bare XID command.
        modem.InjectInbound(Mod8SrejXidCommand());

        await modem.SentFrames.WaitForCountAsync(1, TimeSpan.FromSeconds(2));

        Ax25Frame.TryParse(modem.SentFrames[0].Span, out var reply).Should().BeTrue();
        IsXid(reply!).Should().BeTrue("a pre-session XID command must be answered with an XID frame, not a DM");
        IsDm(reply!).Should().BeFalse("the pre-session XID command must NOT fall through to the all_other_commands → DM path");
        reply!.IsResponse.Should().BeTrue("the answer is an XID *response* (figc5.1 responder path)");
        reply!.PollFinal.Should().BeTrue("the XID response carries F=1 so the initiator's figc5.2 F_eq_1 diamond fires");

        // The response advertises SREJ (the responder seeded SrejEnabled before
        // DefaultOfferFor ran, and the peer also offered SREJ so the merge kept it).
        XidInfoField.TryParse(reply!.Info.Span, out var responseParams).Should().BeTrue();
        responseParams!.HdlcOptionalFunctions!.Reject.Should().Be(RejectMode.SelectiveReject,
            "both sides offered SREJ, so the lesser-of merge keeps SREJ in the response");
    }

    [Fact]
    public async Task SABM_after_pre_session_XID_brings_session_to_Connected_with_SREJ_adopted()
    {
        var modem = new LoopbackModem();
        await using var listener = new Ax25Listener(modem, new Ax25ListenerOptions
        {
            MyCall = LocalCall,
        });

        var accepted = new TaskCompletionSource<Ax25Session>(TaskCreationOptions.RunContinuationsAsynchronously);
        listener.SessionAccepted += (_, e) => accepted.TrySetResult(e.Session);

        await listener.StartAsync();

        // 1) Pre-session XID command → XID response. SessionAccepted must NOT fire
        //    yet (no DL-CONNECT — the SABM raises it).
        modem.InjectInbound(Mod8SrejXidCommand());
        await modem.SentFrames.WaitForCountAsync(1, TimeSpan.FromSeconds(2));
        Ax25Frame.TryParse(modem.SentFrames[0].Span, out var xidReply).Should().BeTrue();
        IsXid(xidReply!).Should().BeTrue();
        accepted.Task.IsCompleted.Should().BeFalse("answering an XID command is not a connection — no SessionAccepted yet");

        // 2) The peer now sends SABM. The figc4.1 t14 "Set Version 2.0" clears only
        //    IsExtended; the staged SrejEnabled survives, so the connection adopts SREJ.
        modem.InjectInbound(Ax25Frame.Sabm(LocalCall, PeerCall));

        var session = await accepted.Task.WithTimeout(TimeSpan.FromSeconds(2));
        session.Context.Local.Should().Be(LocalCall);
        session.Context.Remote.Should().Be(PeerCall);
        session.CurrentState.Should().Be("Connected", "the SABM after the XID exchange establishes the link");

        // The SABM is answered with a UA (the establishment response), not a DM.
        await modem.SentFrames.WaitForCountAsync(2, TimeSpan.FromSeconds(2));
        Ax25Frame.TryParse(modem.SentFrames[1].Span, out var ua).Should().BeTrue();
        IsUa(ua!).Should().BeTrue("the SABM must be acknowledged with a UA");

        // The negotiated SREJ carried across the XID → SABM sequence: the connection
        // is SREJ-enabled because the cached session staged it and Set Version 2.0
        // left SrejEnabled untouched.
        session.Context.SrejEnabled.Should().BeTrue(
            "the XID-negotiated SREJ must survive into the established session");
        session.Context.ImplicitReject.Should().BeFalse();
    }
}
