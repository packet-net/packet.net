using Packet.Ax25.Transport;
using Packet.Kiss.Adaptive;

namespace Packet.Kiss;

/// <summary>
/// Glues an <see cref="IAdaptiveParameterEstimator"/> to any
/// <see cref="IAx25Transport"/> that can both confirm TX-completion
/// (<see cref="ITxCompletionTransport"/>) and set the CSMA channel-access
/// params (<see cref="ICsmaChannelParams"/>). Before each TX it asks the
/// estimator what parameters to use for the destination peer, applies them via
/// the channel-param setters, sends the frame awaiting TX-completion so the host
/// gets a real completion signal, then feeds the outcome back into the
/// estimator.
/// </summary>
/// <remarks>
/// <para>
/// CSMA channel params (TXDELAY etc.) are <em>per-modem</em>, not per-peer
/// — the TNC applies whatever value was last configured to every
/// subsequent transmission. To realise per-peer adaptive parameters on
/// a single modem, this transport re-applies the per-peer recommendation
/// immediately before each TX. That costs one extra short serial write
/// per frame, which is negligible against any reasonable airtime budget.
/// </para>
/// <para>
/// Concurrent <see cref="SendAsync"/> calls would race the parameter
/// changes (a frame for peer A could fly out with peer B's TXDELAY).
/// The transport serialises sends through an internal semaphore so
/// callers don't have to.
/// </para>
/// <para>
/// The transport tracks the last-applied value for each parameter and
/// only sends a SET command when the value has changed, to avoid spamming
/// the TNC's parameter writes.
/// </para>
/// </remarks>
public sealed class AdaptiveKissTransport : IAsyncDisposable, IDisposable
{
    private readonly ITxCompletionTransport modem;
    private readonly ICsmaChannelParams channelParams;
    private readonly IAdaptiveParameterEstimator estimator;
    private readonly SemaphoreSlim sendLock = new(1, 1);
    private readonly TimeProvider time;

    private KissParameters lastApplied;
    private int disposed;

    /// <summary>The default TX-completion timeout if the caller doesn't supply one.</summary>
    public TimeSpan DefaultAckTimeout { get; init; } = TimeSpan.FromSeconds(30);

    /// <param name="modem">The TX-completion-capable transport to drive (a KISS TNC with
    /// ACKMODE, etc.). Must also implement <see cref="ICsmaChannelParams"/> so the per-peer
    /// channel-access params can be applied before each TX.</param>
    /// <param name="estimator">The per-peer parameter estimator.</param>
    /// <param name="time">Clock for observation timestamps (test seam).</param>
    /// <exception cref="ArgumentException"><paramref name="modem"/> does not also implement
    /// <see cref="ICsmaChannelParams"/>.</exception>
    public AdaptiveKissTransport(
        ITxCompletionTransport modem,
        IAdaptiveParameterEstimator estimator,
        TimeProvider? time = null)
    {
        ArgumentNullException.ThrowIfNull(modem);
        ArgumentNullException.ThrowIfNull(estimator);
        this.modem = modem;
        channelParams = modem as ICsmaChannelParams
            ?? throw new ArgumentException(
                "the adaptive transport needs a transport that also exposes ICsmaChannelParams.", nameof(modem));
        this.estimator = estimator;
        this.time = time ?? TimeProvider.System;
        lastApplied = new KissParameters(null, null, null, null);
    }

    /// <summary>
    /// Send <paramref name="ax25Bytes"/> to the given peer, awaiting TX-completion.
    /// Applies the estimator's per-peer parameter recommendation first.
    /// </summary>
    /// <param name="peer">Peer identifier (typically a callsign in canonical text form).</param>
    /// <param name="ax25Bytes">AX.25 frame body (no FCS, no KISS framing).</param>
    /// <param name="ackTimeout">Override the default TX-completion timeout.</param>
    public async Task<TxCompletion> SendAsync(
        string peer,
        ReadOnlyMemory<byte> ax25Bytes,
        TimeSpan? ackTimeout = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(peer);

        await sendLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var recommendation = estimator.Recommend(peer);
            await ApplyParameterDiffAsync(recommendation, cancellationToken).ConfigureAwait(false);

            TxCompletion receipt;
            try
            {
                receipt = await modem.SendAwaitingCompletionAsync(
                    ax25Bytes,
                    timeout: ackTimeout ?? DefaultAckTimeout,
                    cancellationToken: cancellationToken).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                estimator.Observe(new FrameOutcomeSample(
                    Peer: peer,
                    Outcome: FrameOutcome.AckModeTimedOut,
                    PayloadBytes: ax25Bytes.Length,
                    ParametersUsed: recommendation,
                    AckElapsed: null,
                    ObservedAtUtc: time.GetUtcNow()));
                throw;
            }
            catch (OperationCanceledException)
            {
                // Cancellation isn't a learning signal; just propagate.
                throw;
            }

            estimator.Observe(new FrameOutcomeSample(
                Peer: peer,
                Outcome: FrameOutcome.AcknowledgedFirstTry,
                PayloadBytes: ax25Bytes.Length,
                ParametersUsed: recommendation,
                AckElapsed: receipt.Elapsed,
                ObservedAtUtc: time.GetUtcNow()));
            return receipt;
        }
        finally
        {
            sendLock.Release();
        }
    }

    /// <summary>
    /// Record an externally-observed loss (e.g. AX.25-layer retransmit
    /// timeout, peer never ACKed). The estimator uses this signal
    /// alongside the per-send ACKMODE outcomes.
    /// </summary>
    public void RecordLoss(string peer, int payloadBytes)
    {
        ArgumentException.ThrowIfNullOrEmpty(peer);
        estimator.Observe(new FrameOutcomeSample(
            Peer: peer,
            Outcome: FrameOutcome.Lost,
            PayloadBytes: payloadBytes,
            ParametersUsed: lastApplied,
            AckElapsed: null,
            ObservedAtUtc: time.GetUtcNow()));
    }

    /// <summary>
    /// Record an AX.25-layer retransmit (the frame did eventually get
    /// through but only after one or more replays). Counted as a soft
    /// signal — the estimator won't bump TXDELAY up but it'll reset the
    /// success streak.
    /// </summary>
    public void RecordRetransmittedAck(string peer, int payloadBytes)
    {
        ArgumentException.ThrowIfNullOrEmpty(peer);
        estimator.Observe(new FrameOutcomeSample(
            Peer: peer,
            Outcome: FrameOutcome.AcknowledgedAfterRetransmit,
            PayloadBytes: payloadBytes,
            ParametersUsed: lastApplied,
            AckElapsed: null,
            ObservedAtUtc: time.GetUtcNow()));
    }

    private async Task ApplyParameterDiffAsync(KissParameters recommendation, CancellationToken cancellationToken)
    {
        if (recommendation.TxDelayTenMsUnits is byte tx && lastApplied.TxDelayTenMsUnits != tx)
        {
            await channelParams.SetTxDelayAsync(tx, cancellationToken).ConfigureAwait(false);
            lastApplied = lastApplied with { TxDelayTenMsUnits = tx };
        }
        if (recommendation.Persistence is byte p && lastApplied.Persistence != p)
        {
            await channelParams.SetPersistenceAsync(p, cancellationToken).ConfigureAwait(false);
            lastApplied = lastApplied with { Persistence = p };
        }
        if (recommendation.SlotTimeTenMsUnits is byte s && lastApplied.SlotTimeTenMsUnits != s)
        {
            await channelParams.SetSlotTimeAsync(s, cancellationToken).ConfigureAwait(false);
            lastApplied = lastApplied with { SlotTimeTenMsUnits = s };
        }
        if (recommendation.TxTailTenMsUnits is byte tt && lastApplied.TxTailTenMsUnits != tt)
        {
            await channelParams.SetTxTailAsync(tt, cancellationToken).ConfigureAwait(false);
            lastApplied = lastApplied with { TxTailTenMsUnits = tt };
        }
    }

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0)
        {
            return;
        }
        sendLock.Dispose();
        await ValueTask.CompletedTask.ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public void Dispose() => DisposeAsync().AsTask().GetAwaiter().GetResult();
}
