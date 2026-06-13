using System.Threading.Channels;

namespace Packet.Node.Core.Console;

/// <summary>
/// An in-memory duplex <see cref="INodeConnection"/> pair — two ends wired
/// back-to-back so bytes written to one surface on the other's
/// <see cref="ReadAsync"/>, with no transport underneath. Used by the console's
/// local <em>crossconnect</em> (<c>C &lt;app-ssid&gt;</c> to a callsign the node is
/// registered for): one end is handed to the app's accept handler (the RHPv2
/// server's accept push), the other is relayed against the inbound user. The two
/// share a single completion signal, so disposing either end (the user drops, or
/// the app closes its handle) unblocks the pumps on both sides.
/// </summary>
internal sealed class LoopbackNodeConnection : INodeConnection
{
    private readonly ChannelReader<ReadOnlyMemory<byte>> inbox;
    private readonly ChannelWriter<ReadOnlyMemory<byte>> outbox;
    private readonly LoopbackState state;
    private int disposed;

    private LoopbackNodeConnection(
        string peerId,
        NodeTransportKind kind,
        ChannelReader<ReadOnlyMemory<byte>> inbox,
        ChannelWriter<ReadOnlyMemory<byte>> outbox,
        LoopbackState state)
    {
        PeerId = peerId;
        TransportKind = kind;
        this.inbox = inbox;
        this.outbox = outbox;
        this.state = state;
    }

    public string PeerId { get; }

    public NodeTransportKind TransportKind { get; }

    public Task Completion => state.Completion;

    /// <summary>
    /// Build a back-to-back pair. The <paramref name="appPeerId"/> is what the app sees as the
    /// caller (the real inbound user's callsign), so an RHP-attached app's <c>ACCEPT.remote</c>
    /// names the human who dialled; <paramref name="userPeerId"/> labels the user-facing end (the
    /// app callsign) for console messages/logging.
    /// </summary>
    public static (INodeConnection appEnd, INodeConnection userEnd) CreatePair(
        string appPeerId, NodeTransportKind appKind, string userPeerId, NodeTransportKind userKind)
    {
        var options = new UnboundedChannelOptions { SingleReader = true, SingleWriter = false };
        var userToApp = Channel.CreateUnbounded<ReadOnlyMemory<byte>>(options);
        var appToUser = Channel.CreateUnbounded<ReadOnlyMemory<byte>>(options);
        var state = new LoopbackState();

        // appEnd reads what the user wrote, writes toward the user; userEnd is the mirror.
        var appEnd = new LoopbackNodeConnection(appPeerId, appKind, userToApp.Reader, appToUser.Writer, state);
        var userEnd = new LoopbackNodeConnection(userPeerId, userKind, appToUser.Reader, userToApp.Writer, state);
        return (appEnd, userEnd);
    }

    public async ValueTask<ReadOnlyMemory<byte>> ReadAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            if (await inbox.WaitToReadAsync(cancellationToken).ConfigureAwait(false) && inbox.TryRead(out var chunk))
            {
                return chunk;
            }
            return ReadOnlyMemory<byte>.Empty;   // peer completed its writer → EOF
        }
        catch (ChannelClosedException)
        {
            return ReadOnlyMemory<byte>.Empty;
        }
        catch (OperationCanceledException)
        {
            return ReadOnlyMemory<byte>.Empty;   // never throw on a normal close (INodeConnection contract)
        }
    }

    public ValueTask WriteAsync(ReadOnlyMemory<byte> bytes, CancellationToken cancellationToken = default)
    {
        if (bytes.IsEmpty)
        {
            return ValueTask.CompletedTask;
        }
        // The caller may reuse its buffer once WriteAsync returns; copy before queueing.
        var copy = bytes.ToArray();
        outbox.TryWrite(copy);   // false once the peer end has gone — drop, the relay is winding down
        return ValueTask.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) == 0)
        {
            state.Complete();        // unblock BOTH ends' Completion
            outbox.TryComplete();    // signal EOF to the peer's reader
        }
        return ValueTask.CompletedTask;
    }

    // One completion shared by both ends: either side disposing tears the bridge down.
    private sealed class LoopbackState
    {
        private readonly TaskCompletionSource tcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task Completion => tcs.Task;
        public void Complete() => tcs.TrySetResult();
    }
}
