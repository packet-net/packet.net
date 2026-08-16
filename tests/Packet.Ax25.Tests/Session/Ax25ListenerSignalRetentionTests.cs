using System.Collections;
using System.Reflection;
using AwesomeAssertions;
using Packet.Ax25.Session;
using Packet.Core;

namespace Packet.Ax25.Tests.Session;

/// <summary>
/// The listener's per-session signal queue is a rendezvous for an in-flight
/// outbound dial, not the delivery bus. It used to be fed unconditionally from
/// <c>SendUpward</c> - every upward DataLinkSignal, DL-DATA indications and their
/// freshly-allocated payload arrays included - while only
/// <see cref="Ax25Listener.ConnectAsync(Callsign, CancellationToken)"/> ever
/// dequeued. An inbound-accepted session therefore grew one retained entry per
/// received I-frame for as long as it stayed in the LRU cache
/// (packet-net/packet.net#696). It is now armed only for the duration of a dial,
/// never holds data indications, and is emptied when the dial finishes.
/// </summary>
public class Ax25ListenerSignalRetentionTests
{
    private static readonly Callsign LocalCall = new("M0LTE", 0);
    private static readonly Callsign PeerCall = new("G7XYZ", 7);

    /// <summary>
    /// Total entries queued across the listener's cached sessions. The queue is
    /// deliberately private (it is not part of the listener's contract), and its
    /// <em>contents</em> are exactly what regressed, so the assertion reaches for it
    /// by reflection rather than inferring from memory pressure.
    /// </summary>
    private static int QueuedSignalCount(Ax25Listener listener)
    {
        var field = typeof(Ax25Listener).GetField("sessions", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("Ax25Listener.sessions not found");
        var sessions = (IDictionary)(field.GetValue(listener) ?? throw new InvalidOperationException("sessions was null"));

        var total = 0;
        foreach (DictionaryEntry entry in sessions)
        {
            var cached = entry.Value!;
            var queue = cached.GetType().GetProperty("Signals")!.GetValue(cached)!;
            total += ((ICollection)queue).Count;
        }
        return total;
    }

    [Fact]
    public async Task An_inbound_accepted_session_retains_no_signals_for_its_traffic()
    {
        var modem = new LoopbackModem();
        await using var listener = new Ax25Listener(modem, new Ax25ListenerOptions { MyCall = LocalCall });

        var delivered = 0;
        var accepted = new TaskCompletionSource<Ax25Session>(TaskCreationOptions.RunContinuationsAsynchronously);
        listener.SessionAccepted += (_, e) =>
        {
            e.Session.DataLinkSignalEmitted += (_, sig) =>
            {
                if (sig is DataLinkDataIndication)
                {
                    Interlocked.Increment(ref delivered);
                }
            };
            accepted.TrySetResult(e.Session);
        };

        await listener.StartAsync();
        modem.InjectInbound(Ax25Frame.Sabm(LocalCall, PeerCall));
        var session = await accepted.Task.WithTimeout(TimeSpan.FromSeconds(2));
        session.CurrentState.Should().Be("Connected");

        const int frames = 400;
        var payload = new byte[200];
        Array.Fill(payload, (byte)0x41);
        for (var i = 0; i < frames; i++)
        {
            modem.InjectInbound(Ax25Frame.I(
                destination: LocalCall, source: PeerCall,
                nr: 0, ns: (byte)(i % 8), info: payload));
        }

        await ListenerTestSupport.WaitFor(
            () => Volatile.Read(ref delivered) >= frames, TimeSpan.FromSeconds(20),
            "the consumer should receive every DL-DATA indication");

        // The consumer got all 400 through the event; the listener kept none.
        QueuedSignalCount(listener).Should().Be(0,
            "the queue serves an in-flight dial only - nothing is retained for an inbound session");
    }

    [Fact]
    public async Task A_finished_dial_leaves_nothing_queued()
    {
        // The outbound path: the dial arms the queue, drains its confirm, and the
        // disarm empties whatever the SDL raised afterwards.
        var modem = new LoopbackModem();
        await using var listener = new Ax25Listener(modem, new Ax25ListenerOptions { MyCall = LocalCall });
        await listener.StartAsync();

        var dial = listener.ConnectAsync(PeerCall, LocalCall, extended: false, preConnectXidNegotiatesSrej: false);
        await modem.SentFrames.WaitForCountAsync(1, TimeSpan.FromSeconds(2));

        // Answer the SABM so the dial completes on DL-CONNECT-confirm.
        modem.InjectInbound(Ax25Frame.Ua(LocalCall, PeerCall, finalBit: true));
        var session = await dial.WithTimeout(TimeSpan.FromSeconds(5));
        session.CurrentState.Should().Be("Connected");

        modem.InjectInbound(Ax25Frame.I(
            destination: LocalCall, source: PeerCall, nr: 0, ns: 0, info: "hello"u8));
        await ListenerTestSupport.WaitFor(
            () => QueuedSignalCount(listener) == 0, TimeSpan.FromSeconds(5),
            "post-dial traffic must not accumulate");

        QueuedSignalCount(listener).Should().Be(0);
    }
}
