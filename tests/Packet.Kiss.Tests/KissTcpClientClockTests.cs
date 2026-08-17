using System.IO.Pipelines;
using Microsoft.Extensions.Time.Testing;
using Packet.Kiss;

namespace Packet.Kiss.Tests;

/// <summary>
/// The ACKMODE timing pair on <see cref="KissTcpClient"/> comes from the injected
/// <see cref="TimeProvider"/>. Both stamps used to read
/// <c>DateTimeOffset.UtcNow</c> even though the client already holds a clock,
/// which left the queued/completed pair undrivable from a test and out of step
/// with every other instant the client reads (packet-net/packet.net#696;
/// plan §2.7 requires <c>TimeProvider.GetUtcNow</c> for current time).
/// </summary>
public sealed class KissTcpClientClockTests : IDisposable
{
    private readonly Pipe clientToPeer = new();
    private readonly Pipe peerToClient = new();
    private readonly KissDecoder peerDecoder = new();
    private readonly FakeTimeProvider clock = new();
    private readonly KissTcpClient client;
    private readonly Stream peer;

    public KissTcpClientClockTests()
    {
        var clientStream = new DuplexStream(peerToClient.Reader.AsStream(), clientToPeer.Writer.AsStream());
        peer = new DuplexStream(clientToPeer.Reader.AsStream(), peerToClient.Writer.AsStream());
        client = new KissTcpClient(clientStream, readIdleTimeout: Timeout.InfiniteTimeSpan, timeProvider: clock);
    }

    [Fact]
    public async Task The_ackmode_completion_is_timed_on_the_injected_clock()
    {
        using var pumpCts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var pump = Task.Run(async () =>
        {
            try
            {
                await foreach (var _ in client.ReadFramesAsync(pumpCts.Token)) { }
            }
            catch (OperationCanceledException) { }
        });

        var queuedAt = clock.GetUtcNow();
        var send = client.SendFrameWithAckAsync(new byte[] { 0xDE, 0xAD }, TimeSpan.FromSeconds(30));

        // The tag is on the wire, so the queued instant has been stamped.
        var tag = await ReadOneAckModeSendTagAsync();

        // Only virtual time passes before the TNC reports the frame cleared.
        clock.Advance(TimeSpan.FromSeconds(7));
        await WriteFrameAsync(KissCommand.AckMode, [(byte)(tag >> 8), (byte)(tag & 0xFF)]);

        var receipt = await send.WaitAsync(TimeSpan.FromSeconds(10));
        receipt.Queued.Should().Be(queuedAt, "the submit instant is read from the injected clock");
        receipt.Completed.Should().Be(queuedAt + TimeSpan.FromSeconds(7),
            "the echo-arrival instant is read from the same clock");
        receipt.Elapsed.Should().Be(TimeSpan.FromSeconds(7));

        await pumpCts.CancelAsync();
        await pump;
    }

    private async Task<ushort> ReadOneAckModeSendTagAsync()
    {
        var buf = new byte[256];
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (true)
        {
            var n = await peer.ReadAsync(buf, cts.Token);
            if (n == 0)
            {
                throw new IOException("client closed the stream");
            }

            foreach (var frame in peerDecoder.Push(buf.AsSpan(0, n)))
            {
                if (KissAckMode.TryParseDataFrame(frame, out var tag, out _))
                {
                    return tag;
                }
            }
        }
    }

    private async Task WriteFrameAsync(KissCommand command, byte[] payload)
    {
        await peer.WriteAsync(KissEncoder.Encode(port: 0, command, payload));
        await peer.FlushAsync();
    }

    public void Dispose()
    {
        client.Dispose();
        peer.Dispose();
    }
}
