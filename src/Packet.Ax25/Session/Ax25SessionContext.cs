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

    /// <summary>
    /// Count of outstanding SREJ exceptions per §C4.3. The figc4.4 SREJ
    /// paths increment this when sending an SREJ and decrement when an
    /// expected out-of-sequence I-frame is delivered. Distinct from the
    /// <see cref="SelectiveRejectException"/> flag (which is just a
    /// "have any SREJs been sent" boolean).
    /// </summary>
    public int SrejExceptionCount { get; set; }

    /// <summary>SABM(E) was sent by request of Layer 3 (DL-CONNECT request).</summary>
    public bool Layer3Initiated { get; set; }

    /// <summary>
    /// Scratch register used by figc4.7's <c>Invoke_Retransmission</c>:
    /// stashes V(s) at routine entry so the loop knows when it has caught
    /// up. Only meaningful during a single Invoke_Retransmission invocation.
    /// </summary>
    public byte? X { get; set; }

    /// <summary>
    /// Set by the T1 timer-expiry handler; consumed and cleared by
    /// figc4.7's <c>Select_T1_Value</c> subroutine when it picks between
    /// the IIR-smoothed and the linear-backoff branches. Distinct from
    /// "T1 currently running" (<see cref="ITimerScheduler.IsRunning"/>):
    /// this flag records "T1 fired at least once since the last
    /// Select_T1_Value call".
    /// </summary>
    public bool T1HadExpired { get; set; }

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

    /// <summary>True for half-duplex operation. Set by figc4.7's <c>Set_Version_2_0</c> / <c>Set_Version_2_2</c> subroutines.</summary>
    public bool HalfDuplex { get; set; } = true;

    /// <summary>
    /// True if implicit reject (mod-8 v2.0) is the selected reject scheme;
    /// false means selective reject (v2.2). Mirrors the v2.0 / v2.2 selection
    /// per figc4.7's <c>Set_Version_2_0</c> / <c>Set_Version_2_2</c>.
    /// </summary>
    public bool ImplicitReject { get; set; } = true;

    /// <summary>
    /// Acknowledgement-timer T2 duration. Default 3 s per AX.25 v2.2.
    /// Set by figc4.7's <c>Set_Version_2_0</c> / <c>Set_Version_2_2</c>.
    /// </summary>
    public TimeSpan T2 { get; set; } = TimeSpan.FromMilliseconds(3000);

    /// <summary>
    /// Smoothed Round-Trip Time per §6.7.1.2. Updated as I-frames are
    /// sent and acknowledged; used to derive <see cref="T1V"/>. Default
    /// 3 seconds matches the spec's "Initial Default" value.
    /// </summary>
    public TimeSpan Srt { get; set; } = TimeSpan.FromMilliseconds(3000);

    /// <summary>
    /// T1 timeout value per §6.7.1.3 — the actual duration the
    /// acknowledgement timer is armed for. Recomputed as 2 × SRT
    /// after each round-trip; figc4.1 t03 / figc4.2 t21 / figc4.4 etc.
    /// initialise this via <c>T1V := 2 * SRT</c> on (re)connection.
    /// Default 6 seconds = 2 × initial SRT.
    /// </summary>
    public TimeSpan T1V { get; set; } = TimeSpan.FromMilliseconds(6000);

    // ─── Queues ─────────────────────────────────────────────────────────

    /// <summary>
    /// FIFO queue of I-frame payloads awaiting transmission. Each entry
    /// carries the Layer-3 payload + PID byte; the session pops one
    /// entry per <see cref="IFramePopsOffQueue"/> event when conditions
    /// allow transmission.
    /// </summary>
    public Queue<(ReadOnlyMemory<byte> Data, byte Pid)> IFrameQueue { get; } = new();

    /// <summary>
    /// Map of N(S) → I-frame payload + PID for retransmission of
    /// previously-sent outbound frames. Populated when an I-frame is
    /// emitted; consumed by figc4.4's
    /// <c>push_old_I_frame_N_r_on_queue</c> verb during REJ/SREJ recovery.
    /// </summary>
    public Dictionary<byte, (ReadOnlyMemory<byte> Data, byte Pid)> SentIFrames { get; } = new();

    /// <summary>
    /// Out-of-sequence received I-frames awaiting their turn — keyed by
    /// the frame's N(S). When <see cref="VR"/> advances to a stored
    /// seqno, the figc4.4 <c>retrieve_stored_V_r_I_frame</c> action
    /// dequeues from here and delivers upward.
    /// </summary>
    public Dictionary<byte, (ReadOnlyMemory<byte> Info, byte Pid)> StoredReceivedIFrames { get; } = new();

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
        SrejExceptionCount = 0;
        Layer3Initiated = false;
        X = null;
        T1HadExpired = false;
        IFrameQueue.Clear();
        SentIFrames.Clear();
        StoredReceivedIFrames.Clear();
    }
}
