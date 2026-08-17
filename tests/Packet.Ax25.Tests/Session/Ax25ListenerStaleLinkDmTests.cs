using AwesomeAssertions;
using Packet.Ax25.Session;
using Packet.Core;
using Xunit;

namespace Packet.Ax25.Tests.Session;

/// <summary>
/// figc4.1 catch-all coverage for a peer that still believes a link we have already
/// torn down is up - the "stale link" case.
/// </summary>
/// <remarks>
/// <para>
/// The Listener caches sessions across disconnect (they keep their SRT / T1V history for
/// the next time that peer calls), so the ordinary state of affairs after a QSO ends is a
/// CACHED session sitting in Disconnected. A peer whose view of the link survived ours -
/// its DISC lost, our UA lost, or it simply never heard the teardown - then polls us with
/// an RR command carrying P=1. figc4.1 t05 (<c>all other commands</c>) answers that with a
/// DM, which clears the peer's link on the spot; staying silent instead makes the peer burn
/// its whole retry budget (LinBPQ: RETRIES x FRACK = 30 s of pointless polling on a shared
/// channel) before it gives up, and leaves it holding a link-table entry that changes how it
/// treats our next connection attempt.
/// </para>
/// <para>
/// Routing a cached-Disconnected session used to post the classifier's specific event
/// (<c>RrReceived</c>) straight into the session, where Disconnected has no transition for
/// it, so the frame was silently swallowed - while the very same frame from a peer we had
/// evicted from the cache got the correct DM. These tests pin the two halves of the fixed
/// rule: a <b>command</b> gets the t05 DM, a <b>response</b> gets t06's discard (answering a
/// response with a DM - itself a response - would have two disconnected stations trading DMs
/// forever).
/// </para>
/// </remarks>
public class Ax25ListenerStaleLinkDmTests
{
    private static readonly Callsign LocalCall = new("M0LTE", 0);
    private static readonly Callsign PeerCall = new("G7XYZ", 7);
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(2);

    /// <summary>
    /// Connect, tear the link down, then have the peer poll the (still cached) session with
    /// an RR command, P=1 - exactly what LinBPQ does to a link it has not been told about.
    /// The listener must answer DM with F=1.
    /// </summary>
    [Fact]
    public async Task Listener_Answers_Dm_When_Peer_Polls_A_Cached_Disconnected_Session()
    {
        var modem = new LoopbackModem();
        await using var listener = new Ax25Listener(modem, new Ax25ListenerOptions { MyCall = LocalCall });

        var accepted = new TaskCompletionSource<Ax25Session>(TaskCreationOptions.RunContinuationsAsynchronously);
        listener.SessionAccepted += (_, e) => accepted.TrySetResult(e.Session);
        await listener.StartAsync();

        // Establish, then tear down from the peer's side: SABM → UA, DISC → UA.
        modem.InjectInbound(Ax25Frame.Sabm(LocalCall, PeerCall));
        var session = await accepted.Task.WaitAsync(Budget);
        await modem.SentFrames.WaitForCountAsync(1, Budget);

        modem.InjectInbound(Ax25Frame.Disc(LocalCall, PeerCall));
        await modem.SentFrames.WaitForCountAsync(2, Budget);
        await ListenerTestSupport.WaitFor(
            () => session.CurrentState == "Disconnected", Budget, "the DISC must return us to Disconnected");

        // The peer never saw the teardown and polls the link it still believes in.
        modem.InjectInbound(Ax25Frame.Rr(LocalCall, PeerCall, nr: 3, isCommand: true, pollFinal: true));

        await modem.SentFrames.WaitForCountAsync(3, Budget);
        Ax25Frame.TryParse(modem.SentFrames[2].Span, out var dm).Should().BeTrue();
        (dm!.Control & 0xEF).Should().Be(0x0F,
            "figc4.1 t05 answers a command received in Disconnected with a DM, whether or not the " +
            "session is still in the listener's cache");
        dm.PollFinal.Should().BeTrue("t05 assigns F := P, and the poll carried P=1");
        dm.IsResponse.Should().BeTrue("a DM is always a response");
        dm.Destination.Callsign.Should().Be(PeerCall);
        dm.Source.Callsign.Should().Be(LocalCall);
    }

    /// <summary>
    /// The same stale-link shape, but the peer's frame is a RESPONSE (an RR final, e.g. the
    /// answer to a poll we sent before tearing the link down). figc4.1 has no DM-emitting
    /// input for a response - t06 discards it - so the listener must stay silent.
    /// </summary>
    [Fact]
    public async Task Listener_Stays_Silent_For_A_Response_To_A_Cached_Disconnected_Session()
    {
        var modem = new LoopbackModem();
        await using var listener = new Ax25Listener(modem, new Ax25ListenerOptions { MyCall = LocalCall });

        var accepted = new TaskCompletionSource<Ax25Session>(TaskCreationOptions.RunContinuationsAsynchronously);
        listener.SessionAccepted += (_, e) => accepted.TrySetResult(e.Session);
        await listener.StartAsync();

        modem.InjectInbound(Ax25Frame.Sabm(LocalCall, PeerCall));
        var session = await accepted.Task.WaitAsync(Budget);
        await modem.SentFrames.WaitForCountAsync(1, Budget);

        modem.InjectInbound(Ax25Frame.Disc(LocalCall, PeerCall));
        await modem.SentFrames.WaitForCountAsync(2, Budget);
        await ListenerTestSupport.WaitFor(
            () => session.CurrentState == "Disconnected", Budget, "the DISC must return us to Disconnected");

        modem.InjectInbound(Ax25Frame.Rr(LocalCall, PeerCall, nr: 3, isCommand: false, pollFinal: true));

        await Task.Delay(200);
        modem.SentFrames.Count.Should().Be(2,
            "a response frame in Disconnected is t06 (discard) - answering it with a DM would have " +
            "two disconnected stations trade DMs forever");
    }

    /// <summary>
    /// The unknown-peer half of the same rule: a DM (always a response) from a peer we have
    /// no session for must not be answered. Two nodes that both answered would ping-pong DMs
    /// for as long as they could hear each other.
    /// </summary>
    [Fact]
    public async Task Listener_Stays_Silent_For_A_Dm_From_An_Unknown_Peer()
    {
        var modem = new LoopbackModem();
        await using var listener = new Ax25Listener(modem, new Ax25ListenerOptions { MyCall = LocalCall });
        await listener.StartAsync();

        modem.InjectInbound(Ax25Frame.Dm(LocalCall, PeerCall, finalBit: true));

        await Task.Delay(200);
        modem.SentFrames.Count.Should().Be(0, "a DM is a response - t06 discards it, no reply");
    }
}
