using AwesomeAssertions;
using Microsoft.Extensions.Time.Testing;
using Packet.Core;
using Packet.Ax25.Session;
using Xunit;

namespace Packet.Ax25.Tests.Session;

/// <summary>
/// Regression coverage for packet-net/packet.net#327 — the figc4.x delayed
/// acknowledgement must actually reach the wire in the production wiring —
/// updated for the §6.7.1.2 T2 acknowledge delay (packet-net/packet.net#385).
///
/// The SDL's only path to a non-piggybacked ack runs through the link
/// multiplexer: an in-sequence I-frame received with P=0 and no ack already
/// pending emits <c>LM-SEIZE Request</c> + <c>Set Ack Pending</c> (figc4.3
/// t26); the RR is then sent by the <c>LM-SEIZE Confirm</c> transition
/// (t22 → <c>Enquiry Response (F=0)</c>). #327 made the listener grant the
/// seize (a stub swallowed it, so a session with no reply data NEVER
/// acknowledged and the peer FRACK-retried into link failure); #385 made
/// the grant T2-deferred, so back-to-back I-frames coalesce into one
/// cumulative RR instead of one RR keyup per frame. These tests pin the
/// composed behaviour: the ack still always reaches the wire, exactly once
/// per burst, T2 after the first unacknowledged frame.
///
/// Found hardware-testing the Rust port against a real LinBPQ
/// (pico-node#15); these tests are the C# equivalent of its
/// <c>idle_received_i_frame_is_still_acknowledged</c>.
/// </summary>
public class Ax25ListenerDelayedAckTests
{
    private static readonly Callsign LocalCall = new("M0LTE", 0);
    private static readonly Callsign PeerCall  = new("G7XYZ", 7);

    /// <summary>
    /// The marquee repro: peer connects, peer sends one I-frame (P=0),
    /// the local side has nothing to say back. An RR response with
    /// N(R)=1 must still go out — after the T2 acknowledge delay, with
    /// no per-frame RR before it. Red on the stubbed sendLinkMux: only
    /// the UA ever reaches the modem and the peer would retry into link
    /// failure.
    /// </summary>
    [Fact]
    public async Task Idle_received_I_frame_is_still_acknowledged()
    {
        var modem = new LoopbackModem();
        var time = new FakeTimeProvider();
        await using var listener = new Ax25Listener(modem, new Ax25ListenerOptions
        {
            MyCall = LocalCall,
        }, time);

        var accepted = new TaskCompletionSource<Ax25Session>(TaskCreationOptions.RunContinuationsAsynchronously);
        listener.SessionAccepted += (_, e) => accepted.TrySetResult(e.Session);
        await listener.StartAsync();

        // Peer connects: SABM → UA.
        modem.InjectInbound(Ax25Frame.Sabm(LocalCall, PeerCall));
        var session = await accepted.Task.WithTimeout(TimeSpan.FromSeconds(2));
        await modem.SentFrames.WaitForCountAsync(1, TimeSpan.FromSeconds(2));
        session.CurrentState.Should().Be("Connected");

        // Peer sends one in-sequence I-frame, P=0. We send nothing back.
        modem.InjectInbound(Ax25Frame.I(LocalCall, PeerCall, nr: 0, ns: 0,
            info: "QSL?"u8.ToArray(), pollBit: false));

        // The ack is held behind the §6.7.1.2 acknowledge delay: once the
        // frame has been processed (ack pending) nothing further is on the
        // wire yet.
        await ListenerTestSupport.WaitFor(
            () => session.Context.AcknowledgePending,
            TimeSpan.FromSeconds(2),
            "the received I-frame must set Ack-Pending");
        modem.SentFrames.Count.Should().Be(1, "the ack waits for T2 — no per-frame RR");

        // T2 expires → the delayed ack must reach the wire without any local
        // send and without waiting for the peer to poll: an RR response, N(R)=1.
        time.Advance(session.Context.T2 + TimeSpan.FromMilliseconds(1));
        await modem.SentFrames.WaitForCountAsync(2, TimeSpan.FromSeconds(2));

        var ack = ParseSent(modem, 1);
        IsRr(ack).Should().BeTrue(
            $"the frame after the UA must be the delayed-ack RR, got control 0x{ack.Control:X2}");
        ack.Nr.Should().Be(1, "the RR must acknowledge the received I-frame (N(R)=V(R)=1)");
        ack.IsResponse.Should().BeTrue("the figc4.7 Enquiry Response (F=0) sends a response frame");
        ack.PollFinal.Should().BeFalse("the delayed ack is F=0 — it is not answering a poll");
    }

    /// <summary>
    /// Same shape, two back-to-back I-frames before any ack flushes: the
    /// second frame arrives with an ack already pending (figc4.3 runs the
    /// seize path only once — t26's AckPending guard), and the T2-deferred
    /// grant (#385) coalesces them, so exactly ONE RR with the cumulative
    /// N(R)=2 goes out when T2 expires — not one per frame.
    /// </summary>
    [Fact]
    public async Task Back_to_back_idle_I_frames_get_one_cumulative_ack()
    {
        var modem = new LoopbackModem();
        var time = new FakeTimeProvider();
        await using var listener = new Ax25Listener(modem, new Ax25ListenerOptions
        {
            MyCall = LocalCall,
        }, time);

        var accepted = new TaskCompletionSource<Ax25Session>(TaskCreationOptions.RunContinuationsAsynchronously);
        listener.SessionAccepted += (_, e) => accepted.TrySetResult(e.Session);
        await listener.StartAsync();

        modem.InjectInbound(Ax25Frame.Sabm(LocalCall, PeerCall));
        var session = await accepted.Task.WithTimeout(TimeSpan.FromSeconds(2));
        await modem.SentFrames.WaitForCountAsync(1, TimeSpan.FromSeconds(2));

        modem.InjectInbound(Ax25Frame.I(LocalCall, PeerCall, nr: 0, ns: 0,
            info: "ONE"u8.ToArray(), pollBit: false));
        modem.InjectInbound(Ax25Frame.I(LocalCall, PeerCall, nr: 0, ns: 1,
            info: "TWO"u8.ToArray(), pollBit: false));

        // Both frames processed; the coalesced ack is still pending and
        // nothing beyond the UA has been transmitted.
        await ListenerTestSupport.WaitFor(
            () => session.Context.VR == 2,
            TimeSpan.FromSeconds(2),
            "both I-frames must be processed (V(R)=2)");
        modem.SentFrames.Count.Should().Be(1, "the coalesced ack waits for T2 — no per-frame RRs");

        // T2 expires → exactly one cumulative RR, N(R)=2.
        time.Advance(session.Context.T2 + TimeSpan.FromMilliseconds(1));
        await ListenerTestSupport.WaitFor(
            () => LastRrNr(modem) == 2,
            TimeSpan.FromSeconds(2),
            "both received I-frames must end up acknowledged (final RR N(R)=2)");

        var rrCount = modem.SentFrames.SnapshotList()
            .Select(b => { Ax25Frame.TryParse(b.Span, out var f); return f; })
            .Count(f => f is not null && IsRr(f));
        rrCount.Should().Be(1, "the burst coalesces into ONE cumulative ack (#385)");
    }

    private static Ax25Frame ParseSent(LoopbackModem modem, int index)
    {
        Ax25Frame.TryParse(modem.SentFrames[index].Span, out var frame).Should().BeTrue(
            $"sent frame [{index}] must parse as AX.25");
        return frame!;
    }

    /// <summary>S-frame RR test, mod-8: low nibble 0x01, and not a U/I frame.</summary>
    private static bool IsRr(Ax25Frame frame) => (frame.Control & 0x0F) == 0x01;

    private static int LastRrNr(LoopbackModem modem)
    {
        var frames = modem.SentFrames.SnapshotList();
        for (int i = frames.Count - 1; i >= 0; i--)
        {
            if (Ax25Frame.TryParse(frames[i].Span, out var f) && IsRr(f!))
            {
                return f!.Nr;
            }
        }
        return -1;
    }
}
