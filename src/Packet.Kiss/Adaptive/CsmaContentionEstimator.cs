using System.Collections.Concurrent;

namespace Packet.Kiss.Adaptive;

/// <summary>
/// Adaptive PERSIST + SLOTTIME estimator. Watches frame outcomes for
/// channel-contention signals — specifically <see cref="FrameOutcome.AckModeTimedOut"/>
/// (the TNC accepted the frame but the modem's CSMA never got a clear
/// channel before the host timed out) and <see cref="FrameOutcome.Lost"/>
/// (frame got out but the peer never ACKed at the AX.25 layer) — and
/// ratchets the CSMA knobs in response: drop PERSIST (less aggressive
/// transmit) and raise SLOTTIME (longer back-off) on contention,
/// slowly walk PERSIST back up when the channel stays clear.
/// </summary>
/// <remarks>
/// <para>
/// PERSIST is the 0–255 probability parameter (transmit-when-free
/// probability is roughly (P+1)/256). SLOTTIME is the back-off slot
/// length in 10 ms units. The KISS-spec defaults are 63 / 10.
/// </para>
/// <para>
/// TXDELAY is *not* tuned by this estimator — its
/// <see cref="Recommend"/> returns null for the TXDELAY field so the
/// caller can compose it with <see cref="TxDelayHillClimbEstimator"/>
/// (or any other TXDELAY estimator) via
/// <see cref="CompositeAdaptiveEstimator"/>.
/// </para>
/// <para>
/// On the bench (audio cross-wire, no real CSMA contention) this
/// estimator stays at its initial values forever — there's nothing
/// to learn from. It earns its keep on a real shared channel.
/// </para>
/// </remarks>
public sealed class CsmaContentionEstimator : IAdaptiveParameterEstimator
{
    /// <summary>Floor on PERSIST. Below this the TNC is so polite it never wins a slot.</summary>
    public byte MinPersistence { get; init; } = 16;

    /// <summary>Ceiling on PERSIST (255 = always transmit when free).</summary>
    public byte MaxPersistence { get; init; } = 255;

    /// <summary>Floor on SLOTTIME (units of 10 ms). 1 unit = 10 ms.</summary>
    public byte MinSlotTime { get; init; } = 5;

    /// <summary>Ceiling on SLOTTIME (units of 10 ms). 50 units = 500 ms.</summary>
    public byte MaxSlotTime { get; init; } = 50;

    /// <summary>How many consecutive first-try ACKs trigger a step-up of PERSIST.</summary>
    public int SuccessesPerPersistStepUp { get; init; } = 16;

    /// <summary>PERSIST step on success (towards <see cref="MaxPersistence"/>).</summary>
    public byte PersistStepUp { get; init; } = 4;

    /// <summary>PERSIST step on a contention signal (towards <see cref="MinPersistence"/>).</summary>
    public byte PersistStepDown { get; init; } = 16;

    /// <summary>SLOTTIME step on a contention signal (towards <see cref="MaxSlotTime"/>).</summary>
    public byte SlotTimeStepUp { get; init; } = 2;

    private readonly byte initialPersistence;
    private readonly byte initialSlotTime;
    private readonly ConcurrentDictionary<string, PeerState> peerState = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Create an estimator with the given starting CSMA values. Defaults
    /// match the KISS TNC spec (PERSIST=63, SLOTTIME=10 = 100 ms).
    /// </summary>
    public CsmaContentionEstimator(byte initialPersistence = 63, byte initialSlotTime = 10)
    {
        this.initialPersistence = initialPersistence;
        this.initialSlotTime = initialSlotTime;
    }

    /// <inheritdoc/>
    public KissParameters Recommend(string peer)
    {
        ArgumentException.ThrowIfNullOrEmpty(peer);
        var state = peerState.GetOrAdd(peer, _ => new PeerState(initialPersistence, initialSlotTime));
        return new KissParameters(
            TxDelayTenMsUnits: null,
            Persistence: state.Persistence,
            SlotTimeTenMsUnits: state.SlotTime,
            TxTailTenMsUnits: null);
    }

    /// <inheritdoc/>
    public void Observe(FrameOutcomeSample sample)
    {
        ArgumentException.ThrowIfNullOrEmpty(sample.Peer);
        var state = peerState.GetOrAdd(sample.Peer, _ => new PeerState(initialPersistence, initialSlotTime));
        lock (state.Gate)
        {
            switch (sample.Outcome)
            {
                case FrameOutcome.AcknowledgedFirstTry:
                    state.SuccessStreak++;
                    if (state.SuccessStreak >= SuccessesPerPersistStepUp && state.Persistence < MaxPersistence)
                    {
                        int next = state.Persistence + PersistStepUp;
                        state.Persistence = (byte)Math.Min(MaxPersistence, next);
                        state.SuccessStreak = 0;
                    }
                    break;
                case FrameOutcome.AcknowledgedAfterRetransmit:
                    // Retransmit doesn't necessarily mean CSMA contention — could
                    // be preamble too short, peer missed a bit, etc. Reset the
                    // streak but don't penalise.
                    state.SuccessStreak = 0;
                    break;
                case FrameOutcome.AckModeTimedOut:
                    // Strong contention signal: the TNC accepted the frame but
                    // never managed to key it onto the air in time.
                    state.SuccessStreak = 0;
                    state.Persistence = (byte)Math.Max(MinPersistence, state.Persistence - PersistStepDown);
                    state.SlotTime = (byte)Math.Min(MaxSlotTime, state.SlotTime + SlotTimeStepUp);
                    break;
                case FrameOutcome.Lost:
                    // Frame went out over the air but the peer never ACKed at
                    // the AX.25 layer. Could be collision (CSMA-ish) or could
                    // be range / link quality. Treat as a soft contention
                    // signal — drop PERSIST a bit, leave SLOTTIME.
                    state.SuccessStreak = 0;
                    state.Persistence = (byte)Math.Max(MinPersistence, state.Persistence - (PersistStepDown / 2));
                    break;
            }
        }
    }

    /// <summary>Current PERSIST recommendation for <paramref name="peer"/>, for diagnostics.</summary>
    public byte? CurrentPersistenceFor(string peer) =>
        peerState.TryGetValue(peer, out var state) ? state.Persistence : null;

    /// <summary>Current SLOTTIME recommendation (10 ms units) for <paramref name="peer"/>.</summary>
    public byte? CurrentSlotTimeFor(string peer) =>
        peerState.TryGetValue(peer, out var state) ? state.SlotTime : null;

    private sealed class PeerState(byte persistence, byte slotTime)
    {
        public byte Persistence = persistence;
        public byte SlotTime = slotTime;
        public int SuccessStreak;
        public readonly Lock Gate = new();
    }
}
