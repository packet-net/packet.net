using Packet.Kiss;
using Packet.Kiss.Adaptive;

namespace Packet.Kiss.NinoTnc;

/// <summary>
/// Glues an <see cref="IAdaptiveParameterEstimator"/> to a
/// <see cref="NinoTncSerialPort"/>. Before each TX it asks the estimator
/// what parameters to use for the destination peer, applies them via KISS
/// SETTING commands, sends the frame using ACKMODE so we get a real
/// TX-completion signal, then feeds the outcome back into the estimator.
/// </summary>
/// <remarks>
/// <para>
/// KISS parameters (TXDELAY etc.) are <em>per-modem</em>, not per-peer — the
/// TNC applies whatever value was last configured to every subsequent
/// transmission. To realise per-peer adaptive parameters on a single modem,
/// this transport re-applies the per-peer recommendation immediately before
/// each TX. That costs one extra short serial write per frame, which is
/// negligible against any reasonable airtime budget.
/// </para>
/// <para>
/// Concurrent <see cref="SendAsync"/> calls would race the parameter
/// changes (a frame for peer A could fly out with peer B's TXDELAY). The
/// transport serialises sends through an internal semaphore so callers
/// don't have to.
/// </para>
/// <para>
/// The transport tracks the last-applied value for each parameter and only
/// sends a SET command when the value has changed, to avoid spamming the
/// TNC's parameter writes.
/// </para>
/// </remarks>
public sealed class AdaptiveNinoTncTransport : IAsyncDisposable, IDisposable
{
    private readonly INinoTncModem modem;
    private readonly IAdaptiveParameterEstimator estimator;
    private readonly SemaphoreSlim sendLock = new(1, 1);
    private readonly TimeProvider time;

    private KissParameters lastApplied;
    private int disposed;

    /// <summary>The default ACKMODE timeout if the caller doesn't supply one.</summary>
    public TimeSpan DefaultAckTimeout { get; init; } = TimeSpan.FromSeconds(30);

    public AdaptiveNinoTncTransport(
        INinoTncModem modem,
        IAdaptiveParameterEstimator estimator,
        TimeProvider? time = null)
    {
        ArgumentNullException.ThrowIfNull(modem);
        ArgumentNullException.ThrowIfNull(estimator);
        this.modem = modem;
        this.estimator = estimator;
        this.time = time ?? TimeProvider.System;
        lastApplied = new KissParameters(null, null, null, null);
    }

    /// <summary>
    /// Send <paramref name="ax25Bytes"/> to the given peer over ACKMODE.
    /// Applies the estimator's per-peer parameter recommendation first.
    /// </summary>
    /// <param name="peer">Peer identifier (typically a callsign in canonical text form).</param>
    /// <param name="ax25Bytes">AX.25 frame body (no FCS, no KISS framing).</param>
    /// <param name="ackTimeout">Override the default ACKMODE timeout.</param>
    public async Task<AckModeReceipt> SendAsync(
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

            AckModeReceipt receipt;
            try
            {
                receipt = await modem.SendFrameWithAckAsync(
                    ax25Bytes,
                    timeout: ackTimeout ?? DefaultAckTimeout,
                    sequenceTag: null,
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
    /// timeout, peer never ACKed). The estimator uses this signal alongside
    /// the per-send ACKMODE outcomes.
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
    /// Record an AX.25-layer retransmit (the frame did eventually get through
    /// but only after one or more replays). Counted as a soft signal — the
    /// estimator won't bump TXDELAY up but it'll reset the success streak.
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
            await modem.SetTxDelayAsync(tx, cancellationToken).ConfigureAwait(false);
            lastApplied = lastApplied with { TxDelayTenMsUnits = tx };
        }
        if (recommendation.Persistence is byte p && lastApplied.Persistence != p)
        {
            await modem.SetPersistenceAsync(p, cancellationToken).ConfigureAwait(false);
            lastApplied = lastApplied with { Persistence = p };
        }
        if (recommendation.SlotTimeTenMsUnits is byte s && lastApplied.SlotTimeTenMsUnits != s)
        {
            await modem.SetSlotTimeAsync(s, cancellationToken).ConfigureAwait(false);
            lastApplied = lastApplied with { SlotTimeTenMsUnits = s };
        }
        if (recommendation.TxTailTenMsUnits is byte tt && lastApplied.TxTailTenMsUnits != tt)
        {
            await modem.SetTxTailAsync(tt, cancellationToken).ConfigureAwait(false);
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
