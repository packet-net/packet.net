using System.Threading.Channels;
using Packet.Ax25;
using Packet.Ax25.Session;

namespace Packet.Node.Core.Console;

/// <summary>
/// Wraps an <see cref="Ax25Session"/> (plus its owning <see cref="Ax25Listener"/>)
/// as an <see cref="INodeConnection"/>: inbound <c>DL-DATA indication</c>s become
/// readable bytes, <see cref="WriteAsync"/> goes out through the listener's
/// segmentation-aware <see cref="Ax25Listener.SendData"/>, and a disconnect
/// (indication or confirm) completes the connection. This is the over-the-air
/// service path - the bridge between the AX.25 engine and the transport-agnostic
/// console.
/// </summary>
/// <remarks>
/// The adapter attaches to the session's <see cref="Ax25Session.DataLinkSignalEmitted"/>
/// stream via <see cref="Ax25Session.AttachConsumerWithReplay"/> (the same signal seam
/// axcall's <c>SessionRelay</c> uses). The replay matters for <b>outbound</b> sessions: the
/// session returned by <see cref="Ax25Listener.ConnectAsync"/> is already connected, so a peer
/// that sends immediately on accept - another pdn node emits its console banner the moment it
/// accepts, it does <em>not</em> wait - can put data on the wire in the window between connect
/// and this subscribe. A plain <c>+=</c> would drop it (the bug where an RHP <c>open</c> to a
/// node callsign connected but the banner never arrived); the replay delivers it instead.
/// </remarks>
public sealed class Ax25NodeConnection : INodeConnection
{
    private readonly Ax25Listener listener;
    private readonly Ax25Session session;
    private readonly Channel<ReadOnlyMemory<byte>> inbound =
        Channel.CreateUnbounded<ReadOnlyMemory<byte>>(new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });
    private readonly TaskCompletionSource completion =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private int disposed;

    // The pending drain-then-DISC started by DisposeAsync, so AbortAsync can cut it short.
    private Ax25GracefulClose? closing;

    public Ax25NodeConnection(Ax25Listener listener, Ax25Session session)
    {
        this.listener = listener ?? throw new ArgumentNullException(nameof(listener));
        this.session = session ?? throw new ArgumentNullException(nameof(session));
        // Replay-then-subscribe: catches a peer's pre-subscribe greeting (e.g. a node's connect
        // banner on an outbound link) that a plain `+= OnSignal` would miss. See class remarks.
        session.AttachConsumerWithReplay(OnSignal);
    }

    /// <inheritdoc/>
    public string PeerId => session.Context.Remote.ToString();

    /// <inheritdoc/>
    public NodeTransportKind TransportKind => NodeTransportKind.Ax25;

    /// <inheritdoc/>
    public Task Completion => completion.Task;

    /// <summary>
    /// What to do if the peer starts the link over (a SABM or SABME) while a close is waiting
    /// for its data to be acknowledged: the owner of an inbound link sets this to hand it on as a
    /// fresh connect, which the listener does not do for a SABM in Connected. Unset, the close
    /// carries on and disconnects the reset link. See <see cref="Ax25GracefulClose.Begin"/>.
    /// </summary>
    internal Action? PeerRestartedAfterClose { get; set; }

    /// <inheritdoc/>
    /// <remarks>Set by <see cref="Ax25OutboundConnector"/>, which watches the dial; an
    /// inbound connection leaves it false.</remarks>
    public bool Crossed { get; init; }

    /// <summary>The wrapped session - exposed so the AX.25 adapter source
    /// (listener wiring) can correlate, and for tests.</summary>
    public Ax25Session Session => session;

    private void OnSignal(object? sender, DataLinkSignal sig)
    {
        switch (sig)
        {
            case DataLinkDataIndication di:
                // The console carries node-text data (PID 0xF0 / no-Layer-3). A
                // session may ALSO carry NET/ROM (PID 0xCF interlink datagrams),
                // which the NetRomService taps separately - those must not leak into
                // the console as garbage text, so filter them out here.
                if (di.Pid != Ax25Frame.PidNetRom)
                {
                    inbound.Writer.TryWrite(di.Info);
                }
                break;
            case DataLinkDisconnectIndication:
            case DataLinkDisconnectConfirm:
                Complete();
                break;
        }
    }

    /// <inheritdoc/>
    public async ValueTask<ReadOnlyMemory<byte>> ReadAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            if (await inbound.Reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false) &&
                inbound.Reader.TryRead(out var chunk))
            {
                return chunk;
            }
        }
        catch (ChannelClosedException)
        {
            // disconnected - fall through to EOF
        }
        return ReadOnlyMemory<byte>.Empty;
    }

    /// <inheritdoc/>
    public ValueTask WriteAsync(ReadOnlyMemory<byte> bytes, CancellationToken cancellationToken = default)
    {
        if (Volatile.Read(ref disposed) != 0)
        {
            return ValueTask.CompletedTask;
        }
        // Segmentation-aware send through the listener. SendData throws if the
        // session is no longer owned by the listener (evicted / torn down) - treat
        // that as a closed connection rather than letting it escape the console.
        try
        {
            // A connected-mode link is a byte stream: the data link frames it into
            // N1-sized I-frames. The listener's SendData treats its input as one SDU
            // and (correctly) throws when it exceeds N1 without the v2.2 segmenter
            // negotiated - right for an oversized atomic datagram, wrong for a stream
            // write. v2.0 peers (e.g. LinBPQ: plain SABM, no XID) never negotiate the
            // segmenter, so an over-N1 application write (a forwarded message body, a
            // long console reply) must be chunked into ordinary <=N1 I-frames here, or
            // it never reaches the wire and the link is torn down. A negotiated v2.2
            // link still passes the whole buffer through so it produces PID-0x08
            // segments. (Without this the InvalidOperationException SendData throws on
            // the over-N1 case escapes as RHP errCode 17 "Not connected".)
            int n1 = session.Context.N1;
            if (bytes.Length > n1 && !session.Context.SegmenterReassemblerEnabled)
            {
                for (int offset = 0; offset < bytes.Length; offset += n1)
                {
                    listener.SendData(session, bytes.Slice(offset, Math.Min(n1, bytes.Length - offset)));
                }
            }
            else
            {
                listener.SendData(session, bytes);
            }
        }
        catch (ArgumentException)
        {
            Complete();
        }
        catch (ObjectDisposedException)
        {
            Complete();
        }
        return ValueTask.CompletedTask;
    }

    private void Complete()
    {
        completion.TrySetResult();
        inbound.Writer.TryComplete();
    }

    /// <summary>
    /// Close the connection the way an app or the console expects: stop taking writes, end the
    /// local side at once (<see cref="Completion"/> completes, reads return EOF), and leave the
    /// link up until everything already written has been acknowledged by the peer, then send
    /// DISC (packet.net#850, see <see cref="Ax25GracefulClose"/>). Returns without waiting for
    /// the peer, so a caller never blocks on a slow or dead link; the link's own T1/N2 retry
    /// limit ends it if the peer has gone.
    /// </summary>
    public ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0)
        {
            return ValueTask.CompletedTask;
        }
        session.DataLinkSignalEmitted -= OnSignal;

        try
        {
            Volatile.Write(ref closing, Ax25GracefulClose.Begin(session, PeerRestartedAfterClose));
        }
        catch
        {
            // Best-effort teardown; never throw from dispose.
        }

        Complete();
        return ValueTask.CompletedTask;
    }

    /// <summary>
    /// Disconnect at once, discarding anything still queued or unacknowledged: a sysop kill and
    /// node shutdown. Also cuts short a graceful close already under way on this connection.
    /// </summary>
    public ValueTask AbortAsync()
    {
        try
        {
            if (Interlocked.Exchange(ref disposed, 1) == 0)
            {
                session.DataLinkSignalEmitted -= OnSignal;
                Ax25GracefulClose.DisconnectNow(session);
            }
            else
            {
                Volatile.Read(ref closing)?.Abort();
            }
        }
        catch
        {
            // Best-effort teardown; never throw from a close.
        }

        Complete();
        return ValueTask.CompletedTask;
    }
}
