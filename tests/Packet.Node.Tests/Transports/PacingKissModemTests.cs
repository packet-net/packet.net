using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Packet.Kiss;
using Packet.Node.Core.Transports;

namespace Packet.Node.Tests.Transports;

/// <summary>
/// <see cref="PacingKissModem"/> — the ACKMODE host-side pacing decorator. A port
/// with <c>kiss.ackMode</c> on must serialise its outbound frames onto the
/// half-duplex channel: each frame is sent in ACKMODE and the next is held until the
/// prior frame's TX-completion echo arrives (or a short timeout). The pacing must
/// never block the caller (the listener sink is fire-and-forget), one frame must
/// never wedge the pump, and the pass-through surface (reads + KISS-param setters)
/// must reach the inner modem unchanged.
/// </summary>
public sealed class PacingKissModemTests
{
    [Fact]
    public async Task Sends_are_serialised_in_order_each_held_until_the_prior_ack()
    {
        await using var inner = new GatedModem();
        await using var modem = new PacingKissModem(inner, TimeSpan.FromSeconds(30), NullLogger.Instance);

        // Enqueue three frames. SendFrameAsync is fire-and-forget — it returns at once.
        await modem.SendFrameAsync(Encoding.ASCII.GetBytes("A"));
        await modem.SendFrameAsync(Encoding.ASCII.GetBytes("B"));
        await modem.SendFrameAsync(Encoding.ASCII.GetBytes("C"));

        // Frame A enters inner.SendFrameWithAckAsync and BLOCKS there (not yet acked).
        (await inner.WaitForInFlightAsync("A")).Should().BeTrue();
        // B and C must NOT have started — the pump is awaiting A's echo.
        inner.Started.Should().Equal("A");

        // Ack A → the pump releases B.
        inner.SignalAck();
        (await inner.WaitForInFlightAsync("B")).Should().BeTrue();
        inner.Started.Should().Equal("A", "B");
        inner.Completed.Should().Equal("A");

        // Ack B → C.
        inner.SignalAck();
        (await inner.WaitForInFlightAsync("C")).Should().BeTrue();
        inner.Started.Should().Equal("A", "B", "C");

        // Ack C → all three complete, strictly in order.
        inner.SignalAck();
        (await inner.WaitForCompletedCountAsync(3)).Should().BeTrue();
        inner.Completed.Should().Equal("A", "B", "C");
    }

    [Fact]
    public async Task A_timeout_on_one_frame_does_not_wedge_the_pump()
    {
        await using var inner = new GatedModem();
        // A tiny pacing window: the first frame's ack is never signalled, so it times out;
        // the pump must still go on to send the next frame.
        await using var modem = new PacingKissModem(inner, TimeSpan.FromMilliseconds(50), NullLogger.Instance);

        // The first frame throws TimeoutException from inner (simulating the echo never
        // arriving within the pacing window); the second completes normally.
        inner.ThrowTimeoutOnce = true;

        await modem.SendFrameAsync(Encoding.ASCII.GetBytes("X"));   // will time out
        await modem.SendFrameAsync(Encoding.ASCII.GetBytes("Y"));   // must still be sent

        // Y must reach the inner modem despite X timing out — the pump kept going.
        (await inner.WaitForInFlightAsync("Y")).Should().BeTrue();
        inner.SignalAck();
        (await inner.WaitForCompletedCountAsync(1)).Should().BeTrue();
        inner.Completed.Should().Equal("Y");
        inner.Started.Should().Equal("X", "Y");
    }

    [Fact]
    public async Task Read_and_param_setters_pass_through_to_inner_unchanged()
    {
        await using var inner = new GatedModem(Frame("hello"));
        await using var modem = new PacingKissModem(inner, TimeSpan.FromSeconds(5), NullLogger.Instance);

        // ReadFramesAsync must surface the inner modem's frames verbatim.
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var got = new List<string>();
        await foreach (var f in modem.ReadFramesAsync(cts.Token).ConfigureAwait(false))
        {
            got.Add(Encoding.ASCII.GetString(f.Payload));
            break;
        }
        got.Should().Equal("hello");

        // The four KISS setters must reach the inner modem with their values intact.
        await modem.SetTxDelayAsync(30);
        await modem.SetPersistenceAsync(63);
        await modem.SetSlotTimeAsync(10);
        await modem.SetTxTailAsync(5);

        inner.TxDelay.Should().Be((byte)30);
        inner.Persistence.Should().Be((byte)63);
        inner.SlotTime.Should().Be((byte)10);
        inner.TxTail.Should().Be((byte)5);
    }

    [Fact]
    public async Task Disposing_the_decorator_disposes_the_inner_modem()
    {
        var inner = new GatedModem();
        var modem = new PacingKissModem(inner, TimeSpan.FromSeconds(5), NullLogger.Instance);
        await modem.DisposeAsync();

        inner.Disposed.Should().BeTrue();
    }

    private static KissFrame Frame(string s) => new(0, KissCommand.Data, Encoding.ASCII.GetBytes(s));

    /// <summary>
    /// An inner modem whose <see cref="SendFrameWithAckAsync"/> blocks until the test
    /// explicitly signals an ack — so a test can observe exactly when each frame enters
    /// (and leaves) the send, proving the pump serialises. Records the order frames start
    /// and complete.
    /// </summary>
    private sealed class GatedModem : IKissModem, IAsyncDisposable
    {
        private readonly KissFrame[] frames;
        private readonly SemaphoreSlim ackGate = new(0);
        private readonly object gate = new();
        private readonly List<string> started = new();
        private readonly List<string> completed = new();

        public GatedModem(params KissFrame[] frames) => this.frames = frames;

        public bool ThrowTimeoutOnce { get; set; }
        public bool Disposed { get; private set; }
        public byte? TxDelay { get; private set; }
        public byte? Persistence { get; private set; }
        public byte? SlotTime { get; private set; }
        public byte? TxTail { get; private set; }

        public IReadOnlyList<string> Started { get { lock (gate) return started.ToArray(); } }
        public IReadOnlyList<string> Completed { get { lock (gate) return completed.ToArray(); } }

        /// <summary>Release one blocked send (one frame's TX-completion echo).</summary>
        public void SignalAck() => ackGate.Release();

        /// <summary>Spin (bounded) until a frame with the given payload has entered the send.</summary>
        public async Task<bool> WaitForInFlightAsync(string payload)
        {
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
            while (DateTime.UtcNow < deadline)
            {
                lock (gate) { if (started.Contains(payload)) return true; }
                await Task.Delay(5).ConfigureAwait(false);
            }
            return false;
        }

        public async Task<bool> WaitForCompletedCountAsync(int count)
        {
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
            while (DateTime.UtcNow < deadline)
            {
                lock (gate) { if (completed.Count >= count) return true; }
                await Task.Delay(5).ConfigureAwait(false);
            }
            return false;
        }

        public async Task<AckModeReceipt> SendFrameWithAckAsync(
            ReadOnlyMemory<byte> ax25Bytes, TimeSpan? timeout = null, ushort? sequenceTag = null, CancellationToken cancellationToken = default)
        {
            var payload = Encoding.ASCII.GetString(ax25Bytes.Span);
            lock (gate) started.Add(payload);

            if (ThrowTimeoutOnce)
            {
                ThrowTimeoutOnce = false;
                // Simulate the echo never arriving within the pacing window.
                throw new TimeoutException("ackmode echo timed out");
            }

            // Block until the test signals this frame's ack — this is what lets the test
            // prove the pump holds the next frame.
            await ackGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            lock (gate) completed.Add(payload);
            var now = DateTimeOffset.UtcNow;
            return new AckModeReceipt(sequenceTag ?? 0, now, now);
        }

        public async IAsyncEnumerable<KissFrame> ReadFramesAsync([EnumeratorCancellation] CancellationToken ct = default)
        {
            foreach (var f in frames)
            {
                ct.ThrowIfCancellationRequested();
                yield return f;
            }
            try { await Task.Delay(Timeout.Infinite, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { }
        }

        // The fire-and-forget send is unused by the decorator's pacing path (it routes
        // through SendFrameWithAckAsync), but must exist on the interface.
        public Task SendFrameAsync(ReadOnlyMemory<byte> ax25Bytes, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task SetTxDelayAsync(byte v, CancellationToken cancellationToken = default) { TxDelay = v; return Task.CompletedTask; }
        public Task SetPersistenceAsync(byte v, CancellationToken cancellationToken = default) { Persistence = v; return Task.CompletedTask; }
        public Task SetSlotTimeAsync(byte v, CancellationToken cancellationToken = default) { SlotTime = v; return Task.CompletedTask; }
        public Task SetTxTailAsync(byte v, CancellationToken cancellationToken = default) { TxTail = v; return Task.CompletedTask; }

        public ValueTask DisposeAsync()
        {
            Disposed = true;
            ackGate.Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
