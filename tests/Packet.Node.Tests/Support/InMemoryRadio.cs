using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Packet.Ax25;
using Packet.Kiss;

namespace Packet.Node.Tests.Support;

/// <summary>
/// A pair of in-memory <see cref="IKissModem"/>s wired back-to-back: a frame
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

    /// <summary>One side of the in-memory medium — a full <see cref="IKissModem"/>.</summary>
    public sealed class Endpoint : IKissModem
    {
        private readonly ChannelWriter<KissFrame> txTo;
        private readonly ChannelReader<KissFrame> rxFrom;

        /// <summary>Set of KISS params the listener / supervisor pushed to this
        /// modem, so a hot-reload test can assert they were applied live.</summary>
        public AppliedKissParams Applied { get; } = new();

        internal Endpoint(ChannelWriter<KissFrame> txTo, ChannelReader<KissFrame> rxFrom)
        {
            this.txTo = txTo;
            this.rxFrom = rxFrom;
        }

        public Task SendFrameAsync(ReadOnlyMemory<byte> ax25Bytes, CancellationToken cancellationToken = default)
        {
            txTo.TryWrite(new KissFrame((byte)0, KissCommand.Data, ax25Bytes.ToArray()));
            return Task.CompletedTask;
        }

        public async IAsyncEnumerable<KissFrame> ReadFramesAsync(
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            while (await rxFrom.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
            {
                while (rxFrom.TryRead(out var f))
                {
                    yield return f;
                }
            }
        }

        public Task<AckModeReceipt> SendFrameWithAckAsync(
            ReadOnlyMemory<byte> ax25Bytes, TimeSpan? timeout = null, ushort? sequenceTag = null,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task SetTxDelayAsync(byte tenMsUnits, CancellationToken cancellationToken = default) { Applied.TxDelay = tenMsUnits; return Task.CompletedTask; }
        public Task SetPersistenceAsync(byte value, CancellationToken cancellationToken = default) { Applied.Persistence = value; return Task.CompletedTask; }
        public Task SetSlotTimeAsync(byte tenMsUnits, CancellationToken cancellationToken = default) { Applied.SlotTime = tenMsUnits; return Task.CompletedTask; }
        public Task SetTxTailAsync(byte tenMsUnits, CancellationToken cancellationToken = default) { Applied.TxTail = tenMsUnits; Applied.TxTailSendCount++; return Task.CompletedTask; }
    }

    /// <summary>Records the last KISS params applied to an in-memory modem.</summary>
    public sealed class AppliedKissParams
    {
        public byte? TxDelay { get; set; }
        public byte? Persistence { get; set; }
        public byte? SlotTime { get; set; }
        public byte? TxTail { get; set; }

        /// <summary>How many times TXTAIL was sent to the modem — proves the
        /// always-send-on-apply cadence (#465), not just the final value.</summary>
        public int TxTailSendCount { get; set; }
    }
}
