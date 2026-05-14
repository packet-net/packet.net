namespace Packet.Kiss.Adaptive;

/// <summary>
/// What happened to an outbound frame from the host's point of view. The
/// adaptive estimator uses the outcome distribution to decide whether the
/// current parameter set is too conservative (lots of acknowledged-on-first
/// attempts → can cut TXDELAY) or too aggressive (acknowledged-late or
/// not-at-all → push TXDELAY up).
/// </summary>
public enum FrameOutcome
{
    /// <summary>The TNC sent it and the peer (or the wire) confirmed receipt
    /// on the first attempt. Best case — parameters could be tightened.</summary>
    AcknowledgedFirstTry,

    /// <summary>The frame eventually got through but only after one or more
    /// retransmits. Mid case — the current parameters are correct or only
    /// slightly conservative; we don't get strong signal either way.</summary>
    AcknowledgedAfterRetransmit,

    /// <summary>The frame was lost or the AX.25 layer gave up retrying after
    /// N2 attempts. Worst case — the parameters may be too aggressive.</summary>
    Lost,

    /// <summary>The TNC accepted the frame but never sent the ACKMODE
    /// echo within the timeout. Indicates an unexpectedly long key-up,
    /// a stuck TX, or a missed echo — treat as a soft "Lost" signal.</summary>
    AckModeTimedOut,
}

/// <summary>
/// One observation of a frame outcome to a specific peer. Estimators consume
/// streams of these and refine their per-peer parameter recommendations.
/// </summary>
/// <param name="Peer">The peer callsign (with SSID).</param>
/// <param name="Outcome">What happened.</param>
/// <param name="PayloadBytes">Size of the AX.25 frame body (no FCS, no KISS).</param>
/// <param name="ParametersUsed">
/// The KISS parameters that were in effect when this frame went out — the
/// estimator correlates outcomes against what was actually configured.
/// </param>
/// <param name="AckElapsed">
/// Round-trip time from queueing to TX-completion echo, when known. Only
/// meaningful when ACKMODE is in use.
/// </param>
/// <param name="ObservedAtUtc">When the outcome was logged.</param>
public readonly record struct FrameOutcomeSample(
    string Peer,
    FrameOutcome Outcome,
    int PayloadBytes,
    KissParameters ParametersUsed,
    TimeSpan? AckElapsed,
    DateTimeOffset ObservedAtUtc);
