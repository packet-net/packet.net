using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Packet.Kiss;
using Packet.Node.Core.Transports;

namespace Packet.Node.Tests.Transports;

/// <summary>
/// <see cref="ReconnectingKissModem"/> — the #50 fix. A kiss-tcp port must survive
/// the far end bouncing: when the inbound stream ends (a drop), the wrapper
/// re-dials a fresh inner modem and resumes, replaying the configured KISS
/// parameters; sends while the link is down are dropped, not thrown.
/// </summary>
public sealed class ReconnectingKissModemTests
{
    private static KissFrame Data(string s) => new(0, KissCommand.Data, Encoding.ASCII.GetBytes(s));

    [Fact]
    public async Task Reconnects_after_a_drop_and_keeps_delivering_frames()
    {
        var first = new FakeModem(endAfterFrames: true, Data("A"));     // yields A, then the stream ends = a drop
        var second = new FakeModem(endAfterFrames: false, Data("B"));   // yields B, then holds the link open
        var reconnects = 0;
        Func<CancellationToken, Task<IKissModem>> reconnect = _ =>
        {
            Interlocked.Increment(ref reconnects);
            return Task.FromResult<IKissModem>(second);
        };
        await using var modem = new ReconnectingKissModem(
            first, reconnect, "test", NullLogger.Instance, minBackoff: TimeSpan.Zero, maxBackoff: TimeSpan.Zero);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var got = new List<string>();
        await foreach (var f in modem.ReadFramesAsync(cts.Token).ConfigureAwait(false))
        {
            got.Add(Encoding.ASCII.GetString(f.Payload));
            if (got.Count == 2) break;   // A from the first modem, B from the reconnected one
        }

        got.Should().Equal("A", "B");
        reconnects.Should().Be(1);
    }

    [Fact]
    public async Task Replays_KISS_params_to_the_reconnected_modem()
    {
        var first = new FakeModem(endAfterFrames: true);                // no frames → drops immediately
        var second = new FakeModem(endAfterFrames: false, Data("X"));
        Func<CancellationToken, Task<IKissModem>> reconnect = _ => Task.FromResult<IKissModem>(second);
        await using var modem = new ReconnectingKissModem(
            first, reconnect, "test", NullLogger.Instance, minBackoff: TimeSpan.Zero, maxBackoff: TimeSpan.Zero);

        await modem.SetTxDelayAsync(30);
        await modem.SetTxTailAsync(5);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await foreach (var _ in modem.ReadFramesAsync(cts.Token).ConfigureAwait(false))
        {
            break;   // first frame off the reconnected modem
        }

        second.TxDelay.Should().Be(30);
        second.TxTail.Should().Be(5);
    }

    [Fact]
    public async Task Send_while_the_link_is_down_is_dropped_not_thrown()
    {
        var down = new FakeModem(endAfterFrames: false) { ThrowOnSend = true };
        Func<CancellationToken, Task<IKissModem>> reconnect = _ => Task.FromResult<IKissModem>(new FakeModem(false));
        await using var modem = new ReconnectingKissModem(down, reconnect, "test", NullLogger.Instance);

        // Must not throw — the link being down is a drop-and-retransmit, not a fault.
        var act = async () => await modem.SendFrameAsync(Encoding.ASCII.GetBytes("hi"));
        await act.Should().NotThrowAsync();
    }

    private sealed class FakeModem : IKissModem
    {
        private readonly KissFrame[] frames;
        private readonly bool endAfterFrames;

        public FakeModem(bool endAfterFrames, params KissFrame[] frames)
        {
            this.endAfterFrames = endAfterFrames;
            this.frames = frames;
        }

        public bool ThrowOnSend { get; init; }
        public byte? TxDelay { get; private set; }
        public byte? Persistence { get; private set; }
        public byte? SlotTime { get; private set; }
        public byte? TxTail { get; private set; }

        public async IAsyncEnumerable<KissFrame> ReadFramesAsync([EnumeratorCancellation] CancellationToken ct = default)
        {
            foreach (var f in frames)
            {
                ct.ThrowIfCancellationRequested();
                yield return f;
            }
            if (!endAfterFrames)
            {
                // Hold the link open until cancelled (a live modem with nothing more to say).
                try { await Task.Delay(Timeout.Infinite, ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { }
            }
            // else: returning ends the stream → the wrapper treats it as a drop
        }

        public Task SendFrameAsync(ReadOnlyMemory<byte> ax25Bytes, CancellationToken cancellationToken = default)
            => ThrowOnSend ? throw new IOException("link down") : Task.CompletedTask;

        public Task<AckModeReceipt> SendFrameWithAckAsync(
            ReadOnlyMemory<byte> ax25Bytes, TimeSpan? timeout = null, ushort? sequenceTag = null, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task SetTxDelayAsync(byte v, CancellationToken cancellationToken = default) { TxDelay = v; return Task.CompletedTask; }
        public Task SetPersistenceAsync(byte v, CancellationToken cancellationToken = default) { Persistence = v; return Task.CompletedTask; }
        public Task SetSlotTimeAsync(byte v, CancellationToken cancellationToken = default) { SlotTime = v; return Task.CompletedTask; }
        public Task SetTxTailAsync(byte v, CancellationToken cancellationToken = default) { TxTail = v; return Task.CompletedTask; }
    }
}
