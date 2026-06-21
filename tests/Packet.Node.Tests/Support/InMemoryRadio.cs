using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Packet.Ax25;
using Packet.Ax25.Transport;
using Packet.Kiss;

namespace Packet.Node.Tests.Support;

/// <summary>
/// A pair of in-memory <see cref="IAx25Transport"/>s wired back-to-back: a frame
/// written on one endpoint surfaces on the other's inbound stream. The
/// software-RF channel for the node integration tests — lets two real
/// <see cref="Packet.Ax25.Session.Ax25Listener"/>s connect in-process with no
/// sockets, mirroring the conformance harness's in-memory transport.
/// </summary>
public static class InMemoryRadio
{
    /// <summary>Create two endpoints sharing one medium. Frames written on
    /// <c>A</c> are heard on <c>B</c> and vice-versa.</summary>
    public static (Endpoint A, Endpoint B) CreatePair()
    {
        var aToB = Channel.CreateUnbounded<KissFrame>(new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });
        var bToA = Channel.CreateUnbounded<KissFrame>(new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });
        var a = new Endpoint(txTo: aToB.Writer, rxFrom: bToA.Reader);
        var b = new Endpoint(txTo: bToA.Writer, rxFrom: aToB.Reader);
        return (a, b);
    }

    /// <summary>One side of the in-memory medium — a full <see cref="IAx25Transport"/>
    /// with the <see cref="ICsmaChannelParams"/> capability (which records the params
    /// pushed to it so a hot-reload test can assert they were applied live).</summary>
    public sealed class Endpoint : IAx25Transport, ICsmaChannelParams
    {
        private readonly ChannelWriter<KissFrame> txTo;
        private readonly ChannelReader<KissFrame> rxFrom;

        /// <summary>Set of KISS params the listener / supervisor pushed to this
        /// transport, so a hot-reload test can assert they were applied live.</summary>
        public AppliedKissParams Applied { get; } = new();

        internal Endpoint(ChannelWriter<KissFrame> txTo, ChannelReader<KissFrame> rxFrom)
        {
            this.txTo = txTo;
            this.rxFrom = rxFrom;
        }

        public Task SendAsync(ReadOnlyMemory<byte> ax25, CancellationToken cancellationToken = default)
        {
            txTo.TryWrite(new KissFrame((byte)0, KissCommand.Data, ax25.ToArray()));
            return Task.CompletedTask;
        }

        public async IAsyncEnumerable<Ax25InboundFrame> ReceiveAsync(
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            while (await rxFrom.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
            {
                while (rxFrom.TryRead(out var f))
                {
                    // The medium only ever carries KISS Data frames, but filter for parity
                    // with the real transports' RX seam.
                    if (f.Command != KissCommand.Data) continue;
                    yield return new Ax25InboundFrame(f.Payload, f.Port, DateTimeOffset.UtcNow);
                }
            }
        }

        public Task SetTxDelayAsync(byte tenMsUnits, CancellationToken cancellationToken = default) { Applied.TxDelay = tenMsUnits; return Task.CompletedTask; }
        public Task SetPersistenceAsync(byte value, CancellationToken cancellationToken = default) { Applied.Persistence = value; return Task.CompletedTask; }
        public Task SetSlotTimeAsync(byte tenMsUnits, CancellationToken cancellationToken = default) { Applied.SlotTime = tenMsUnits; return Task.CompletedTask; }
        public Task SetTxTailAsync(byte tenMsUnits, CancellationToken cancellationToken = default) { Applied.TxTail = tenMsUnits; Applied.TxTailSendCount++; return Task.CompletedTask; }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    /// <summary>Records the last KISS params applied to an in-memory transport.</summary>
    public sealed class AppliedKissParams
    {
        public byte? TxDelay { get; set; }
        public byte? Persistence { get; set; }
        public byte? SlotTime { get; set; }
        public byte? TxTail { get; set; }

        /// <summary>How many times TXTAIL was sent to the transport — proves the
        /// always-send-on-apply cadence (#465), not just the final value.</summary>
        public int TxTailSendCount { get; set; }
    }
}
