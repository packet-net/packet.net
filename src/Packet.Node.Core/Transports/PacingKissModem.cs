using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using Packet.Kiss;

namespace Packet.Node.Core.Transports;

/// <summary>
/// Paces a port's outbound transmissions to a real half-duplex channel. Wraps an
/// inner KISS modem (in practice a <see cref="ReconnectingKissModem"/> over a
/// kiss-tcp link) and turns the fire-and-forget blast the <c>Ax25Listener</c> sink
/// emits into a <b>serialised, one-at-a-time</b> stream: each frame is sent in
/// ACKMODE (<see cref="IKissModem.SendFrameWithAckAsync"/>) and the next frame is
/// not released until the TNC's TX-completion echo for the prior frame arrives (or
/// a short timeout elapses). On the software-RF lab channel (net-sim) — and on a
/// real TNC that honours the G8BPQ ACKMODE extension — that echo means "the frame
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
internal sealed partial class PacingKissModem : IKissModem, IAsyncDisposable
{
    private readonly IKissModem inner;
    private readonly TimeSpan pacingTimeout;
    private readonly ILogger logger;
    private readonly Channel<byte[]> queue;
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
    public PacingKissModem(IKissModem inner, TimeSpan? pacingTimeout = null, ILogger? logger = null)
    {
        this.inner = inner ?? throw new ArgumentNullException(nameof(inner));
        this.pacingTimeout = pacingTimeout ?? DefaultPacingTimeout;
        this.logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance;

        // Unbounded: the listener's send sink is fire-and-forget and must never block,
        // and the node's own offered load is naturally bounded by the AX.25 send windows
        // across its sessions. Single reader (the pump), many writers (the sink).
        queue = Channel.CreateUnbounded<byte[]>(new UnboundedChannelOptions
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
    public Task SendFrameAsync(ReadOnlyMemory<byte> ax25Bytes, CancellationToken cancellationToken = default)
    {
        // Copy out of the caller's (reusable) buffer before returning — the pump reads
        // it later, off this call's stack.
        var frame = ax25Bytes.ToArray();

        // TryWrite always succeeds on an unbounded channel until it is completed; once
        // disposal completes the writer, a late send is silently dropped (the port is
        // going away — AX.25 retransmit covers a genuinely-needed frame).
        queue.Writer.TryWrite(frame);
        return Task.CompletedTask;
    }

    // The single background pump: drain the queue and send each frame in ACKMODE,
    // awaiting its TX-completion echo before pulling the next. That await IS the pacing.
    private async Task PumpAsync(CancellationToken ct)
    {
        try
        {
            await foreach (var frame in queue.Reader.ReadAllAsync(ct).ConfigureAwait(false))
            {
                try
                {
                    await inner.SendFrameWithAckAsync(frame, pacingTimeout, sequenceTag: null, ct)
                        .ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    // Shutting down — stop pumping.
                    break;
                }
                catch (TimeoutException)
                {
                    // The echo never came within the pacing window. Don't wedge the pump:
                    // the frame is already on the wire (ACKMODE wraps a real send), and
                    // AX.25 T1 retransmits anything genuinely lost. Release the next frame.
                    LogPaceTimeout((int)pacingTimeout.TotalMilliseconds);
                }
                catch (Exception ex)
                {
                    // Any other send fault (link bounce mid-send, modem disposed): log and
                    // keep pumping so one bad frame can never stall the whole port.
                    LogPaceFaulted(ex);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Disposal cancelled the pump while it waited on the reader — normal shutdown.
        }
    }

    /// <inheritdoc/>
    /// <remarks>Delegates straight to the inner modem — a caller that explicitly wants an
    /// ACKMODE receipt bypasses the pacing queue and awaits its own echo.</remarks>
    public Task<AckModeReceipt> SendFrameWithAckAsync(
        ReadOnlyMemory<byte> ax25Bytes, TimeSpan? timeout = null, ushort? sequenceTag = null, CancellationToken cancellationToken = default)
        => inner.SendFrameWithAckAsync(ax25Bytes, timeout, sequenceTag, cancellationToken);

    /// <inheritdoc/>
    public IAsyncEnumerable<KissFrame> ReadFramesAsync(CancellationToken cancellationToken = default)
        => inner.ReadFramesAsync(cancellationToken);

    /// <inheritdoc/>
    public Task SetTxDelayAsync(byte tenMsUnits, CancellationToken cancellationToken = default)
        => inner.SetTxDelayAsync(tenMsUnits, cancellationToken);

    /// <inheritdoc/>
    public Task SetPersistenceAsync(byte value, CancellationToken cancellationToken = default)
        => inner.SetPersistenceAsync(value, cancellationToken);

    /// <inheritdoc/>
    public Task SetSlotTimeAsync(byte tenMsUnits, CancellationToken cancellationToken = default)
        => inner.SetSlotTimeAsync(tenMsUnits, cancellationToken);

    /// <inheritdoc/>
    public Task SetTxTailAsync(byte tenMsUnits, CancellationToken cancellationToken = default)
        => inner.SetTxTailAsync(tenMsUnits, cancellationToken);

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;

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

        // We own the inner modem (the standard decorator chain — see ReconnectingKissModem):
        // dispose it exactly once, here.
        switch (inner)
        {
            case IAsyncDisposable ad:
                await ad.DisposeAsync().ConfigureAwait(false);
                break;
            case IDisposable d:
                d.Dispose();
                break;
        }
    }

    [LoggerMessage(EventId = 5111, Level = LogLevel.Debug,
        Message = "ACKMODE pace: TX-completion echo not seen within {TimeoutMs} ms; releasing next frame.")]
    private partial void LogPaceTimeout(int timeoutMs);

    [LoggerMessage(EventId = 5112, Level = LogLevel.Warning,
        Message = "ACKMODE pace: paced send faulted; releasing next frame.")]
    private partial void LogPaceFaulted(Exception ex);
}
