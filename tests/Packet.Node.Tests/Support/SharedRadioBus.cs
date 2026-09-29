using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Packet.Ax25.Transport;
using Packet.Kiss;

namespace Packet.Node.Tests.Support;

/// <summary>
/// A broadcast in-memory medium: every frame an endpoint transmits is heard by
/// every <em>other</em> endpoint on the bus (address filtering is the AX.25
/// layer's job, exactly as on a real shared RF channel). Lets a test stand up a
/// node + a remote + a third station all on one "channel", so the node's
/// connect-OUT to the third station works while the remote ignores it.
/// </summary>
public sealed class SharedRadioBus
{
    private readonly List<Endpoint> endpoints = new();
    private readonly object gate = new();

    /// <summary>Attach a new endpoint (a transport) to the bus.</summary>
    /// <param name="loseOutbound">Optional: return true for a frame this endpoint transmits to
    /// lose it on the air (nobody hears it). For losing one particular frame, such as a UA.</param>
    public IAx25Transport Attach(Func<ReadOnlyMemory<byte>, bool>? loseOutbound = null)
    {
        var ep = new Endpoint(this, loseOutbound);
        lock (gate)
        {
            endpoints.Add(ep);
        }

        return ep;
    }

    private void Broadcast(Endpoint from, KissFrame frame)
    {
        Endpoint[] others;
        lock (gate)
        {
            others = endpoints.Where(e => !ReferenceEquals(e, from)).ToArray();
        }

        foreach (var e in others)
        {
            e.Deliver(frame);
        }
    }

    private sealed class Endpoint : IAx25Transport, ICsmaChannelParams
    {
        private readonly SharedRadioBus bus;
        private readonly Channel<KissFrame> rx =
            Channel.CreateUnbounded<KissFrame>(new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });

        private readonly Func<ReadOnlyMemory<byte>, bool>? loseOutbound;

        public Endpoint(SharedRadioBus bus, Func<ReadOnlyMemory<byte>, bool>? loseOutbound)
        {
            this.bus = bus;
            this.loseOutbound = loseOutbound;
        }

        internal void Deliver(KissFrame frame) => rx.Writer.TryWrite(frame);

        public Task SendAsync(ReadOnlyMemory<byte> ax25, CancellationToken cancellationToken = default)
        {
            if (loseOutbound?.Invoke(ax25) == true)
            {
                return Task.CompletedTask;
            }

            bus.Broadcast(this, new KissFrame((byte)0, KissCommand.Data, ax25.ToArray()));
            return Task.CompletedTask;
        }

        public async IAsyncEnumerable<Ax25InboundFrame> ReceiveAsync(
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            while (await rx.Reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
            {
                while (rx.Reader.TryRead(out var f))
                {
                    if (f.Command != KissCommand.Data)
                    {
                        continue;
                    }

                    yield return new Ax25InboundFrame(f.Payload, f.Port, DateTimeOffset.UtcNow);
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
