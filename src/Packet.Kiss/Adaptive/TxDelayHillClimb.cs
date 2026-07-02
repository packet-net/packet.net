using System.Collections.Concurrent;

namespace Packet.Kiss.Adaptive;

/// <summary>
/// A first-pass adaptive TXDELAY estimator. Walks the per-peer TXDELAY value
/// down while the success rate stays high, and ratchets it back up after
/// losses. Other KISS parameters are passed through unchanged — those are
/// follow-up work.
/// </summary>
/// <remarks>
/// <para>
/// Algorithm sketch:
/// </para>
/// <list type="number">
///   <item>Per peer, maintain a current recommended TXDELAY (default: 50 =
///         500 ms; clamped to <see cref="MinTxDelay"/>..<see cref="MaxTxDelay"/>).</item>
///   <item>On <see cref="FrameOutcome.AcknowledgedFirstTry"/> count a
///         success; every <see cref="SuccessesPerStepDown"/> successes,
///         decrement by <see cref="StepUnits"/>.</item>
///   <item>On <see cref="FrameOutcome.Lost"/> or
///         <see cref="FrameOutcome.AckModeTimedOut"/>, increment by
///         <see cref="LossPenaltyUnits"/> and reset the success streak.</item>
///   <item><see cref="FrameOutcome.AcknowledgedAfterRetransmit"/> is mildly
///         penalising (no decrement, success streak resets) but does not
///         increment TXDELAY directly — it could be a collision rather than
///         a preamble miss.</item>
/// </list>
/// <para>
/// This is a deliberately simple hill-climb. The intent is to ship something
/// that does better than a static value on stable links while leaving room
/// for a smarter estimator (e.g. Welford running variance, separate
/// per-mode parameter sets, latency-jitter-aware persistence tuning) later
/// without churning <see cref="IAdaptiveParameterEstimator"/>.
/// </para>
/// </remarks>
public sealed class TxDelayHillClimbEstimator : IAdaptiveParameterEstimator
{
    /// <summary>Floor for the recommended TXDELAY (10 ms units).</summary>
    public byte MinTxDelay { get; init; } = 5;   // 50 ms — below this most TNCs won't lock

    /// <summary>Ceiling for the recommended TXDELAY (10 ms units).</summary>
    public byte MaxTxDelay { get; init; } = 100; // 1 s — beyond this we're wasting airtime

    /// <summary>How many consecutive first-try ACKs trigger a step-down.</summary>
    public int SuccessesPerStepDown { get; init; } = 8;

    /// <summary>How many 10 ms units to walk down per step-down event.</summary>
    public byte StepUnits { get; init; } = 1;

    /// <summary>How many 10 ms units to walk up on a loss / ACK timeout.</summary>
    public byte LossPenaltyUnits { get; init; } = 5;

    private readonly byte initialTxDelay;
    private readonly ConcurrentDictionary<string, PeerState> peerState = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Create an estimator with the given starting TXDELAY (10 ms units).
    /// Defaults to 50 = 500 ms, the KISS spec default.
    /// </summary>
    public TxDelayHillClimbEstimator(byte initialTxDelay = 50)
    {
        if (initialTxDelay == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(initialTxDelay), initialTxDelay, "TXDELAY must be > 0");
        }
        this.initialTxDelay = initialTxDelay;
    }

    /// <inheritdoc/>
    public KissParameters Recommend(string peer)
    {
        ArgumentException.ThrowIfNullOrEmpty(peer);
        var state = peerState.GetOrAdd(peer, _ => new PeerState(initialTxDelay));
        return new KissParameters(state.TxDelay, Persistence: null, SlotTimeTenMsUnits: null, TxTailTenMsUnits: null);
    }

    /// <inheritdoc/>
    public void Observe(FrameOutcomeSample sample)
    {
        ArgumentException.ThrowIfNullOrEmpty(sample.Peer);
        var state = peerState.GetOrAdd(sample.Peer, _ => new PeerState(initialTxDelay));
        lock (state.Gate)
        {
            if (state.SettlingFrames > 0)
            {
                // The NinoTNC applies a changed TXDELAY from the SECOND frame after the KISS
                // parameter command, not the first (bench-measured across every step of a
                // 20/50/100 sweep). The first outcome after a change was transmitted with the
                // OLD value — attributing it to the new one corrupts the climb, so skip it.
                // Cheap insurance on other TNCs too: it only delays the climb by one frame.
                state.SettlingFrames--;
                return;
            }

            switch (sample.Outcome)
            {
                case FrameOutcome.AcknowledgedFirstTry:
                    state.SuccessStreak++;
                    if (state.SuccessStreak >= SuccessesPerStepDown && state.TxDelay > MinTxDelay)
                    {
                        int next = state.TxDelay - StepUnits;
                        state.TxDelay = (byte)Math.Max(MinTxDelay, next);
                        state.SuccessStreak = 0;
                        state.SettlingFrames = 1;
                    }
                    break;
                case FrameOutcome.AcknowledgedAfterRetransmit:
                    // Probabilistic noise — reset streak but don't bump TXDELAY.
                    state.SuccessStreak = 0;
                    break;
                case FrameOutcome.Lost:
                case FrameOutcome.AckModeTimedOut:
                    state.SuccessStreak = 0;
                    int penalised = state.TxDelay + LossPenaltyUnits;
                    state.TxDelay = (byte)Math.Min(MaxTxDelay, penalised);
                    state.SettlingFrames = 1;
                    break;
            }
        }
    }

    /// <summary>
    /// Reveal the current per-peer TXDELAY recommendation, for diagnostics.
    /// </summary>
    public byte? CurrentTxDelayFor(string peer) =>
        peerState.TryGetValue(peer, out var state) ? state.TxDelay : null;

    private sealed class PeerState(byte initial)
    {
        public byte TxDelay = initial;
        public int SuccessStreak;
        public int SettlingFrames;
        public readonly Lock Gate = new();
    }
}
