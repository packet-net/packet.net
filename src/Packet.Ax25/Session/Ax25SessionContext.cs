using Packet.Core;

namespace Packet.Ax25.Session;

/// <summary>
/// Mutable per-session state for one AX.25 data-link connection. Holds the
/// sequence variables, flags, queues, and link-parameter values that the
/// generated SDL transitions read and update.
/// </summary>
/// <remarks>
/// <para>
/// One context per <c>(local, remote, port)</c> session. Field names
/// match the spec's variable names verbatim so the dispatcher's
/// action-string switch can map cleanly. See AX.25 v2.2 §4.2.2 (sequence
/// numbers) and §C4.3 (flags + variables).
/// </para>
/// <para>
/// V(S), V(A), and V(R) are <see cref="byte"/>s because they hold mod-128
/// values; mod-8 use only the low 3 bits and is just <see cref="IsExtended"/>
/// = false. The dispatcher applies the right modulus when comparing /
/// incrementing — the field stores the underlying 0-127 value.
/// </para>
/// </remarks>
public sealed class Ax25SessionContext
{
    /// <summary>Our station identity for this session.</summary>
    public required Callsign Local { get; init; }

    /// <summary>Remote station identity for this session.</summary>
    public required Callsign Remote { get; init; }

    /// <summary>Digipeater path. Empty for direct links.</summary>
    public IReadOnlyList<Callsign> Digipeaters { get; init; } = Array.Empty<Callsign>();

    // ─── Sequence variables (§4.2.2) ────────────────────────────────────

    /// <summary>Send state variable — sequence number of the next I-frame to send.</summary>
    public byte VS { get; set; }

    /// <summary>Acknowledge state variable — last acknowledged sent I-frame.</summary>
    public byte VA { get; set; }

    /// <summary>Receive state variable — sequence number of next I-frame expected to receive.</summary>
    public byte VR { get; set; }

    /// <summary>Retry counter — how many retransmissions of the current outstanding poll.</summary>
    public int RC { get; set; }

    // ─── Flags (§C4.3) ──────────────────────────────────────────────────

    /// <summary>Layer 3 is busy and cannot receive I frames.</summary>
    public bool OwnReceiverBusy { get; set; }

    /// <summary>Remote station is busy and cannot receive I frames.</summary>
    public bool PeerReceiverBusy { get; set; }

    /// <summary>I frames have been successfully received but not yet acknowledged.</summary>
    public bool AcknowledgePending { get; set; }

    /// <summary>A REJ frame has been sent to the remote station (mod-8 implicit reject).</summary>
    public bool RejectException { get; set; }

    /// <summary>An SREJ frame has been sent to the remote station.</summary>
    public bool SelectiveRejectException { get; set; }

    /// <summary>SABM(E) was sent by request of Layer 3 (DL-CONNECT request).</summary>
    public bool Layer3Initiated { get; set; }

    // ─── Negotiated link parameters (§6.7.2, XID defaults) ───────────────

    /// <summary>Maximum information field length in octets (N1). Default 256.</summary>
    public int N1 { get; set; } = 256;

    /// <summary>Maximum number of retries (N2). Default 10.</summary>
    public int N2 { get; set; } = 10;

    /// <summary>Maximum outstanding I frames (k). Default 4 (mod-8) / 32 (mod-128).</summary>
    public int K { get; set; } = 4;

    /// <summary>True for mod-128 (SABME / extended); false for mod-8 (SABM).</summary>
    public bool IsExtended { get; set; }

    /// <summary>True if SREJ has been negotiated via XID.</summary>
    public bool SrejEnabled { get; set; }

    // ─── Queues ─────────────────────────────────────────────────────────

    /// <summary>FIFO queue of I-frame payloads awaiting transmission.</summary>
    public Queue<ReadOnlyMemory<byte>> IFrameQueue { get; } = new();

    /// <summary>Map of seqno → I-frame body, for retransmission.</summary>
    public Dictionary<byte, ReadOnlyMemory<byte>> SentIFrames { get; } = new();

    // ─── Helpers ────────────────────────────────────────────────────────

    /// <summary>Modulus used for sequence-variable arithmetic (8 or 128).</summary>
    public byte Modulus => IsExtended ? (byte)128 : (byte)8;

    /// <summary>Increment a sequence variable, wrapping at <see cref="Modulus"/>.</summary>
    public byte IncrementSeq(byte value) => (byte)((value + 1) % Modulus);

    /// <summary>Reset all session state to "freshly connected" defaults.</summary>
    public void ResetState()
    {
        VS = VA = VR = 0;
        RC = 0;
        OwnReceiverBusy = false;
        PeerReceiverBusy = false;
        AcknowledgePending = false;
        RejectException = false;
        SelectiveRejectException = false;
        Layer3Initiated = false;
        IFrameQueue.Clear();
        SentIFrames.Clear();
    }
}
