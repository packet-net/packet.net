using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using Packet.Ax25.Transport;

namespace Packet.Node.Core.Transports;

/// <summary>
/// Paces a port's outbound transmissions to a real half-duplex channel. Wraps an
/// inner TX-completion-capable transport (in practice a <see cref="ReconnectingKissModem"/>
/// over a kiss-tcp link) and turns the fire-and-forget blast the <c>Ax25Listener</c> sink
/// emits into a <b>serialised, one-at-a-time</b> stream: each frame is sent awaiting
/// TX-completion (<see cref="ITxCompletionTransport.SendAwaitingCompletionAsync"/>) and the
/// next frame is not released until the prior frame's TX-completion arrives (or
/// a short timeout elapses). On the software-RF lab channel (net-sim) — and on a
/// real TNC that honours the G8BPQ ACKMODE extension — that completion means "the frame
/// has cleared the air", so awaiting it before pulling the next frame keeps the
/// node from piling multiple frames onto the shared medium at once.
/// </summary>
/// <remarks>
/// <para>
/// <b>Opt-in, default-off.</b> A port wraps in this decorator only when its
/// <c>kiss.ackMode</c> flag is set; an unwrapped port keeps the exact
/// fire-and-forget behaviour it has always had. See <c>KissParams.AckMode</c> and
/// the <see cref="Hosting.PortSupervisor"/> bring-up that wires it.
/// </para>
/// <para>
/// <b>The send contract is preserved.</b> <see cref="SendFrameAsync"/> snapshots the
/// caller's buffer (the listener sink reuses its frame buffer) and returns
/// immediately — it never blocks the caller, exactly like the plain send the SDL
/// frame sinks rely on. The pacing happens entirely on a single background pump
/// that drains the queue and awaits each ACKMODE send in turn.
/// </para>
/// <para>
/// <b>One frame can never wedge the pump.</b> A timeout (or any other send fault)
/// on one frame is logged and the pump moves on to the next — a stuck or
/// unacknowledged frame degrades to fire-and-forget for that one frame rather than
/// stalling the whole port. AX.25 T1 still retransmits anything genuinely lost.
/// </para>
/// </remarks>
internal sealed partial class PacingKissModem : ITxCompletionTransport, ICsmaChannelParams, IAsyncDisposable
{
    private readonly ITxCompletionTransport inner;
    private readonly TimeSpan pacingTimeout;
    private readonly ILogger logger;
    private readonly Channel<TxJob> queue;

    /// <summary>One queued transmission. <see cref="Receipt"/> is null for the
    /// fire-and-forget path; for an explicit <see cref="SendAwaitingCompletionAsync"/>
    /// the pump resolves it with the frame's TX-completion (or faults it)
    /// once this frame's turn comes — so explicit completion-sends share the same
    /// serialised order as everything else on the port.</summary>
    private readonly record struct TxJob(
        byte[] Frame,
        TimeSpan? Timeout,
        TaskCompletionSource<TxCompletion>? Receipt);
    private readonly CancellationTokenSource lifecycle = new();
    private readonly Task pump;
    private int disposed;

    /// <summary>The default per-frame pacing timeout: long enough for a maximum-size
    /// AX.25 frame to clear a 1200-baud channel (a 256-byte info field plus header is
    /// ~2 s on the air) with comfortable margin, short enough that a missed echo does
    /// not stall the port for long. Tunable via the constructor.</summary>
    public static readonly TimeSpan DefaultPacingTimeout = TimeSpan.FromSeconds(5);

    /// <param name="inner">The modem this decorator paces and owns — disposed when this
    /// decorator is disposed (the standard inner-ownership chain, matching
    /// <see cref="ReconnectingKissModem"/>).</param>
    /// <param name="pacingTimeout">How long to wait for one frame's TX-completion echo
    /// before giving up on that frame and releasing the next. <see cref="DefaultPacingTimeout"/>
    /// if not specified.</param>
    /// <param name="logger">Optional logger for per-frame pace faults.</param>
    public PacingKissModem(ITxCompletionTransport inner, TimeSpan? pacingTimeout = null, ILogger? logger = null)
    {
        this.inner = inner ?? throw new ArgumentNullException(nameof(inner));
        this.pacingTimeout = pacingTimeout ?? DefaultPacingTimeout;
        this.logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance;

        // Unbounded: the listener's send sink is fire-and-forget and must never block,
        // and the node's own offered load is naturally bounded by the AX.25 send windows
        // across its sessions. Single reader (the pump), many writers (the sink).
        queue = Channel.CreateUnbounded<TxJob>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false,
        });

        pump = Task.Run(() => PumpAsync(lifecycle.Token));
    }

    /// <inheritdoc/>
    /// <remarks>
    /// Snapshots the frame and enqueues it for the pump, then returns immediately. The
    /// caller's <paramref name="ax25Bytes"/> buffer may be reused the instant this
    /// returns (the <c>Ax25Listener</c> sink reuses it), so the bytes are copied here.
    /// </remarks>
    public Task SendAsync(ReadOnlyMemory<byte> ax25, CancellationToken cancellationToken = default)
    {
        // Copy out of the caller's (reusable) buffer before returning — the pump reads
        // it later, off this call's stack.
        var frame = ax25.ToArray();

        // TryWrite always succeeds on an unbounded channel until it is completed; once
        // disposal completes the writer, a late send is silently dropped (the port is
        // going away — AX.25 retransmit covers a genuinely-needed frame).
        queue.Writer.TryWrite(new TxJob(frame, Timeout: null, Receipt: null));
        return Task.CompletedTask;
    }

    // The single background pump: drain the queue and send each frame awaiting its
    // TX-completion before pulling the next. That await IS the pacing.
    private async Task PumpAsync(CancellationToken ct)
    {
        try
        {
            await foreach (var job in queue.Reader.ReadAllAsync(ct).ConfigureAwait(false))
            {
                try
                {
                    var receipt = await SendWithReconnectRetryAsync(job, ct).ConfigureAwait(false);
                    job.Receipt?.TrySetResult(receipt);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    // Shutting down — stop pumping.
                    job.Receipt?.TrySetCanceled(ct);
                    break;
                }
                catch (TimeoutException tex)
                {
                    // The echo never came within the pacing window. Don't wedge the pump:
                    // the frame is already on the wire (ACKMODE wraps a real send), and
                    // AX.25 T1 retransmits anything genuinely lost. Release the next frame.
                    // An explicit receipt-awaiting caller gets the timeout to handle itself.
                    LogPaceTimeout((int)(job.Timeout ?? pacingTimeout).TotalMilliseconds);
                    job.Receipt?.TrySetException(tex);
                }
                catch (Exception ex)
                {
                    // Any other send fault (link bounce mid-send, modem disposed): log and
                    // keep pumping so one bad frame can never stall the whole port.
                    LogPaceFaulted(ex);
                    job.Receipt?.TrySetException(ex);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Disposal cancelled the pump while it waited on the reader — normal shutdown.
        }
        finally
        {
            // Disposal can complete the writer with jobs still queued — fail their
            // waiters rather than leaving them hanging forever.
            while (queue.Reader.TryRead(out var leftover))
            {
                leftover.Receipt?.TrySetCanceled(CancellationToken.None);
            }
        }
    }

    // Sends a frame awaiting TX-completion, retrying once if the inner transport was
    // swapped mid-flight by a reconnect (ObjectDisposedException from the dead client).
    // Without this, a KISS-TCP bounce drops the in-flight frame and relies entirely on
    // AX.25 T1 retransmit — which cannot recover during sustained flapping (#664).
    private async Task<TxCompletion> SendWithReconnectRetryAsync(TxJob job, CancellationToken ct)
    {
        var timeout = job.Timeout ?? pacingTimeout;
        try
        {
            return await inner.SendAwaitingCompletionAsync(job.Frame, timeout, ct).ConfigureAwait(false);
        }
        catch (ObjectDisposedException) when (inner is ITransportLinkState { IsReconnecting: true })
        {
            // The inner KissTcpClient was disposed by ReconnectingKissModem mid-send.
            // Wait for the fresh link, then re-send on it rather than dropping the frame.
            LogPaceLinkSwapRetry();
            await WaitForReconnectAsync(ct).ConfigureAwait(false);
            return await inner.SendAwaitingCompletionAsync(job.Frame, timeout, ct).ConfigureAwait(false);
        }
    }

    private async Task WaitForReconnectAsync(CancellationToken ct)
    {
        // Poll the link-state; the reconnect backoff is at most ~30 s per attempt.
        // Bound the wait so a permanently-down link still degrades to the old
        // drop-and-let-T1-retransmit behaviour rather than wedging the pump forever.
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(35));
        try
        {
            while (inner is ITransportLinkState { IsReconnecting: true })
            {
                await Task.Delay(50, timeout.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // Bounded wait elapsed — proceed with the retry attempt regardless.
        }
    }

    /// <inheritdoc/>
    /// <remarks>
    /// Routed THROUGH the pacing queue (not past it): the frame transmits in
    /// strict arrival order with every fire-and-forget send on this port, and
    /// the returned task resolves with this frame's TX-completion receipt when
    /// its turn completes. This is what the TX-complete→T1 seam rides on — the
    /// listener sends a T1-arming frame here and re-arms T1 when the receipt
    /// lands, knowing the frame has actually cleared the air. The timeout
    /// (default: the pacing timeout) covers the inner ACKMODE send, not the
    /// queue wait; queue wait is bounded by the pacing of the frames ahead.
    /// </remarks>
    public async Task<TxCompletion> SendAwaitingCompletionAsync(
        ReadOnlyMemory<byte> ax25, TimeSpan? timeout = null, CancellationToken cancellationToken = default)
    {
        var tcs = new TaskCompletionSource<TxCompletion>(TaskCreationOptions.RunContinuationsAsynchronously);
        var accepted = queue.Writer.TryWrite(new TxJob(ax25.ToArray(), timeout, tcs));
        ObjectDisposedException.ThrowIf(!accepted, this);
        await using var reg = cancellationToken
            .Register(static (state, token) => ((TaskCompletionSource<TxCompletion>)state!).TrySetCanceled(token), tcs)
            .ConfigureAwait(false);
        return await tcs.Task.ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public IAsyncEnumerable<Ax25InboundFrame> ReceiveAsync(CancellationToken cancellationToken = default)
        => inner.ReceiveAsync(cancellationToken);

    // The CSMA channel-access params are forwarded to the inner transport when it exposes
    // them; an inner with no CSMA params (a transport that doesn't implement
    // ICsmaChannelParams) silently no-ops, preserving the pre-migration behaviour.

    /// <inheritdoc/>
    public Task SetTxDelayAsync(byte tenMsUnits, CancellationToken cancellationToken = default)
        => inner is ICsmaChannelParams c ? c.SetTxDelayAsync(tenMsUnits, cancellationToken) : Task.CompletedTask;

    /// <inheritdoc/>
    public Task SetPersistenceAsync(byte value, CancellationToken cancellationToken = default)
        => inner is ICsmaChannelParams c ? c.SetPersistenceAsync(value, cancellationToken) : Task.CompletedTask;

    /// <inheritdoc/>
    public Task SetSlotTimeAsync(byte tenMsUnits, CancellationToken cancellationToken = default)
        => inner is ICsmaChannelParams c ? c.SetSlotTimeAsync(tenMsUnits, cancellationToken) : Task.CompletedTask;

    /// <inheritdoc/>
    public Task SetTxTailAsync(byte tenMsUnits, CancellationToken cancellationToken = default)
        => inner is ICsmaChannelParams c ? c.SetTxTailAsync(tenMsUnits, cancellationToken) : Task.CompletedTask;

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0)
        {
            return;
        }

        // Stop accepting new frames, cancel the pump, and wait for it to drain out.
        queue.Writer.TryComplete();
        await lifecycle.CancelAsync().ConfigureAwait(false);
        try
        {
            await pump.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // expected on the cancel path
        }
        lifecycle.Dispose();

        // We own the inner transport (the standard decorator chain — see ReconnectingKissModem):
        // dispose it exactly once, here.
        await inner.DisposeAsync().ConfigureAwait(false);
    }

    [LoggerMessage(EventId = 5111, Level = LogLevel.Debug,
        Message = "ACKMODE pace: TX-completion echo not seen within {TimeoutMs} ms; releasing next frame.")]
    private partial void LogPaceTimeout(int timeoutMs);

    [LoggerMessage(EventId = 5113, Level = LogLevel.Debug,
        Message = "ACKMODE pace: inner transport disposed mid-send (link reconnecting); retrying on fresh link.")]
    private partial void LogPaceLinkSwapRetry();

    [LoggerMessage(EventId = 5112, Level = LogLevel.Warning,
        Message = "ACKMODE pace: paced send faulted; releasing next frame.")]
    private partial void LogPaceFaulted(Exception ex);
}
