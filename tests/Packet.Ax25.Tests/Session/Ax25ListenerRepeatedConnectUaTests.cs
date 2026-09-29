using AwesomeAssertions;
using Microsoft.Extensions.Time.Testing;
using Packet.Ax25.Session;
using Packet.Core;
using Xunit;

namespace Packet.Ax25.Tests.Session;

/// <summary>
/// #842, <see cref="Ax25SessionQuirks.RepeatedConnectUaIgnored"/>: the UA that answers a dial,
/// delivered twice. LinBPQ with two AXIP <c>MAP</c> lines for one address sends every frame
/// once per line, so the dialler sees UA, UA. The first connects the link; figc4.4 then reads
/// the second, in the connected state, as an unexpected UA (§6.5: "a TNC initiates a reset
/// procedure whenever it receives an unexpected UA response frame") and re-establishes, which
/// BPQ answers with two UAs again, for ever. The quirk drops a UA that is byte-for-byte the one
/// that just connected the link and is the very next frame the peer sends; anything else still
/// runs the figure.
/// </summary>
public sealed class Ax25ListenerRepeatedConnectUaTests
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

    private static async Task<(Ax25Listener listener, LoopbackModem modem, Task<Ax25Session> dial)> DialAsync(
        Ax25SessionQuirks quirks, bool extended)
    {
        var modem = new LoopbackModem();
        var listener = new Ax25Listener(modem, new Ax25ListenerOptions { MyCall = Local, Quirks = quirks });
        await listener.StartAsync();

        // `link: dial: v20` against BPQ is a plain SABM with no XID first; the v2.2 dial is the
        // same story with SABME.
        var dial = listener.ConnectAsync(Peer, Local, extended, preConnectXidNegotiatesSrej: false);
        await modem.SentFrames.WaitForCountAsync(1, TimeSpan.FromSeconds(5));
        Sent(modem)[0].Should().Match<Ax25Frame>(f => IsEstablish(f));
        return (listener, modem, dial);
    }

    // An RR poll after the frames under test: the pump handles inbound frames in order, so once
    // the answer to this poll is on the wire, everything before it has been dispatched.
    private static async Task FlushAsync(LoopbackModem modem, bool extended, byte nr = 0)
    {
        modem.InjectInbound(Ax25Frame.Rr(Local, Peer, nr: nr, isCommand: true, pollFinal: true, extended: extended));
        await ListenerTestSupport.WaitFor(
            () => Sent(modem).Any(f => f.FrameType == Ax25FrameType.Rr && !f.IsCommand),
            TimeSpan.FromSeconds(5), "the poll is answered");
    }

    [Theory]
    [InlineData(false, 2)]
    [InlineData(false, 3)]
    [InlineData(true, 2)]
    public async Task A_dial_answered_with_the_same_UA_more_than_once_stays_connected(bool extended, int copies)
    {
        var (listener, modem, dial) = await DialAsync(Ax25SessionQuirks.Default, extended);
        await using var _ = listener;

        var ua = Ax25Frame.Ua(Local, Peer, finalBit: true);
        for (int i = 0; i < copies; i++)
        {
            modem.InjectInbound(ua);
        }

        var session = await dial.WithTimeout(TimeSpan.FromSeconds(10));
        await FlushAsync(modem, extended);

        session.CurrentState.Should().Be("Connected", "the poll was answered on the link the first UA connected");
        Sent(modem).Count(IsEstablish).Should().Be(1, "the repeated UA is the one that connected the link, not a reason to reset it");
    }

    [Fact]
    public async Task Strictly_faithful_resets_on_the_repeated_UA_as_the_figure_draws()
    {
        // The paired test: with the quirk off, figc4.4's connected-state UA arm runs, DL-ERROR
        // and Establish Data Link, so a second SABM follows the second UA. Against BPQ with two
        // MAP lines that is the endless loop in the issue.
        var (listener, modem, dial) = await DialAsync(Ax25SessionQuirks.StrictlyFaithful, extended: false);
        await using var _ = listener;

        var ua = Ax25Frame.Ua(Local, Peer, finalBit: true);
        modem.InjectInbound(ua);
        modem.InjectInbound(ua);

        await dial.WithTimeout(TimeSpan.FromSeconds(10));
        await modem.SentFrames.WaitForCountAsync(2, TimeSpan.FromSeconds(5));

        Sent(modem)[1].FrameType.Should().Be(Ax25FrameType.Sabm, "the figure re-establishes on an unexpected UA");
        listener.ActiveSessions.Single().CurrentState.Should().Be("AwaitingConnection");
    }

    // #874, Ax25SessionQuirks.UnexpectedUaIgnored: on a link that is up, every UA is a late or
    // repeated answer to the SABM(E) that set it up, whatever came before it. The narrow #842
    // window is only what is left when that quirk is off.
    private static readonly Ax25SessionQuirks NarrowOnly = Ax25SessionQuirks.Default with { UnexpectedUaIgnored = false };

    [Fact]
    public async Task A_UA_after_other_traffic_from_the_peer_is_dropped_too()
    {
        var (listener, modem, dial) = await DialAsync(Ax25SessionQuirks.Default, extended: false);
        await using var _ = listener;

        var ua = Ax25Frame.Ua(Local, Peer, finalBit: true);
        modem.InjectInbound(ua);
        var session = await dial.WithTimeout(TimeSpan.FromSeconds(10));

        modem.InjectInbound(Ax25Frame.I(Local, Peer, nr: 0, ns: 0, "DAPPSv1>\r"u8));
        modem.InjectInbound(ua);
        await FlushAsync(modem, extended: false);

        session.CurrentState.Should().Be("Connected");
        session.Context.VR.Should().Be((byte)1, "the peer's data was taken and the UA after it changed nothing");
        Sent(modem).Count(IsEstablish).Should().Be(1, "a UA on an up link is never a reason to reset it");
    }

    [Fact]
    public async Task With_only_the_narrow_quirk_a_UA_after_other_traffic_still_resets_the_link()
    {
        // #842's window on its own: only the frame straight after the connecting UA can be its
        // repeat. A UA that follows other traffic is the figure's unexpected UA.
        var (listener, modem, dial) = await DialAsync(NarrowOnly, extended: false);
        await using var _ = listener;

        var ua = Ax25Frame.Ua(Local, Peer, finalBit: true);
        modem.InjectInbound(ua);
        var session = await dial.WithTimeout(TimeSpan.FromSeconds(10));

        modem.InjectInbound(Ax25Frame.I(Local, Peer, nr: 0, ns: 0, "DAPPSv1>\r"u8));
        modem.InjectInbound(ua);

        await ListenerTestSupport.WaitFor(() => Sent(modem).Count(IsEstablish) == 2, TimeSpan.FromSeconds(5),
            "a SABM follows the UA that came after the peer's data");
        session.CurrentState.Should().Be("AwaitingConnection");
    }

    [Fact]
    public async Task A_UA_that_differs_from_the_connecting_one_is_dropped_too()
    {
        var (listener, modem, dial) = await DialAsync(Ax25SessionQuirks.Default, extended: false);
        await using var _ = listener;

        modem.InjectInbound(Ax25Frame.Ua(Local, Peer, finalBit: true));
        var session = await dial.WithTimeout(TimeSpan.FromSeconds(10));
        modem.InjectInbound(Ax25Frame.Ua(Local, Peer, finalBit: false));
        await FlushAsync(modem, extended: false);

        session.CurrentState.Should().Be("Connected");
        Sent(modem).Count(IsEstablish).Should().Be(1, "the F bit does not make a UA on an up link mean anything else");
    }

    [Fact]
    public async Task With_only_the_narrow_quirk_a_UA_that_differs_still_resets_the_link()
    {
        // Byte-for-byte: a UA with F=0 is not a copy of the F=1 UA that connected the link.
        var (listener, modem, dial) = await DialAsync(NarrowOnly, extended: false);
        await using var _ = listener;

        modem.InjectInbound(Ax25Frame.Ua(Local, Peer, finalBit: true));
        var session = await dial.WithTimeout(TimeSpan.FromSeconds(10));
        modem.InjectInbound(Ax25Frame.Ua(Local, Peer, finalBit: false));

        await ListenerTestSupport.WaitFor(() => Sent(modem).Count(IsEstablish) == 2, TimeSpan.FromSeconds(5),
            "a different UA is the figure's unexpected UA");
        session.CurrentState.Should().Be("AwaitingConnection");
    }

    // packet.net#874's shape from A's side: a v2.2 dial whose T1 runs out once, so a second
    // SABME is queued behind a slow transmitter; the peer's own SABME crosses (answered UA); the
    // peer's UA to the first SABME returns the dial; both ends send at once; then the peer's UA to
    // the retry arrives, after the peer's data.
    private static async Task<(Ax25Listener listener, LoopbackModem modem, Ax25Session session, List<DataLinkSignal> signals)>
        CrossedDialWithARetryAsync(Ax25SessionQuirks quirks)
    {
        var modem = new LoopbackModem();
        var time = new FakeTimeProvider();
        var t1 = TimeSpan.FromSeconds(1);
        var listener = new Ax25Listener(modem, new Ax25ListenerOptions { MyCall = Local, Quirks = quirks, T1V = t1 }, time);
        await listener.StartAsync();

        var dial = listener.ConnectAsync(Peer, Local, extended: true, preConnectXidNegotiatesSrej: false);
        await ListenerTestSupport.WaitFor(() => Sent(modem).Count(IsEstablish) == 1, TimeSpan.FromSeconds(5), "the SABME is on the air");
        // T1 runs out once, on the fake clock, so exactly one retry follows and nothing else
        // can expire during the test.
        time.Advance(t1 + TimeSpan.FromMilliseconds(50));
        await ListenerTestSupport.WaitFor(() => Sent(modem).Count(IsEstablish) == 2, TimeSpan.FromSeconds(5),
            "T1 runs out once and the dial sends its SABME again");

        modem.InjectInbound(Ax25Frame.Sabme(Local, Peer));
        await ListenerTestSupport.WaitFor(() => Sent(modem).Any(f => f.FrameType == Ax25FrameType.Ua), TimeSpan.FromSeconds(5),
            "the crossing SABME is answered while the dial waits");

        var ua = Ax25Frame.Ua(Local, Peer, finalBit: true);
        modem.InjectInbound(ua);
        var session = await dial.WithTimeout(TimeSpan.FromSeconds(10));
        var signals = new List<DataLinkSignal>();
        session.DataLinkSignalEmitted += (_, sig) => { lock (signals) { signals.Add(sig); } };

        // What DAPPS does on crossed:true, at both ends.
        modem.InjectInbound(Ax25Frame.I(Local, Peer, nr: 0, ns: 0, "exchange\r"u8, extended: true));
        listener.SendData(session, "exchange\r"u8.ToArray());

        // The peer's answer to the retry (#857 re-acknowledged it), then its next frame.
        modem.InjectInbound(ua);
        modem.InjectInbound(Ax25Frame.I(Local, Peer, nr: 1, ns: 1, "mail\r"u8, extended: true));
        return (listener, modem, session, signals);
    }

    [Fact]
    public async Task A_UA_answering_the_dial_s_own_T1_retry_does_not_reset_the_link()
    {
        var (listener, modem, session, signals) = await CrossedDialWithARetryAsync(Ax25SessionQuirks.Default);
        await using var _ = listener;
        // The poll acknowledges our S0, which the peer's S1 R1 already had; an N(R) behind
        // V(a) would be the figure's N(R) error and a reset of its own.
        await FlushAsync(modem, extended: true, nr: 1);

        string Trace()
        {
            lock (signals)
            {
                return "sent: " + string.Join(", ", Sent(modem).Select(f => f.FrameType + (f.IsCommand ? " C" : " R")))
                    + "; signals: " + string.Join(", ", signals.Select(x => x.Name));
            }
        }

        session.CurrentState.Should().BeOneOf(new[] { "Connected", "TimerRecovery" }, Trace());
        session.Context.VR.Should().Be((byte)2, "both of the peer's I frames were taken, the one after the stale UA too");
        Sent(modem).Count(IsEstablish).Should().Be(2, "the T1 retry was the last SABME; the UA answering it changed nothing");
        lock (signals)
        {
            signals.Where(s => s is DataLinkConnectIndication or DataLinkErrorIndication).Should().BeEmpty(
                "nothing reset the link");
        }
    }

    [Fact]
    public async Task With_the_quirk_off_the_UA_answering_the_retry_resets_the_link_as_the_figure_draws()
    {
        // The paired test, and packet.net#874 on main: the UA after the peer's data is outside
        // #842's window, figc4.4 t17 runs (DL-ERROR K, Establish Data Link), a third SABME goes out
        // and the peer's next I frame is thrown away in AwaitingV22Connection.
        var (listener, modem, session, signals) = await CrossedDialWithARetryAsync(NarrowOnly);
        await using var _ = listener;

        await ListenerTestSupport.WaitFor(() => Sent(modem).Count(IsEstablish) == 3, TimeSpan.FromSeconds(5),
            "the figure re-establishes on the unexpected UA");
        session.CurrentState.Should().Be("AwaitingV22Connection");
        session.Context.VR.Should().Be((byte)1, "the I frame after the reset was discarded");
        lock (signals)
        {
            signals.Should().Contain(s => s is DataLinkErrorIndication);
        }
    }
}
