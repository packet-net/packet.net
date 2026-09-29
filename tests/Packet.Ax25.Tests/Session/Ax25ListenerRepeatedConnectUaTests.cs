using AwesomeAssertions;
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
    private static async Task FlushAsync(LoopbackModem modem, bool extended)
    {
        modem.InjectInbound(Ax25Frame.Rr(Local, Peer, nr: 0, isCommand: true, pollFinal: true, extended: extended));
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

    [Fact]
    public async Task A_UA_after_other_traffic_from_the_peer_still_resets_the_link()
    {
        // The quirk is narrow: only the frame straight after the connecting UA can be its
        // repeat. A UA that follows other traffic is the figure's unexpected UA (the peer may
        // really have reset, e.g. a retried SABM crossing our first UA after it sent data), and
        // the reset is what brings the two ends back into step.
        var (listener, modem, dial) = await DialAsync(Ax25SessionQuirks.Default, extended: false);
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
    public async Task A_UA_that_differs_from_the_connecting_one_still_resets_the_link()
    {
        // Byte-for-byte: a UA with F=0 is not a copy of the F=1 UA that connected the link.
        var (listener, modem, dial) = await DialAsync(Ax25SessionQuirks.Default, extended: false);
        await using var _ = listener;

        modem.InjectInbound(Ax25Frame.Ua(Local, Peer, finalBit: true));
        var session = await dial.WithTimeout(TimeSpan.FromSeconds(10));
        modem.InjectInbound(Ax25Frame.Ua(Local, Peer, finalBit: false));

        await ListenerTestSupport.WaitFor(() => Sent(modem).Count(IsEstablish) == 2, TimeSpan.FromSeconds(5),
            "a different UA is the figure's unexpected UA");
        session.CurrentState.Should().Be("AwaitingConnection");
    }
}
