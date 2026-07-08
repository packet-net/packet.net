using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Packet.Ax25.Transport;
using Packet.Node.Core.Transports;

namespace Packet.Node.Tests.Transports;

/// <summary>
/// <see cref="ReconnectingKissModem"/> — the #50 fix. A kiss-tcp port must survive
/// the far end bouncing: when the inbound stream ends (a drop), the wrapper
/// re-dials a fresh inner transport and resumes, replaying the configured KISS
/// parameters; sends while the link is down are dropped, not thrown.
/// </summary>
public sealed class ReconnectingKissModemTests
{
    private static Ax25InboundFrame Data(string s) => new(Encoding.ASCII.GetBytes(s), 0, DateTimeOffset.UtcNow);

    [Fact]
    public async Task Reconnects_after_a_drop_and_keeps_delivering_frames()
    {
        var first = new FakeModem(endAfterFrames: true, Data("A"));     // yields A, then the stream ends = a drop
        var second = new FakeModem(endAfterFrames: false, Data("B"));   // yields B, then holds the link open
        var reconnects = 0;
        Func<CancellationToken, Task<IAx25Transport>> reconnect = _ =>
        {
            Interlocked.Increment(ref reconnects);
            return Task.FromResult<IAx25Transport>(second);
        };
        await using var modem = new ReconnectingKissModem(
            first, reconnect, "test", NullLogger.Instance, minBackoff: TimeSpan.Zero, maxBackoff: TimeSpan.Zero);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var got = new List<string>();
        await foreach (var f in modem.ReceiveAsync(cts.Token).ConfigureAwait(false))
        {
            got.Add(Encoding.ASCII.GetString(f.Ax25.Span));
            if (got.Count == 2)
            {
                break;   // A from the first modem, B from the reconnected one
            }
        }

        got.Should().Equal("A", "B");
        reconnects.Should().Be(1);
    }

    [Fact]
    public async Task Replays_KISS_params_to_the_reconnected_modem()
    {
        var first = new FakeModem(endAfterFrames: true);                // no frames → drops immediately
        var second = new FakeModem(endAfterFrames: false, Data("X"));
        Func<CancellationToken, Task<IAx25Transport>> reconnect = _ => Task.FromResult<IAx25Transport>(second);
        await using var modem = new ReconnectingKissModem(
            first, reconnect, "test", NullLogger.Instance, minBackoff: TimeSpan.Zero, maxBackoff: TimeSpan.Zero);

        await modem.SetTxDelayAsync(30);
        await modem.SetTxTailAsync(5);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await foreach (var _ in modem.ReceiveAsync(cts.Token).ConfigureAwait(false))
        {
            break;   // first frame off the reconnected modem
        }

        second.TxDelay.Should().Be(30);
        second.TxTail.Should().Be(5);
    }

    [Fact]
    public async Task Replays_an_explicit_zero_tx_tail_to_the_reconnected_modem()
    {
        // #465 composes with #483: once the supervisor always sets TXTAIL (implicit 0
        // default), the last-set value the wrapper replays onto a freshly re-dialed
        // modem includes that explicit 0 — a new connection that starts at the modem's
        // defaults is brought back to the deterministic 0 the operator's config implies.
        var first = new FakeModem(endAfterFrames: true);                // no frames → drops immediately
        var second = new FakeModem(endAfterFrames: false, Data("X"));
        Func<CancellationToken, Task<IAx25Transport>> reconnect = _ => Task.FromResult<IAx25Transport>(second);
        await using var modem = new ReconnectingKissModem(
            first, reconnect, "test", NullLogger.Instance, minBackoff: TimeSpan.Zero, maxBackoff: TimeSpan.Zero);

        await modem.SetTxTailAsync(0);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await foreach (var _ in modem.ReceiveAsync(cts.Token).ConfigureAwait(false))
        {
            break;   // first frame off the reconnected modem
        }

        second.TxTail.Should().Be((byte)0, "the explicit 0 is replayed, not skipped as 'unset'");
    }

    [Fact]
    public async Task IsReconnecting_is_true_exactly_while_the_link_is_down()
    {
        // The pdn_port_transport_reconnecting source (#583): false on a live link, true from the
        // drop until the fresh inner is live, false again after.
        var first = new FakeModem(endAfterFrames: true, Data("A"));     // yields A, then drops
        var second = new FakeModem(endAfterFrames: false, Data("B"));
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Func<CancellationToken, Task<IAx25Transport>> reconnect = async _ =>
        {
            await gate.Task.ConfigureAwait(false);                      // hold mid-reconnect
            return second;
        };
        await using var modem = new ReconnectingKissModem(
            first, reconnect, "test", NullLogger.Instance, minBackoff: TimeSpan.Zero, maxBackoff: TimeSpan.Zero);

        modem.IsReconnecting.Should().BeFalse("the initial link is up");

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var got = new List<string>();
        var pump = Task.Run(async () =>
        {
            await foreach (var f in modem.ReceiveAsync(cts.Token).ConfigureAwait(false))
            {
                got.Add(Encoding.ASCII.GetString(f.Ax25.Span));
                if (got.Count == 2)
                {
                    break;
                }
            }
        });

        // The pump delivers A, hits end-of-stream, and parks in the gated reconnect: observable.
        while (!modem.IsReconnecting && !pump.IsCompleted)
        {
            await Task.Delay(10);
        }
        modem.IsReconnecting.Should().BeTrue("the link is down and the wrapper is re-dialling");

        gate.SetResult();                                               // release the reconnect
        await pump;

        got.Should().Equal("A", "B");
        modem.IsReconnecting.Should().BeFalse("a fresh inner is live again");
    }

    [Fact]
    public async Task Send_while_the_link_is_down_is_dropped_not_thrown()
    {
        var down = new FakeModem(endAfterFrames: false) { ThrowOnSend = true };
        Func<CancellationToken, Task<IAx25Transport>> reconnect = _ => Task.FromResult<IAx25Transport>(new FakeModem(false));
        await using var modem = new ReconnectingKissModem(down, reconnect, "test", NullLogger.Instance);

        // Must not throw — the link being down is a drop-and-retransmit, not a fault.
        var act = async () => await modem.SendAsync(Encoding.ASCII.GetBytes("hi"));
        await act.Should().NotThrowAsync();
    }

    private sealed class FakeModem : IAx25Transport, ICsmaChannelParams
    {
        private readonly Ax25InboundFrame[] frames;
        private readonly bool endAfterFrames;

        public FakeModem(bool endAfterFrames, params Ax25InboundFrame[] frames)
        {
            this.endAfterFrames = endAfterFrames;
            this.frames = frames;
        }

        public bool ThrowOnSend { get; init; }
        public byte? TxDelay { get; private set; }
        public byte? Persistence { get; private set; }
        public byte? SlotTime { get; private set; }
        public byte? TxTail { get; private set; }

        public async IAsyncEnumerable<Ax25InboundFrame> ReceiveAsync([EnumeratorCancellation] CancellationToken ct = default)
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

        public Task SendAsync(ReadOnlyMemory<byte> ax25, CancellationToken cancellationToken = default)
            => ThrowOnSend ? throw new IOException("link down") : Task.CompletedTask;

        public Task SetTxDelayAsync(byte v, CancellationToken cancellationToken = default) { TxDelay = v; return Task.CompletedTask; }
        public Task SetPersistenceAsync(byte v, CancellationToken cancellationToken = default) { Persistence = v; return Task.CompletedTask; }
        public Task SetSlotTimeAsync(byte v, CancellationToken cancellationToken = default) { SlotTime = v; return Task.CompletedTask; }
        public Task SetTxTailAsync(byte v, CancellationToken cancellationToken = default) { TxTail = v; return Task.CompletedTask; }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
