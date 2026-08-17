using Packet.Kiss;

namespace Packet.Kiss.Tests;

/// <summary>
/// <see cref="KissTcpClient"/> serialises its writes, as <c>KissSerialModem</c>
/// always has. Concurrent writers are real - the AX.25 listener fires per-session
/// sends fire-and-forget from timer and pump threads, and
/// <c>ReconnectingKissModem</c> delegates without a gate of its own - and two
/// encoded frames in flight on one socket interleave as soon as a write is split
/// (packet-net/packet.net#696).
/// </summary>
public class KissTcpClientWriteSerialisationTests
{
    [Fact]
    public async Task A_second_send_waits_for_the_first_to_finish_writing()
    {
        var stream = new GatedStream();
        await using var client = new KissTcpClient(stream, readIdleTimeout: Timeout.InfiniteTimeSpan);

        var first = client.SendFrameAsync(new byte[] { 0x01, 0x02 });
        await stream.FirstWriteEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var second = client.SendFrameAsync(new byte[] { 0x03, 0x04 });
        await Task.Delay(100);

        stream.WritesEntered.Should().Be(1, "the second writer must wait for the first frame to be written out");

        stream.ReleaseFirstWrite();
        await first.WaitAsync(TimeSpan.FromSeconds(5));
        await second.WaitAsync(TimeSpan.FromSeconds(5));

        stream.WritesEntered.Should().Be(2);
        stream.Written.Should().HaveCount(2, "each send wrote exactly one complete frame");
    }

    /// <summary>A stream whose first write parks until the test releases it.</summary>
    private sealed class GatedStream : Stream
    {
        private readonly TaskCompletionSource firstWriteGate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly List<byte[]> written = [];
        private readonly object gate = new();
        private int entered;

        public TaskCompletionSource FirstWriteEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int WritesEntered => Volatile.Read(ref entered);

        public IReadOnlyList<byte[]> Written
        {
            get { lock (gate) { return written.ToArray(); } }
        }

        public void ReleaseFirstWrite() => firstWriteGate.TrySetResult();

        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            var n = Interlocked.Increment(ref entered);
            if (n == 1)
            {
                FirstWriteEntered.TrySetResult();
                await firstWriteGate.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            }

            lock (gate)
            {
                written.Add(buffer.ToArray());
            }
        }

        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush() { }

        public override Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
