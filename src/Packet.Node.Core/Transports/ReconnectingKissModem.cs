using System.Net.Sockets;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging;
using Packet.Kiss;

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
/// Reads pump the current inner and reconnect on end-of-stream; sends and
/// parameter-sets target the current inner and are dropped best-effort while the
/// link is down — AX.25 T1 retransmits any lost I-frame once the link is back.
/// ACKMODE sends delegate straight to the current inner: the inner KissTcpClient
/// intercepts its own TX-completion echoes on the same RX pump this wrapper
/// already delegates to (<see cref="ReadFramesAsync"/> drives the inner's
/// enumerator), so the pending waiter and the echo live in one instance and a
/// mid-flight reconnect simply faults the in-flight ACK (the caller retries).
/// </para>
/// </remarks>
internal sealed partial class ReconnectingKissModem : IKissModem, IAsyncDisposable
{
    private readonly Func<CancellationToken, Task<IKissModem>> reconnect;
    private readonly string endpoint;
    private readonly ILogger logger;
    private readonly TimeProvider time;
    private readonly TimeSpan minBackoff;
    private readonly TimeSpan maxBackoff;
    private readonly CancellationTokenSource lifecycle = new();

    // The live inner modem. Swapped on reconnect; read by sends/param-sets. Volatile
    // because the read pump (which swaps it) and a sending caller run concurrently.
    private volatile IKissModem inner;

    private readonly object paramGate = new();
    private byte? txDelay, persistence, slotTime, txTail;

    private int disposed;

    public ReconnectingKissModem(
        IKissModem initial,
        Func<CancellationToken, Task<IKissModem>> reconnect,
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
    public async IAsyncEnumerable<KissFrame> ReadFramesAsync(
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
            await using (var e = live.ReadFramesAsync(ct).GetAsyncEnumerator(ct))
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
    // each failure. Returns the fresh modem, or null if cancelled.
    private async Task<IKissModem?> ReconnectAsync(CancellationToken ct)
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
    public async Task SendFrameAsync(ReadOnlyMemory<byte> ax25Bytes, CancellationToken cancellationToken = default)
    {
        try
        {
            await inner.SendFrameAsync(ax25Bytes, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (IsTransient(ex))
        {
            // Link down (mid-reconnect); drop — AX.25 T1 will retransmit once back.
            LogSendDropped(endpoint);
        }
    }

    /// <inheritdoc/>
    /// <remarks>
    /// Delegates to the current inner modem. The echo that resolves this send is
    /// intercepted inside that same inner instance's RX pump (which this wrapper
    /// drives via <see cref="ReadFramesAsync"/>), so the pending waiter and the
    /// echo never split across instances. If the link drops mid-flight the inner
    /// faults the waiter (TimeoutException / ObjectDisposedException) and AX.25
    /// T1 retransmits once the link is back, exactly like a plain send.
    /// </remarks>
    public Task<AckModeReceipt> SendFrameWithAckAsync(
        ReadOnlyMemory<byte> ax25Bytes, TimeSpan? timeout = null, ushort? sequenceTag = null, CancellationToken cancellationToken = default)
        => inner.SendFrameWithAckAsync(ax25Bytes, timeout, sequenceTag, cancellationToken);

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

    // Best-effort apply to the current inner; if the link is down it's a no-op now
    // and gets replayed on reconnect.
    private async Task ApplyParamAsync(Func<IKissModem, Task> op)
    {
        try
        {
            await op(inner).ConfigureAwait(false);
        }
        catch (Exception ex) when (IsTransient(ex))
        {
            // dropped; ReplayParamsAsync re-sends on the next connect
        }
    }

    // Re-send the last-set value of each KISS parameter to a freshly-connected
    // modem (a new connection starts at the modem's defaults).
    private async Task ReplayParamsAsync(IKissModem m, CancellationToken ct)
    {
        byte? d, p, s, t;
        lock (paramGate) { d = txDelay; p = persistence; s = slotTime; t = txTail; }
        try
        {
            if (d is { } dd) await m.SetTxDelayAsync(dd, ct).ConfigureAwait(false);
            if (p is { } pp) await m.SetPersistenceAsync(pp, ct).ConfigureAwait(false);
            if (s is { } ss) await m.SetSlotTimeAsync(ss, ct).ConfigureAwait(false);
            if (t is { } tt) await m.SetTxTailAsync(tt, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (IsTransient(ex))
        {
            // Lost the link again mid-replay; the next reconnect replays afresh.
        }
    }

    private static bool IsTransient(Exception ex)
        => ex is IOException or ObjectDisposedException or SocketException or InvalidOperationException;

    private static async ValueTask DisposeQuietlyAsync(IKissModem m)
    {
        try
        {
            switch (m)
            {
                case IAsyncDisposable ad: await ad.DisposeAsync().ConfigureAwait(false); break;
                case IDisposable d: d.Dispose(); break;
            }
        }
        catch
        {
            // best-effort — we're discarding this modem anyway
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
