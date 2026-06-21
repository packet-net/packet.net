using System.Collections.Concurrent;
using System.IO.Pipelines;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Packet.Ax25.Transport;
using Packet.Kiss;
using Packet.Node.Core.Transports;

namespace Packet.Node.Tests.Transports;

/// <summary>
/// The #464 fix end-to-end at the transport layer: a real <see cref="KissTcpClient"/>
/// whose far end goes <em>half-open</em> (the TNC/net-sim rebooted with no FIN, so
/// the read would otherwise hang forever) must self-heal through
/// <see cref="ReconnectingKissModem"/> — the read-idle timeout ends the dead
/// inner's stream, the wrapper re-dials a fresh KissTcpClient, and frames resume,
/// with the KISS parameters re-applied to the reconnected modem and the backoff
/// bounded.
/// </summary>
/// <remarks>
/// This deliberately wires the two real production pieces together
/// (KissTcpClient over a loopback duplex stream + ReconnectingKissModem) rather
/// than booting the whole web host — a focused component test, not a
/// WebApplicationFactory integration. The "dial" hands out KissTcpClients over
/// fresh in-memory pipe pairs, so each "connection" is independently severable.
/// </remarks>
public sealed class KissTcpReconnectIntegrationTests
{
    private static KissFrame Data(string s) => new(0, KissCommand.Data, Encoding.ASCII.GetBytes(s));

    [Fact]
    public async Task Half_open_link_self_heals_replays_params_and_resumes_with_bounded_backoff()
    {
        var time = new FakeTimeProvider();
        var idle = TimeSpan.FromMinutes(5);
        var minBackoff = TimeSpan.FromSeconds(1);
        var maxBackoff = TimeSpan.FromSeconds(30);

        // The first connection: a peer that goes silent and never closes — a
        // half-open link. The read-idle timeout is what rescues it.
        using var firstPeer = new LoopbackEndpoint(time, idle);
        // The second connection (after the re-dial): a live peer that delivers a
        // frame, proving the port came back to a working state. It is given a
        // very long idle window so the test's clock-advancing (which exists only
        // to trip the FIRST link's idle timeout) doesn't also drop the recovered
        // link before its queued frame is read.
        using var secondPeer = new LoopbackEndpoint(time, Timeout.InfiniteTimeSpan);
        await secondPeer.PeerWriteAsync(Data("recovered"));

        // firstPeer is the constructor's initial modem; only re-dials draw from
        // this queue (the next connection after the drop is secondPeer).
        var dials = new ConcurrentQueue<LoopbackEndpoint>();
        dials.Enqueue(secondPeer);

        var reconnectCount = 0;
        Func<CancellationToken, Task<IAx25Transport>> dial = _ =>
        {
            Interlocked.Increment(ref reconnectCount);
            if (!dials.TryDequeue(out var ep))
            {
                throw new IOException("no more endpoints provisioned");
            }
            return Task.FromResult<IAx25Transport>(ep.Client);
        };

        await using var modem = new ReconnectingKissModem(
            firstPeer.Client,
            dial,
            endpoint: "kiss-tcp:test",
            logger: NullLogger.Instance,
            timeProvider: time,
            minBackoff: minBackoff,
            maxBackoff: maxBackoff);

        // Configure KISS params before the drop — they must be replayed to the
        // reconnected modem (a fresh connection starts at the modem's defaults).
        await modem.SetTxDelayAsync(40);
        await modem.SetPersistenceAsync(63);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var got = new List<string>();
        var pump = Task.Run(async () =>
        {
            await foreach (var f in modem.ReceiveAsync(cts.Token))
            {
                got.Add(Encoding.ASCII.GetString(f.Ax25.Span));
                if (got.Count == 1) break;
            }
        });

        // Trip the first (dead) link's idle timeout so it ends and the wrapper
        // re-dials. Advance only until the single re-dial has happened — then
        // stop, so we don't perturb the recovered link's clock while its queued
        // frame is being read.
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(20);
        while (Volatile.Read(ref reconnectCount) == 0 && DateTime.UtcNow < deadline)
        {
            await Task.Delay(20);
            time.Advance(idle + TimeSpan.FromSeconds(1));
        }

        await pump.WaitAsync(TimeSpan.FromSeconds(10));

        got.Should().Equal("recovered");
        reconnectCount.Should().Be(1, "exactly one re-dial was needed to recover");

        // Params were replayed onto the second (reconnected) connection.
        secondPeer.LastTxDelay.Should().Be(40);
        secondPeer.LastPersistence.Should().Be(63);
    }

    [Fact]
    public async Task Reconnect_retries_a_refused_dial_with_bounded_backoff_then_recovers()
    {
        var time = new FakeTimeProvider();
        var idle = TimeSpan.FromMinutes(5);
        var minBackoff = TimeSpan.FromSeconds(1);
        var maxBackoff = TimeSpan.FromSeconds(8);

        using var firstPeer = new LoopbackEndpoint(time, idle);
        // Long idle window so the test's advancing only trips the dead first link.
        using var goodPeer = new LoopbackEndpoint(time, Timeout.InfiniteTimeSpan);
        await goodPeer.PeerWriteAsync(Data("back"));

        // The dial refuses twice (peer still rebooting) then succeeds — proving
        // the bounded-backoff retry loop, not just a single re-dial.
        var attempts = 0;
        Func<CancellationToken, Task<IAx25Transport>> dial = _ =>
        {
            var n = Interlocked.Increment(ref attempts);
            if (n <= 2)
            {
                throw new SocketExceptionLike();
            }
            return Task.FromResult<IAx25Transport>(goodPeer.Client);
        };

        await using var modem = new ReconnectingKissModem(
            firstPeer.Client, dial, "kiss-tcp:test", NullLogger.Instance,
            timeProvider: time, minBackoff: minBackoff, maxBackoff: maxBackoff);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var got = new List<string>();
        var pump = Task.Run(async () =>
        {
            await foreach (var f in modem.ReceiveAsync(cts.Token))
            {
                got.Add(Encoding.ASCII.GetString(f.Ax25.Span));
                if (got.Count == 1) break;
            }
        });

        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(20);
        while (!pump.IsCompleted && DateTime.UtcNow < deadline)
        {
            await Task.Delay(20);
            time.Advance(idle + maxBackoff + TimeSpan.FromSeconds(1));
        }

        await pump.WaitAsync(TimeSpan.FromSeconds(10));
        got.Should().Equal("back");
        attempts.Should().Be(3, "two refusals then a successful dial");
    }

    private sealed class SocketExceptionLike : IOException
    {
        public SocketExceptionLike() : base("connection refused (peer still rebooting)") { }
    }

    // A real KissTcpClient bound to a loopback duplex stream, plus the "peer" end
    // the test drives: write frames to the client, observe the KISS params the
    // client sent. The client carries the read-idle timeout under test.
    private sealed class LoopbackEndpoint : IDisposable
    {
        private readonly Stream peer;
        private readonly KissDecoder peerDecoder = new();

        public LoopbackEndpoint(TimeProvider time, TimeSpan idle)
        {
            var peerToClient = new Pipe();
            var clientToPeer = new Pipe();
            var clientStream = new DuplexStream(peerToClient.Reader.AsStream(), clientToPeer.Writer.AsStream());
            peer = new DuplexStream(clientToPeer.Reader.AsStream(), peerToClient.Writer.AsStream());
            Client = new KissTcpClient(clientStream, readIdleTimeout: idle, timeProvider: time);
            _ = DrainClientWritesAsync();
        }

        public KissTcpClient Client { get; }
        public byte? LastTxDelay { get; private set; }
        public byte? LastPersistence { get; private set; }

        public async Task PeerWriteAsync(KissFrame frame)
        {
            var wire = KissEncoder.Encode(frame.Port, frame.Command, frame.Payload);
            await peer.WriteAsync(wire);
            await peer.FlushAsync();
        }

        // Consume what the client writes (KISS param sets) so its writes never
        // block, and record the param values for assertion.
        private async Task DrainClientWritesAsync()
        {
            var buf = new byte[1024];
            try
            {
                while (true)
                {
                    var n = await peer.ReadAsync(buf);
                    if (n == 0) return;
                    foreach (var f in peerDecoder.Push(buf.AsSpan(0, n)))
                    {
                        switch (f.Command)
                        {
                            case KissCommand.TxDelay when f.Payload.Length == 1: LastTxDelay = f.Payload[0]; break;
                            case KissCommand.Persistence when f.Payload.Length == 1: LastPersistence = f.Payload[0]; break;
                        }
                    }
                }
            }
            catch
            {
                // peer torn down — fine
            }
        }

        public void Dispose()
        {
            peer.Dispose();
            Client.Dispose();
        }
    }

    // Binds a read half and a write half into one duplex Stream so a pair of
    // Pipes can stand in for a bidirectional socket. (Mirrors the helper in
    // Packet.Kiss.Tests; kept local to avoid a cross-test-project dependency.)
    private sealed class DuplexStream(Stream readSide, Stream writeSide) : Stream
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
}
