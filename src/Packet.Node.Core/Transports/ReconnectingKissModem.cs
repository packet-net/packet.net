using System.Net.Sockets;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging;
using Packet.Ax25.Transport;

namespace Packet.Node.Core.Transports;

/// <summary>
/// Wraps a KISS modem (in practice a kiss-tcp client) so a node port survives the
/// far end bouncing: when the inbound frame stream ends — the TNC / softmodem /
/// net-sim dropped the connection — this transparently re-establishes a fresh
/// inner modem with capped exponential backoff and resumes, instead of the port
/// silently going dead until the process is restarted. The configured KISS
/// hardware parameters (TXDELAY etc.) are remembered and replayed on every
/// reconnect, since a fresh connection starts at the modem's defaults.
/// </summary>
/// <remarks>
/// <para>
/// The <em>initial</em> connection is made eagerly by the caller (the
/// <see cref="Hosting.PortSupervisor"/>), so a first-connect failure still faults
/// the port and is skipped — per-port fault isolation is unchanged. This wrapper
/// only handles re-connection after an established link drops. The reconnect path
/// retries through the endpoint being briefly unavailable (e.g. the peer
/// restarting), which is the actual bounce case.
/// </para>
/// <para>
/// A drop is detected two ways, both surfacing as the inner stream ending: a
/// graceful close (the peer sent a FIN — <c>KissTcpClient</c> reads 0 bytes and
/// throws) and — the case that actually requires operator intervention without
/// this (#464) — a <em>half-open</em> connection where the peer vanished with no
/// FIN (rebooted TNC, net-sim restart, cable yank). The latter is caught by
/// <c>KissTcpClient</c>'s read-idle liveness timeout (and OS TCP keepalive on a
/// real socket): a stalled read past the idle budget is treated as a dead link
/// and ends the stream, so this wrapper reconnects instead of the read hanging
/// forever.
/// </para>
/// <para>
/// In-flight AX.25 sessions on this port do NOT survive the bounce, and that is
/// correct: the listener (and its accept loop) is built once over this wrapper
/// and is never torn down across a reconnect — <see cref="ReadFramesAsync"/>
/// loops internally and never yields end-of-stream during a re-dial — so the
/// port stays in a working, listening state. But the far end (the TNC and the RF
/// peer beyond it) is gone, so any connected-mode session keyed to it will see
/// no acks, exhaust AX.25 T1/N2 retransmission, and disconnect by the normal
/// state-machine path. The node recovers to "ready to accept/originate new
/// connections," not "resumes the old session" — there is no AX.25 facility to
/// resume a session across a transport identity change, and attempting to would
/// leave a wedged half-open session. New connects work as soon as the link is
/// back and the KISS params are replayed.
/// </para>
/// <para>
/// Reads pump the current inner and reconnect on end-of-stream; sends and
/// parameter-sets target the current inner and are dropped best-effort while the
/// link is down — AX.25 T1 retransmits any lost I-frame once the link is back.
/// TX-completion sends delegate straight to the current inner: the inner KissTcpClient
/// intercepts its own TX-completion echoes on the same RX pump this wrapper
/// already delegates to (<see cref="ReceiveAsync"/> drives the inner's
/// enumerator), so the pending waiter and the echo live in one instance and a
/// mid-flight reconnect simply faults the in-flight completion (the caller retries).
/// </para>
/// </remarks>
internal sealed partial class ReconnectingKissModem : ITxCompletionTransport, ICsmaChannelParams, IAsyncDisposable
{
    private readonly Func<CancellationToken, Task<IAx25Transport>> reconnect;
    private readonly string endpoint;
    private readonly ILogger logger;
    private readonly TimeProvider time;
    private readonly TimeSpan minBackoff;
    private readonly TimeSpan maxBackoff;
    private readonly CancellationTokenSource lifecycle = new();

    // The live inner transport. Swapped on reconnect; read by sends/param-sets. Volatile
    // because the read pump (which swaps it) and a sending caller run concurrently.
    private volatile IAx25Transport inner;

    private readonly object paramGate = new();
    private byte? txDelay, persistence, slotTime, txTail;

    private int disposed;

    public ReconnectingKissModem(
        IAx25Transport initial,
        Func<CancellationToken, Task<IAx25Transport>> reconnect,
        string endpoint,
        ILogger logger,
        TimeProvider? timeProvider = null,
        TimeSpan? minBackoff = null,
        TimeSpan? maxBackoff = null)
    {
        inner = initial ?? throw new ArgumentNullException(nameof(initial));
        this.reconnect = reconnect ?? throw new ArgumentNullException(nameof(reconnect));
        this.endpoint = endpoint ?? "kiss-tcp";
        this.logger = logger ?? throw new ArgumentNullException(nameof(logger));
        time = timeProvider ?? TimeProvider.System;
        this.minBackoff = minBackoff ?? TimeSpan.FromSeconds(1);
        this.maxBackoff = maxBackoff ?? TimeSpan.FromSeconds(30);
    }

    /// <inheritdoc/>
    public async IAsyncEnumerable<Ax25InboundFrame> ReceiveAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, lifecycle.Token);
        var ct = linked.Token;

        while (!ct.IsCancellationRequested)
        {
            var live = inner;
            // Pump the current inner until its stream ends. The await-using is a
            // try/finally (no catch), so yielding inside it is legal; the per-step
            // try/catch around MoveNextAsync contains no yield.
            await using (var e = live.ReceiveAsync(ct).GetAsyncEnumerator(ct))
            {
                while (true)
                {
                    bool has;
                    try
                    {
                        has = await e.MoveNextAsync().ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        // The inner pump normally ends cleanly on a drop (KissTcpClient
                        // swallows the IOException and completes the stream); catch
                        // defensively so an unexpected throw becomes a reconnect, not a
                        // dead port.
                        if (!ct.IsCancellationRequested) LogInnerFaulted(ex, endpoint);
                        has = false;
                    }
                    if (!has) break;
                    yield return e.Current;
                }
            }

            if (ct.IsCancellationRequested) yield break;

            // End of stream with no cancellation = the far end dropped.
            LogDisconnected(endpoint);
            await DisposeQuietlyAsync(live).ConfigureAwait(false);

            var next = await ReconnectAsync(ct).ConfigureAwait(false);
            if (next is null) yield break;   // cancelled while reconnecting
            inner = next;
            LogReconnected(endpoint);
            await ReplayParamsAsync(next, ct).ConfigureAwait(false);
        }
    }

    // Try to reconnect, immediately first, then with capped exponential backoff on
    // each failure. Returns the fresh transport, or null if cancelled.
    private async Task<IAx25Transport?> ReconnectAsync(CancellationToken ct)
    {
        var backoff = minBackoff;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                return await reconnect(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return null;
            }
            catch (Exception ex)
            {
                LogReconnectFailed(endpoint, (int)Math.Ceiling(backoff.TotalSeconds), ex.Message);
            }
            try
            {
                await Task.Delay(backoff, time, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return null;
            }
            var doubled = backoff.Ticks * 2;
            backoff = TimeSpan.FromTicks(Math.Clamp(doubled, minBackoff.Ticks, maxBackoff.Ticks));
        }
        return null;
    }

    /// <inheritdoc/>
    public async Task SendAsync(ReadOnlyMemory<byte> ax25, CancellationToken cancellationToken = default)
    {
        try
        {
            await inner.SendAsync(ax25, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (IsTransient(ex))
        {
            // Link down (mid-reconnect); drop — AX.25 T1 will retransmit once back.
            LogSendDropped(endpoint);
        }
    }

    /// <inheritdoc/>
    /// <remarks>
    /// Delegates to the current inner transport. The echo that resolves this send is
    /// intercepted inside that same inner instance's RX pump (which this wrapper
    /// drives via <see cref="ReceiveAsync"/>), so the pending waiter and the
    /// echo never split across instances. If the link drops mid-flight the inner
    /// faults the waiter (TimeoutException / ObjectDisposedException) and AX.25
    /// T1 retransmits once the link is back, exactly like a plain send.
    /// Throws <see cref="NotSupportedException"/> if the live inner is not
    /// TX-completion-capable — but a port only wraps in this decorator over a
    /// transport that is (the kiss-tcp path), matching the pre-migration behaviour
    /// where the inner always exposed <c>SendFrameWithAckAsync</c>.
    /// </remarks>
    public Task<TxCompletion> SendAwaitingCompletionAsync(
        ReadOnlyMemory<byte> ax25, TimeSpan? timeout = null, CancellationToken cancellationToken = default)
        => inner is ITxCompletionTransport tx
            ? tx.SendAwaitingCompletionAsync(ax25, timeout, cancellationToken)
            : throw new NotSupportedException("the current inner transport does not support TX-completion.");

    /// <inheritdoc/>
    public Task SetTxDelayAsync(byte tenMsUnits, CancellationToken cancellationToken = default)
    {
        lock (paramGate) txDelay = tenMsUnits;
        return ApplyParamAsync(m => m.SetTxDelayAsync(tenMsUnits, cancellationToken));
    }

    /// <inheritdoc/>
    public Task SetPersistenceAsync(byte value, CancellationToken cancellationToken = default)
    {
        lock (paramGate) persistence = value;
        return ApplyParamAsync(m => m.SetPersistenceAsync(value, cancellationToken));
    }

    /// <inheritdoc/>
    public Task SetSlotTimeAsync(byte tenMsUnits, CancellationToken cancellationToken = default)
    {
        lock (paramGate) slotTime = tenMsUnits;
        return ApplyParamAsync(m => m.SetSlotTimeAsync(tenMsUnits, cancellationToken));
    }

    /// <inheritdoc/>
    public Task SetTxTailAsync(byte tenMsUnits, CancellationToken cancellationToken = default)
    {
        lock (paramGate) txTail = tenMsUnits;
        return ApplyParamAsync(m => m.SetTxTailAsync(tenMsUnits, cancellationToken));
    }

    // Best-effort apply to the current inner (when it exposes the CSMA params); if the
    // link is down it's a no-op now and gets replayed on reconnect. An inner with no
    // CSMA params (a transport that doesn't implement ICsmaChannelParams) silently
    // no-ops, exactly as the KissModemTransport-wrapped AXUDP path did.
    private async Task ApplyParamAsync(Func<ICsmaChannelParams, Task> op)
    {
        if (inner is not ICsmaChannelParams csma) return;
        try
        {
            await op(csma).ConfigureAwait(false);
        }
        catch (Exception ex) when (IsTransient(ex))
        {
            // dropped; ReplayParamsAsync re-sends on the next connect
        }
    }

    // Re-send the last-set value of each CSMA parameter to a freshly-connected
    // transport (a new connection starts at the modem's defaults). A transport with
    // no CSMA params is skipped.
    private async Task ReplayParamsAsync(IAx25Transport m, CancellationToken ct)
    {
        if (m is not ICsmaChannelParams csma) return;
        byte? d, p, s, t;
        lock (paramGate) { d = txDelay; p = persistence; s = slotTime; t = txTail; }
        try
        {
            if (d is { } dd) await csma.SetTxDelayAsync(dd, ct).ConfigureAwait(false);
            if (p is { } pp) await csma.SetPersistenceAsync(pp, ct).ConfigureAwait(false);
            if (s is { } ss) await csma.SetSlotTimeAsync(ss, ct).ConfigureAwait(false);
            if (t is { } tt) await csma.SetTxTailAsync(tt, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (IsTransient(ex))
        {
            // Lost the link again mid-replay; the next reconnect replays afresh.
        }
    }

    private static bool IsTransient(Exception ex)
        => ex is IOException or ObjectDisposedException or SocketException or InvalidOperationException;

    private static async ValueTask DisposeQuietlyAsync(IAx25Transport m)
    {
        try
        {
            await m.DisposeAsync().ConfigureAwait(false);
        }
        catch
        {
            // best-effort — we're discarding this transport anyway
        }
    }

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        await lifecycle.CancelAsync().ConfigureAwait(false);
        await DisposeQuietlyAsync(inner).ConfigureAwait(false);
        lifecycle.Dispose();
    }

    [LoggerMessage(EventId = 5101, Level = LogLevel.Warning, Message = "KISS-TCP link {Endpoint} dropped; reconnecting.")]
    private partial void LogDisconnected(string endpoint);

    [LoggerMessage(EventId = 5102, Level = LogLevel.Information, Message = "KISS-TCP link {Endpoint} reconnected.")]
    private partial void LogReconnected(string endpoint);

    [LoggerMessage(EventId = 5103, Level = LogLevel.Warning, Message = "KISS-TCP reconnect to {Endpoint} failed ({Reason}); retrying in {Seconds}s.")]
    private partial void LogReconnectFailed(string endpoint, int seconds, string reason);

    [LoggerMessage(EventId = 5104, Level = LogLevel.Warning, Message = "KISS-TCP inbound pump for {Endpoint} faulted; treating as a drop.")]
    private partial void LogInnerFaulted(Exception ex, string endpoint);

    [LoggerMessage(EventId = 5105, Level = LogLevel.Debug, Message = "KISS-TCP send on {Endpoint} dropped (link down); AX.25 will retransmit.")]
    private partial void LogSendDropped(string endpoint);
}
