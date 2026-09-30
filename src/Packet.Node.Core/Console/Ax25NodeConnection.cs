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
    private readonly TimeProvider timeProvider;

    // The pending drain-then-DISC started by DisposeAsync, so AbortAsync can cut it short.
    private Ax25GracefulClose? closing;

    /// <summary>Wrap <paramref name="session"/>, owned by <paramref name="listener"/>.
    /// <paramref name="timeProvider"/> is the clock the close uses to bound its wait on a busy
    /// peer; pass the one the listener runs its timers on. Null means the system clock.</summary>
    public Ax25NodeConnection(Ax25Listener listener, Ax25Session session, TimeProvider? timeProvider = null)
    {
        this.listener = listener ?? throw new ArgumentNullException(nameof(listener));
        this.session = session ?? throw new ArgumentNullException(nameof(session));
        this.timeProvider = timeProvider ?? TimeProvider.System;
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
    /// It returns whether it took the link; if it did not, the close disconnects it.
    /// </summary>
    internal Func<bool>? PeerRestartedAfterClose { get; set; }

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
            case DataLinkResetIndication reset:
                // The link was reset and the reset threw frames of ours away (packet.net#885):
                // the peer's SABM(E) on the up link, or this end re-establishing after an
                // unexpected UA, an FRMR, an N(R) error or a frame error, or a new dial on the
                // live link. §6.5: the reset "initializes both directions of data flow"; the
                // session counts what went (queued frames the figures discarded, and the send
                // window they abandoned) and says so once the arm is done. The stream's in-order
                // promise is broken, so the owner must know now, not after its own timers: end
                // the connection as the peer disconnecting would. A reset that lost nothing of
                // ours raises no such signal, and the stream carries on over the re-established
                // link. An interlink that NET/ROM is using is left alone: L4 recovers its own
                // frames, and ending the console's connection would DISC the neighbour.
                if (Volatile.Read(ref disposed) != 0 || completion.Task.IsCompleted || KeepOnLinkReset?.Invoke() == true)
                {
                    break;
                }

                EndReason = DescribeReset(reset);
                // A caller who started the link over (SABM(E)) gets the same hand-over a
                // restart during a graceful close gets (#850): a fresh console, an app's
                // accept, or a dial whose claim covers it. Decided here, on the reset's own
                // signal, so it does not race the owner's close. If nobody takes it, the
                // owner's close disconnects it.
                bool handedOver = session.CurrentTrigger is SabmReceived or SabmeReceived
                    && PeerRestartedAfterClose?.Invoke() == true;
                Volatile.Write(ref endedByReset, handedOver ? 2 : 1);
                Complete();
                break;
        }
    }

    private int endedByReset;

    /// <summary>
    /// Asked, when a reset that lost frames lands, whether this connection should ride it out
    /// rather than end: the supervisor answers yes for a session NET/ROM is using as an
    /// interlink (packet.net#885). Null means end.
    /// </summary>
    internal Func<bool>? KeepOnLinkReset { get; set; }

    private string DescribeReset(DataLinkResetIndication reset)
    {
        var t = reset.Transition;
        string cause;
        if (t.Contains("sabm", StringComparison.Ordinal))
        {
            cause = $"link reset by {session.Context.Remote}";
        }
        else if (t.Contains("dl_connect_request", StringComparison.Ordinal))
        {
            cause = "link re-established by a new dial on it";
        }
        else if (t.Contains("ua_received", StringComparison.Ordinal) || t.Contains("frmr", StringComparison.Ordinal))
        {
            cause = "link re-established after an unexpected UA or FRMR";
        }
        else if (t.Contains("control_field", StringComparison.Ordinal) || t.Contains("info_not", StringComparison.Ordinal)
            || t.Contains("length_error", StringComparison.Ordinal))
        {
            cause = "link re-established after a frame error";
        }
        else if (t.Contains("_received", StringComparison.Ordinal))
        {
            // An I or S frame received can only take a connected link back to establishment
            // through the N(R) error recovery (figc4.4 / figc4.5 Check N(R)).
            cause = "link re-established after an N(R) error";
        }
        else if (t.Contains("t1_expiry", StringComparison.Ordinal))
        {
            cause = "link gave up after N2 retries";
        }
        else
        {
            cause = $"link reset ({t})";
        }

        return $"{cause} with {reset.WindowLost + reset.QueuedDiscarded} frame(s) of ours undelivered ({reset.WindowLost} sent and unacknowledged, {reset.QueuedDiscarded} queued)";
    }

    /// <summary>
    /// Why the connection ended, when it was the link and not the owner or the peer's
    /// disconnect: a reset that discarded frames of ours (packet.net#885). Null otherwise.
    /// </summary>
    public string? EndReason { get; private set; }

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
        // Nothing goes out once the connection is closed or a reset has ended it (#885): the
        // link may be a fresh owner's by then, and an old owner's tail must not land in the
        // new console's output.
        if (Volatile.Read(ref disposed) != 0 || Volatile.Read(ref endedByReset) != 0)
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
            if (Volatile.Read(ref endedByReset) == 2)
            {
                // The peer's restart was handed to a fresh owner when the reset landed: the
                // link is theirs now, and this owner's close ends only its own connection.
            }
            else if (Volatile.Read(ref endedByReset) == 1)
            {
                // The link was reset under this connection and the stream ended for it; the
                // owner's close now is not a drain (nothing of its is queued any more) but the
                // end of a link that already lost data and nobody took. Disconnect it now,
                // rather than start a graceful close whose restart hand-over would race the
                // reset's own signals.
                Ax25GracefulClose.DisconnectNow(session);
            }
            else
            {
                Volatile.Write(ref closing, Ax25GracefulClose.Begin(session, timeProvider, PeerRestartedAfterClose));
            }
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
