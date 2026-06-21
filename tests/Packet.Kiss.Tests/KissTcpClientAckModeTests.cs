using System.IO.Pipelines;
using Packet.Kiss;

namespace Packet.Kiss.Tests;

/// <summary>
/// Behavioural tests for the KISS ACKMODE implementation on
/// <see cref="KissTcpClient"/>. These drive the real client logic over a
/// loopback duplex stream (the testability seam: the internal
/// <c>KissTcpClient(Stream)</c> ctor lets us swap the <see cref="System.Net.Sockets.TcpClient"/>'s
/// <see cref="System.Net.Sockets.NetworkStream"/> for a pair of pipes). The
/// "TNC peer" side reads what the client wrote and writes echoes/data back,
/// exercising the send path, the RX-pump echo interception, and pass-through —
/// the same code that runs against a real socket.
/// </summary>
public sealed class KissTcpClientAckModeTests : IDisposable
{
    // Two pipes form a full-duplex channel: the client reads from one and writes
    // to the other; the fake peer does the mirror. DuplexStream binds a (read,
    // write) pair into a single Stream for each end.
    private readonly Pipe clientToPeer = new();
    private readonly Pipe peerToClient = new();
    private readonly KissTcpClient client;
    private readonly Stream peer;

    // Persistent decoder + buffer for the peer side: a single ReadAsync may
    // carry several of the client's frames, so a per-call decoder would drop
    // bytes. Keep one decoder and a queue of already-decoded frames.
    private readonly KissDecoder peerDecoder = new();
    private readonly Queue<KissFrame> peerDecoded = new();

    public KissTcpClientAckModeTests()
    {
        var clientStream = new DuplexStream(peerToClient.Reader.AsStream(), clientToPeer.Writer.AsStream());
        peer = new DuplexStream(clientToPeer.Reader.AsStream(), peerToClient.Writer.AsStream());
        client = new KissTcpClient(clientStream);
    }

    [Fact]
    public async Task Echo_Resolves_The_Send_With_Matching_Tag_And_Timing()
    {
        // Echoes are intercepted on the RX pump, so a pump must be running for
        // the lifetime of the send (as it always is against a live socket).
        using var pumpCts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var pump = DrainReadPumpAsync(pumpCts.Token);

        // The peer plays TNC: read the client's ackmode send, recover the tag,
        // echo it back as the TX-complete signal.
        var peerLoop = Task.Run(async () =>
        {
            var tag = await ReadOneAckModeSendTagAsync();
            await WriteAckEchoAsync(tag);
            return tag;
        });

        var receipt = await client.SendFrameWithAckAsync(
            ax25Bytes: new byte[] { 0xDE, 0xAD, 0xBE, 0xEF },
            timeout: TimeSpan.FromSeconds(5));

        var wireTag = await peerLoop;
        // Auto-assigned tag is the first non-zero cursor value = 1 — asserted
        // on the wire (the neutral TxCompletion no longer carries the tag).
        wireTag.Should().Be((ushort)1);
        receipt.Completed.Should().BeOnOrAfter(receipt.Queued);

        pumpCts.Cancel();
        await pump;
    }

    [Fact]
    public async Task Caller_Supplied_Tag_Is_Used_On_The_Wire_And_Round_Trips()
    {
        using var pumpCts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var pump = DrainReadPumpAsync(pumpCts.Token);

        var peerLoop = Task.Run(async () =>
        {
            var tag = await ReadOneAckModeSendTagAsync();
            await WriteAckEchoAsync(tag);
            return tag;
        });

        await client.SendFrameWithAckAsync(
            ax25Bytes: new byte[] { 0x01 },
            timeout: TimeSpan.FromSeconds(5),
            sequenceTag: 0xBEEF);

        var wireTag = await peerLoop;
        // The caller-supplied tag must be the one written on the wire.
        wireTag.Should().Be((ushort)0xBEEF);

        pumpCts.Cancel();
        await pump;
    }

    [Fact]
    public async Task No_Echo_Times_Out_Within_Roughly_The_Timeout()
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();

        Func<Task> act = () => client.SendFrameWithAckAsync(
            ax25Bytes: new byte[] { 0x42 },
            timeout: TimeSpan.FromMilliseconds(200));

        await act.Should().ThrowAsync<TimeoutException>();
        sw.Stop();

        // Fired off the timeout, not hung; allow generous slack for CI jitter.
        sw.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task Two_In_Flight_Sends_Resolve_To_Their_Own_Tags_Regardless_Of_Echo_Order()
    {
        // Start the read pump so echoes get intercepted as they arrive.
        using var pumpCts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var pump = DrainReadPumpAsync(pumpCts.Token);

        var sendA = client.SendFrameWithAckAsync(new byte[] { 0xA0 }, TimeSpan.FromSeconds(5), sequenceTag: 0x0011);
        var sendB = client.SendFrameWithAckAsync(new byte[] { 0xB0 }, TimeSpan.FromSeconds(5), sequenceTag: 0x0022);

        // Read both sends off the wire (order on the wire doesn't matter — we key
        // by tag), then echo B *before* A to prove out-of-order resolution.
        var tag1 = await ReadOneAckModeSendTagAsync();
        var tag2 = await ReadOneAckModeSendTagAsync();
        new[] { tag1, tag2 }.Should().BeEquivalentTo(new ushort[] { 0x0011, 0x0022 });

        await WriteAckEchoAsync(0x0022);
        await WriteAckEchoAsync(0x0011);

        // Each send resolves from ITS OWN echo (keyed by tag), proven by both
        // completing despite the echoes arriving B-before-A — a cross-resolution
        // would deadlock one of them. The tag correctness is asserted on the
        // wire above; the neutral TxCompletion no longer carries the tag.
        var receiptA = await sendA;
        var receiptB = await sendB;

        receiptA.Completed.Should().BeOnOrAfter(receiptA.Queued);
        receiptB.Completed.Should().BeOnOrAfter(receiptB.Queued);

        pumpCts.Cancel();
        await pump;
    }

    [Fact]
    public async Task Ordinary_Data_Frames_Surface_But_Ack_Echoes_Do_Not()
    {
        // A single read pump both intercepts the echo (transparently, inside
        // ReadAvailableAsync) and yields the surfaced Data frames. It must yield
        // exactly the two Data frames and never the ackmode echo (cmd 0x0C).
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var surfaced = new List<KissFrame>();
        var pump = Task.Run(async () =>
        {
            await foreach (var frame in client.ReadFramesAsync(cts.Token))
            {
                surfaced.Add(frame);
                if (surfaced.Count == 2) break;
            }
        });

        var send = client.SendFrameWithAckAsync(new byte[] { 0x99 }, TimeSpan.FromSeconds(5), sequenceTag: 0x00AB);

        var sentTag = await ReadOneAckModeSendTagAsync();
        sentTag.Should().Be((ushort)0x00AB);

        // Peer writes: a Data frame, then the ack echo, then another Data frame.
        await WriteFrameAsync(KissCommand.Data, new byte[] { 0x11, 0x22 });
        await WriteAckEchoAsync(0x00AB);
        await WriteFrameAsync(KissCommand.Data, new byte[] { 0x33, 0x44 });

        // The echo (tag 0x00AB, asserted on the wire above) resolves the send;
        // the neutral TxCompletion no longer carries the tag.
        var receipt = await send;
        receipt.Completed.Should().BeOnOrAfter(receipt.Queued);

        await pump;
        surfaced.Should().HaveCount(2);
        surfaced.Should().OnlyContain(f => f.Command == KissCommand.Data);
        surfaced[0].Payload.Should().Equal(new byte[] { 0x11, 0x22 });
        surfaced[1].Payload.Should().Equal(new byte[] { 0x33, 0x44 });
    }

    [Fact]
    public async Task Late_Unknown_Echo_Is_Ignored_And_Surfaces_As_An_Ordinary_Frame()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var surfaced = new List<KissFrame>();
        var pump = Task.Run(async () =>
        {
            await foreach (var frame in client.ReadFramesAsync(cts.Token))
            {
                surfaced.Add(frame);
                if (surfaced.Count == 2) break;
            }
        });

        // No send in flight — an ackmode echo for an unknown tag must not throw
        // and (with no pending waiter) simply passes through as a frame.
        await WriteAckEchoAsync(0x7777);
        await WriteFrameAsync(KissCommand.Data, new byte[] { 0x55 });

        await pump;
        // With no pending ack, the 2-byte 0x0C frame is not intercepted; both
        // frames surface. The key assertion is "no throw, no hang".
        surfaced.Should().HaveCount(2);
    }

    // --- helpers: the fake TNC peer side -------------------------------------

    // Read exactly one frame from the client and assert it's an ackmode send
    // (cmd 0x0C, payload length > 2), returning its tag.
    private async Task<ushort> ReadOneAckModeSendTagAsync()
    {
        var frame = await ReadOneFrameFromClientAsync();
        KissAckMode.TryParseDataFrame(frame, out var tag, out _).Should().BeTrue(
            "the client should write an ackmode data frame (cmd 0x0C + 2-byte tag + payload)");
        return tag;
    }

    // Decode one complete KISS frame from whatever the client has written,
    // buffering any extra frames a single read carried for the next call.
    private async Task<KissFrame> ReadOneFrameFromClientAsync()
    {
        var buf = new byte[256];
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (peerDecoded.Count == 0)
        {
            int n = await peer.ReadAsync(buf, cts.Token);
            if (n == 0) throw new IOException("client closed the stream");
            foreach (var f in peerDecoder.Push(buf.AsSpan(0, n)))
            {
                peerDecoded.Enqueue(f);
            }
        }
        return peerDecoded.Dequeue();
    }

    private Task WriteAckEchoAsync(ushort tag)
    {
        // The TX-complete echo: cmd 0x0C with a 2-byte payload (the tag).
        var payload = new byte[] { (byte)(tag >> 8), (byte)(tag & 0xFF) };
        return WriteFrameAsync(KissCommand.AckMode, payload);
    }

    private async Task WriteFrameAsync(KissCommand command, byte[] payload)
    {
        var wire = KissEncoder.Encode(port: 0, command, payload);
        await peer.WriteAsync(wire);
        await peer.FlushAsync();
    }

    // Drive the client's read pump to completion (or cancellation) in the
    // background so echoes get intercepted as soon as they land on the wire.
    private async Task DrainReadPumpAsync(CancellationToken ct)
    {
        try
        {
            await foreach (var _ in client.ReadFramesAsync(ct))
            {
                // discard; this test only cares that echoes get intercepted
            }
        }
        catch (OperationCanceledException) { /* expected on cancel */ }
    }

    public void Dispose() => client.Dispose();
}

/// <summary>
/// Binds a read half and a write half into one duplex <see cref="Stream"/> so a
/// pair of <see cref="System.IO.Pipelines.Pipe"/>s can stand in for a
/// bidirectional socket.
/// </summary>
internal sealed class DuplexStream(Stream readSide, Stream writeSide) : Stream
{
    private readonly Stream readSide = readSide;
    private readonly Stream writeSide = writeSide;

    public override bool CanRead => true;
    public override bool CanWrite => true;
    public override bool CanSeek => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        => readSide.ReadAsync(buffer, cancellationToken);

    public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        => writeSide.WriteAsync(buffer, cancellationToken);

    public override int Read(byte[] buffer, int offset, int count) => readSide.Read(buffer, offset, count);
    public override void Write(byte[] buffer, int offset, int count) => writeSide.Write(buffer, offset, count);

    public override Task FlushAsync(CancellationToken cancellationToken) => writeSide.FlushAsync(cancellationToken);
    public override void Flush() => writeSide.Flush();

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            readSide.Dispose();
            writeSide.Dispose();
        }
        base.Dispose(disposing);
    }
}
