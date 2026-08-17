using AwesomeAssertions;
using Packet.Ax25.Session;
using Packet.Core;

namespace Packet.Ax25.Tests.Session;

/// <summary>
/// Routing an extended (modulo-128) supervisory frame on a port whose
/// <see cref="Ax25ListenerOptions.ParseOptions"/> reject an information field on an
/// S frame (<c>Strict</c>, and therefore <c>Xrouter</c>).
/// <para>
/// The inbound pump must parse before it can route, and cannot know the session
/// modulo until it has routed, so its routing parse is modulo-8. An extended
/// RR / RNR / REJ / SREJ carries a 2-octet control field, and read at modulo-8 the
/// second octet looks like an information field on an S frame - which §3.5 does not
/// permit, so a strict parse rejects the whole frame. SABME and UA are U frames
/// (one octet in both modes), so the link came up and then every acknowledgement was
/// dropped before trace and dispatch (packet-net/packet.net#696). The pump now
/// retries the parse at modulo-128, and accepts that reading only for an address
/// pair whose cached session is already extended.
/// </para>
/// </summary>
public class Ax25ListenerExtendedSupervisoryRoutingTests
{
    private static readonly Callsign LocalCall = new("M0LTE", 0);
    private static readonly Callsign PeerCall = new("G7XYZ", 7);

    private static async Task<Ax25Session> ConnectExtendedInbound(Ax25Listener listener, LoopbackModem modem)
    {
        var accepted = new TaskCompletionSource<Ax25Session>(TaskCreationOptions.RunContinuationsAsynchronously);
        listener.SessionAccepted += (_, e) => accepted.TrySetResult(e.Session);

        await listener.StartAsync();
        modem.InjectInbound(Ax25Frame.Sabme(LocalCall, PeerCall));

        var session = await accepted.Task.WithTimeout(TimeSpan.FromSeconds(2));
        await modem.SentFrames.WaitForCountAsync(1, TimeSpan.FromSeconds(2));
        session.CurrentState.Should().Be("Connected");
        session.Context.IsExtended.Should().BeTrue("SABME opens a modulo-128 link");
        return session;
    }

    [Fact]
    public async Task Strict_port_delivers_an_extended_supervisory_frame_on_a_sabme_link()
    {
        var modem = new LoopbackModem();
        await using var listener = new Ax25Listener(modem, new Ax25ListenerOptions
        {
            MyCall = LocalCall,
            ParseOptions = Ax25ParseOptions.Strict,
        });

        var session = await ConnectExtendedInbound(listener, modem);

        var traced = 0;
        listener.FrameTraced += (_, e) =>
        {
            if (e.Direction == FrameDirection.Received)
            {
                Interlocked.Increment(ref traced);
            }
        };

        // An RR command with P=1 is an enquiry: the SDL owes an S-frame response
        // with F=1. Before the fix this frame never got past the pump's parse.
        modem.InjectInbound(Ax25Frame.Rr(LocalCall, PeerCall, nr: 0, isCommand: true, pollFinal: true, extended: true));

        await modem.SentFrames.WaitForCountAsync(2, TimeSpan.FromSeconds(2));
        traced.Should().Be(1, "the extended RR must reach the monitor trace and the session");
        session.CurrentState.Should().Be("Connected");
    }

    [Fact]
    public async Task Strict_port_still_drops_an_extended_supervisory_frame_with_no_extended_session()
    {
        // The retry is not a widening of the port's options: with no live extended
        // session for the address pair, a frame the strict modulo-8 parse rejected
        // stays rejected - the port is deaf to it end to end.
        var modem = new LoopbackModem();
        await using var listener = new Ax25Listener(modem, new Ax25ListenerOptions
        {
            MyCall = LocalCall,
            ParseOptions = Ax25ParseOptions.Strict,
        });

        var traced = 0;
        listener.FrameTraced += (_, _) => Interlocked.Increment(ref traced);

        await listener.StartAsync();
        modem.InjectInbound(Ax25Frame.Rr(LocalCall, PeerCall, nr: 0, isCommand: true, pollFinal: true, extended: true));

        await Task.Delay(500);
        traced.Should().Be(0, "no cached extended session, so the strict rejection is final");
        modem.SentFrames.Count.Should().Be(0, "nothing was dispatched, so no DM went out");
        listener.IsRunning.Should().BeTrue();
    }

    [Fact]
    public async Task Strict_port_mod8_link_is_unchanged()
    {
        // The paired mod-8 case: a SABM link on the same strict port keeps handling
        // its own single-octet supervisory frames, and an extended-shaped one is not
        // smuggled in by the retry (the cached session is not extended).
        var modem = new LoopbackModem();
        await using var listener = new Ax25Listener(modem, new Ax25ListenerOptions
        {
            MyCall = LocalCall,
            ParseOptions = Ax25ParseOptions.Strict,
        });

        var accepted = new TaskCompletionSource<Ax25Session>(TaskCreationOptions.RunContinuationsAsynchronously);
        listener.SessionAccepted += (_, e) => accepted.TrySetResult(e.Session);
        await listener.StartAsync();
        modem.InjectInbound(Ax25Frame.Sabm(LocalCall, PeerCall));
        var session = await accepted.Task.WithTimeout(TimeSpan.FromSeconds(2));
        await modem.SentFrames.WaitForCountAsync(1, TimeSpan.FromSeconds(2));
        session.Context.IsExtended.Should().BeFalse();

        var traced = 0;
        listener.FrameTraced += (_, _) => Interlocked.Increment(ref traced);

        modem.InjectInbound(Ax25Frame.Rr(LocalCall, PeerCall, nr: 0, isCommand: true, pollFinal: true));
        await modem.SentFrames.WaitForCountAsync(2, TimeSpan.FromSeconds(2));

        await Task.Delay(200);
        var before = traced;
        modem.InjectInbound(Ax25Frame.Rr(LocalCall, PeerCall, nr: 0, isCommand: true, pollFinal: true, extended: true));
        await Task.Delay(400);
        traced.Should().Be(before,
            "the extended-shaped supervisory frame is still dropped on a mod-8 link");
    }

    [Fact]
    public async Task Lenient_port_keeps_delivering_extended_supervisory_frames()
    {
        // The pre-existing lenient path (parse at modulo-8 capturing the second
        // control octet as info, then re-parse at the session modulo) is untouched.
        var modem = new LoopbackModem();
        await using var listener = new Ax25Listener(modem, new Ax25ListenerOptions { MyCall = LocalCall });

        var session = await ConnectExtendedInbound(listener, modem);

        modem.InjectInbound(Ax25Frame.Rr(LocalCall, PeerCall, nr: 0, isCommand: true, pollFinal: true, extended: true));

        await modem.SentFrames.WaitForCountAsync(2, TimeSpan.FromSeconds(2));
        session.CurrentState.Should().Be("Connected");
    }
}
