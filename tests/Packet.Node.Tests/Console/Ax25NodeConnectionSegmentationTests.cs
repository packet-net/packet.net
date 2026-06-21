using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using AwesomeAssertions;
using Packet.Ax25;
using Packet.Ax25.Session;
using Packet.Ax25.Transport;
using Packet.Core;
using Packet.Node.Core.Console;
using Packet.Node.Tests.Support;
using Xunit;

namespace Packet.Node.Tests.Console;

/// <summary>
/// The RHP↔AX.25 bridge (<see cref="Ax25NodeConnection"/>) is a byte-stream sink: an
/// application <c>WriteAsync</c> larger than N1 must be framed into several ordinary
/// I-frames over a v2.0 link (no XID ⇒ no segmenter negotiated, e.g. LinBPQ). Before the
/// chunking fix the whole buffer went to <see cref="Ax25Listener.SendData"/>, which throws
/// <see cref="InvalidOperationException"/> on an over-N1 payload without the segmenter — the
/// exception escaped <c>WriteAsync</c> and surfaced as RHP errCode 17 "Not connected", so
/// the body never reached the wire and the forwarding cycle was torn down (the live GB7RDG
/// multi-frame-forwarding failure, 2026-06-12).
/// </summary>
public class Ax25NodeConnectionSegmentationTests
{
    private static readonly Callsign NodeCall = new("M0LTE", 0);
    private static readonly Callsign PeerCall = new("G7XYZ", 7);

    [Fact]
    public async Task WriteAsync_chunks_an_over_N1_body_into_ordinary_iframes_on_a_v20_link()
    {
        var (listener, modem, session) = await AcceptedV20Session();
        await using var _ = listener;

        var ua = modem.Sent.Length; // frames already sent (the UA)
        var conn = new Ax25NodeConnection(listener, session);

        // 755 bytes > N1 (256), distinct per byte so reassembly order is checked.
        var payload = Enumerable.Range(0, 755).Select(i => (byte)(i & 0xFF)).ToArray();

        // Before the fix this throws InvalidOperationException synchronously; after it,
        // the body is framed into 3 ordinary I-frames.
        await conn.WriteAsync(payload);

        await Wait.ForAsync(() => IFrames(modem, ua).Count >= 3, "3 I-frames carry the 755-byte body");
        var iFrames = IFrames(modem, ua);

        iFrames.Should().HaveCount(3, "ceil(755 / N1=256) = 3 ordinary I-frames");
        iFrames.Should().OnlyContain(f => f.Pid == Ax25Frame.PidNoLayer3, "a byte stream, not PID-0x08 segments");
        iFrames.Should().OnlyContain(f => f.Info.Length <= session.Context.N1, "each I-frame respects N1");
        iFrames.Select(f => (int)f.Ns).Should().Equal(new[] { 0, 1, 2 }); // frames go out in send order

        var reassembled = iFrames.SelectMany(f => f.Info.ToArray()).ToArray();
        reassembled.Should().Equal(payload, "the byte stream is preserved across the frames, in order");
    }

    [Fact]
    public async Task WriteAsync_passes_a_within_N1_body_as_a_single_iframe()
    {
        var (listener, modem, session) = await AcceptedV20Session();
        await using var _ = listener;

        var ua = modem.Sent.Length;
        var conn = new Ax25NodeConnection(listener, session);

        await conn.WriteAsync(new byte[200]);

        await Wait.ForAsync(() => IFrames(modem, ua).Count >= 1, "the body is a single I-frame");
        IFrames(modem, ua).Should().HaveCount(1, "a within-N1 body is one ordinary I-frame, unchanged");
    }

    private static List<Ax25Frame> IFrames(CapturingModem modem, int skip) =>
        modem.Sent.Skip(skip)
            .Select(b => { Ax25Frame.TryParse(b, out var f); return f!; })
            .Where(f => f is not null && Ax25FrameClassifier.Classify(f) is IFrameReceived)
            .ToList();

    /// <summary>Accept an inbound v2.0 SABM (no XID ⇒ segmenter off, N1=256) and return the Connected session.</summary>
    private static async Task<(Ax25Listener Listener, CapturingModem Modem, Ax25Session Session)> AcceptedV20Session()
    {
        var modem = new CapturingModem();
        var listener = new Ax25Listener(modem, new Ax25ListenerOptions { MyCall = NodeCall });

        var accepted = new TaskCompletionSource<Ax25Session>(TaskCreationOptions.RunContinuationsAsynchronously);
        listener.SessionAccepted += (_, e) => accepted.TrySetResult(e.Session);

        await listener.StartAsync();
        modem.InjectInbound(Ax25Frame.Sabm(NodeCall, PeerCall));
        var session = await accepted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await Wait.ForAsync(() => session.CurrentState == "Connected", "the inbound SABM is accepted");
        session.Context.SegmenterReassemblerEnabled.Should().BeFalse("a plain SABM negotiates no v2.2 segmenter");
        return (listener, modem, session);
    }

    /// <summary>An in-memory transport that records every frame the listener transmits and lets a test inject inbound frames.</summary>
    private sealed class CapturingModem : IAx25Transport, ICsmaChannelParams
    {
        private readonly ConcurrentQueue<byte[]> sent = new();
        private readonly Channel<Ax25InboundFrame> rx =
            Channel.CreateUnbounded<Ax25InboundFrame>(new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });

        public byte[][] Sent => sent.ToArray();

        public void InjectInbound(Ax25Frame frame) =>
            rx.Writer.TryWrite(new Ax25InboundFrame(frame.ToBytes(), 0, DateTimeOffset.UtcNow));

        public Task SendAsync(ReadOnlyMemory<byte> ax25, CancellationToken cancellationToken = default)
        {
            sent.Enqueue(ax25.ToArray());
            return Task.CompletedTask;
        }

        public async IAsyncEnumerable<Ax25InboundFrame> ReceiveAsync(
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            while (await rx.Reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
            {
                while (rx.Reader.TryRead(out var f))
                {
                    yield return f;
                }
            }
        }

        public Task SetTxDelayAsync(byte tenMsUnits, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task SetPersistenceAsync(byte value, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task SetSlotTimeAsync(byte tenMsUnits, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task SetTxTailAsync(byte tenMsUnits, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
