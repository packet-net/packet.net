using AwesomeAssertions;
using Packet.Ax25.Session;
using Packet.Core;
using Xunit;

namespace Packet.Ax25.Tests.Session;

/// <summary>
/// Both stations dial each other at once, and the peer's SABM or SABME reaches us while our
/// dial is still in its pre-connect XID probe. figc4.1 answers it (UA, DL-CONNECT indication,
/// Connected), so the link the dial is for is already up. At the version the dial offered, the
/// dial returns that link. It used to post DL-CONNECT request anyway once the probe ended,
/// which put our own SABM(E) on the air and reset the link the peer had just set up (§6.3.3,
/// §6.5): two SABM(E)s where §6.3.6.2's crossing of the same two frames ends with one link
/// and no reset.
/// </summary>
public sealed class Ax25ListenerPeerCallDuringXidTests
{
    private static readonly Callsign Local = new("N0AAA", 3);
    private static readonly Callsign Peer = new("N0BBB", 9);

    private static List<Ax25Frame> Sent(LoopbackModem modem) =>
        modem.SentFrames.SnapshotList()
            .Select(b => Ax25Frame.TryParse(b.Span, Ax25ParseOptions.Lenient, out var f) ? f : null)
            .Where(f => f is not null)
            .Select(f => f!)
            .ToList();

    private static bool IsEstablish(Ax25Frame f) => f.FrameType is Ax25FrameType.Sabm or Ax25FrameType.Sabme;

    private static Ax25Frame Call(bool sabme) => sabme ? Ax25Frame.Sabme(Local, Peer) : Ax25Frame.Sabm(Local, Peer);

    // The peer's answer to our probe: the same parameters back, as a responder that accepts
    // everything offered sends them.
    private static Ax25Frame XidAnswer(Ax25Frame command) =>
        Ax25Frame.Xid(Local, Peer, command.Info.Span, isCommand: false, pollFinal: true);

    // Start a dial with the probe on and wait until its XID command is on the air.
    private static async Task<(Ax25Listener listener, LoopbackModem modem, Task<Ax25Session> dial, Ax25Frame xid)> DialIntoProbeAsync(
        bool extended, TimeSpan? t1v = null)
    {
        var modem = new LoopbackModem();
        var listener = new Ax25Listener(modem, new Ax25ListenerOptions { MyCall = Local, T1V = t1v });
        await listener.StartAsync();

        var dial = listener.ConnectAsync(Peer, Local, extended, preConnectXidNegotiatesSrej: true);
        await modem.SentFrames.WaitForCountAsync(1, TimeSpan.FromSeconds(5));
        var xid = Sent(modem)[0];
        xid.FrameType.Should().Be(Ax25FrameType.Xid, "the dial opens with its pre-connect XID probe");
        return (listener, modem, dial, xid);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_link_the_peer_brings_up_during_the_probe_is_returned_without_our_own_SABM(bool extended)
    {
        var (listener, modem, dial, xid) = await DialIntoProbeAsync(extended);
        await using var _ = listener;

        // The peer is dialling us too: its call was on the air before our XID command reached
        // it, and it answers the command after.
        modem.InjectInbound(Call(sabme: extended));
        modem.InjectInbound(XidAnswer(xid));

        var session = await dial.WithTimeout(TimeSpan.FromSeconds(10));

        session.CurrentState.Should().Be("Connected");
        session.Context.IsExtended.Should().Be(extended);
        Sent(modem).Should().Contain(f => f.FrameType == Ax25FrameType.Ua && !f.IsCommand, "figc4.1 answers the peer's call");
        Sent(modem).Count(IsEstablish).Should().Be(0, "the link is up, so the dial has nothing to establish and no reason to reset it");
    }

    [Fact]
    public async Task A_peer_that_calls_but_never_answers_the_XID_gets_its_link_returned_on_go_back_N()
    {
        // The probe runs its (short) budget out, as it would for any peer that does not answer
        // XID, and the mod-8 link the peer set up is returned with the unanswered SREJ offer
        // withdrawn.
        var (listener, modem, dial, _) = await DialIntoProbeAsync(extended: false, t1v: TimeSpan.FromMilliseconds(400));
        await using var _ = listener;

        modem.InjectInbound(Call(sabme: false));

        var session = await dial.WithTimeout(TimeSpan.FromSeconds(10));

        session.CurrentState.Should().Be("Connected");
        Sent(modem).Count(IsEstablish).Should().Be(0);
        session.Context.IsExtended.Should().BeFalse();
        session.Context.SrejEnabled.Should().BeFalse("nobody agreed to SREJ on this mod-8 link");
        session.Context.ImplicitReject.Should().BeTrue();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_peer_call_at_the_other_version_is_still_re_established(bool extended)
    {
        // Narrowness: a link the peer set up at the other version (a SABM answering our v2.2
        // dial, or a SABME our mod-8 one) is one our XID command offered the wrong version for,
        // so the two ends are not certain to agree about it. The dial re-establishes it, as it
        // always has, and both ends take the version from our frame.
        var (listener, modem, dial, _) = await DialIntoProbeAsync(extended, t1v: TimeSpan.FromMilliseconds(400));
        await using var _ = listener;

        modem.InjectInbound(Call(sabme: !extended));

        await ListenerTestSupport.WaitFor(() => Sent(modem).Any(IsEstablish), TimeSpan.FromSeconds(10),
            "the dial sends its own SABM(E) once the probe is over");
        var ours = Sent(modem).Single(IsEstablish);
        modem.InjectInbound(Ax25Frame.Ua(Local, Peer, finalBit: true));

        var session = await dial.WithTimeout(TimeSpan.FromSeconds(10));
        session.CurrentState.Should().Be("Connected");
        session.Context.IsExtended.Should().Be(ours.FrameType == Ax25FrameType.Sabme, "the link is at the version our frame set");
    }

    [Fact]
    public async Task The_returned_link_carries_data_both_ways()
    {
        var (listener, modem, dial, xid) = await DialIntoProbeAsync(extended: true);
        await using var _ = listener;
        modem.InjectInbound(Call(sabme: true));
        modem.InjectInbound(XidAnswer(xid));
        var session = await dial.WithTimeout(TimeSpan.FromSeconds(10));

        // The peer's first I frame is accepted in sequence (V(R) moves to 1), and ours goes out:
        // nothing was reset under either side.
        modem.InjectInbound(Ax25Frame.I(Local, Peer, nr: 0, ns: 0, "hello\r"u8, pollBit: true, extended: true));
        await ListenerTestSupport.WaitFor(
            () => Sent(modem).Any(f => f.FrameType == Ax25FrameType.Rr && !f.IsCommand),
            TimeSpan.FromSeconds(5), "the peer's poll is answered");
        session.Context.VR.Should().Be((byte)1);

        listener.SendData(session, "hi\r"u8.ToArray());
        await ListenerTestSupport.WaitFor(
            () => Sent(modem).Any(f => f.FrameType == Ax25FrameType.I),
            TimeSpan.FromSeconds(5), "our I frame goes out");
        Sent(modem).Count(IsEstablish).Should().Be(0);
    }

    [Fact]
    public async Task A_dial_on_a_link_that_was_already_up_still_re_establishes_it()
    {
        // Narrowness: only a link that came up UNDER the probe is returned as it is. A dial that
        // starts on a live link is asking for it to be set up again, as it always has.
        var modem = new LoopbackModem();
        await using var listener = new Ax25Listener(modem, new Ax25ListenerOptions { MyCall = Local });
        await listener.StartAsync();

        modem.InjectInbound(Ax25Frame.Sabme(Local, Peer));
        await ListenerTestSupport.WaitFor(
            () => Sent(modem).Any(f => f.FrameType == Ax25FrameType.Ua),
            TimeSpan.FromSeconds(5), "the peer's call is answered");

        var dial = listener.ConnectAsync(Peer, Local, extended: true, preConnectXidNegotiatesSrej: true);
        await ListenerTestSupport.WaitFor(
            () => Sent(modem).Any(f => f.FrameType == Ax25FrameType.Xid),
            TimeSpan.FromSeconds(5), "the probe's XID command goes out");
        modem.InjectInbound(XidAnswer(Sent(modem).First(f => f.FrameType == Ax25FrameType.Xid)));

        await ListenerTestSupport.WaitFor(
            () => Sent(modem).Any(f => f.FrameType == Ax25FrameType.Sabme),
            TimeSpan.FromSeconds(10), "the dial re-establishes the link that was up before it started");
        modem.InjectInbound(Ax25Frame.Ua(Local, Peer, finalBit: true));

        var session = await dial.WithTimeout(TimeSpan.FromSeconds(10));
        session.CurrentState.Should().Be("Connected");
    }
}
