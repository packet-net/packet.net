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
    /// Node-policy flag — when <c>true</c> (the default), the session
    /// accepts inbound SABM/SABME frames and runs the figc4.1 t14 /
    /// figc4.1 t13 acceptance path. When <c>false</c>, figc4.1's
    /// <c>able_to_establish?</c> decision falls through to the No branch
    /// (t15) which emits DM and stays Disconnected. Per-session because
    /// in deployments with multiple sessions on one modem the policy
    /// genuinely differs per peer — a node that has already accepted
    /// one connection still wants the catalogue's existing default
    /// behaviour for unrelated peer sessions. The cleanest binding is a
    /// context field; callers can still override the
    /// <c>able_to_establish</c> binding for richer policies (callsign
    /// allow-lists, channel load, etc.) — the default just reads this
    /// flag.
    /// </summary>
    public bool AcceptIncoming { get; set; } = true;

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

    /// <summary>
    /// Captures <see cref="ITimerScheduler.TimeRemaining"/> for T1 at
    /// the moment <c>stop_T1</c> last ran. Consumed by figc4.7's
    /// <c>Select_T1_Value</c> SRT IIR formula: the spec uses
    /// <c>SRT := (7/8)*SRT + (T1V - remaining_when_stopped)/8</c>,
    /// where the parenthesised term is the round-trip-time estimate
    /// for this exchange. Zero on a fresh session or after T1 has
    /// expired (no remaining time to sample).
    /// </summary>
    public TimeSpan T1RemainingWhenLastStopped { get; set; }

    // ─── Negotiated link parameters (§6.7.2, XID defaults) ───────────────

    /// <summary>Maximum information field length in octets (N1). Default 256.</summary>
    public int N1 { get; set; } = 256;

    /// <summary>Maximum number of retries (N2). Default 10.</summary>
    public int N2 { get; set; } = 10;

    /// <summary>Maximum outstanding I frames (k). Default 4 (mod-8) / 32 (mod-128).</summary>
    public int K { get; set; } = 4;

    /// <summary>
    /// The window (k) the engine actually enforces for BOTH the send side
    /// (max outstanding I-frames) and the receive side (the in-window acceptance
    /// bound for storing out-of-sequence frames) — <see cref="K"/>, but capped
    /// at <c>Modulus/2</c> while Selective Repeat (<see cref="SrejEnabled"/>) is in
    /// effect, per the Selective-Repeat window-wrap invariant (ax25spec#13). Above
    /// that cap, two in-flight frames could share an N(S) and SREJ recovery can
    /// silently deliver a stale stored frame (packet-net/packet.net#393). Gated by
    /// <see cref="Ax25SessionQuirks.Ax25Spec13ClampSrejWindowToHalfModulus"/>
    /// (default on); with the quirk off it is just <see cref="K"/>, reproducing the
    /// unbounded figure-literal behaviour. Go-back-N links (SREJ off) are never
    /// capped — they tolerate <c>k</c> up to <c>Modulus−1</c>.
    /// </summary>
    public int EffectiveWindow =>
        Quirks.Ax25Spec13ClampSrejWindowToHalfModulus && SrejEnabled
            ? Math.Min(K, Modulus / 2)
            : K;

    /// <summary>True for mod-128 (SABME / extended); false for mod-8 (SABM).</summary>
    public bool IsExtended { get; set; }

    /// <summary>True if SREJ has been negotiated via XID.</summary>
    public bool SrejEnabled { get; set; }

    /// <summary>
    /// True if the segmenter/reassembler has been negotiated via XID (the
    /// HDLC Optional Functions segmenter bit, §4.3.3.7) — a v2.2-only
    /// capability (§1621) enabled only when both peers advertise it. The MDL
    /// negotiation sets this; the DL-DATA segment/reassemble path (arc V4)
    /// gates on it. Forced off on the version-2.0 fallback.
    /// </summary>
    public bool SegmenterReassemblerEnabled { get; set; }

    /// <summary>True for half-duplex operation. Set by figc4.7's <c>Set_Version_2_0</c> / <c>Set_Version_2_2</c> subroutines.</summary>
    public bool HalfDuplex { get; set; } = true;

    /// <summary>
    /// True if implicit reject (mod-8 v2.0) is the selected reject scheme;
    /// false means selective reject (v2.2). Mirrors the v2.0 / v2.2 selection
    /// per figc4.7's <c>Set_Version_2_0</c> / <c>Set_Version_2_2</c>.
    /// </summary>
    public bool ImplicitReject { get; set; } = true;

    /// <summary>
    /// Acknowledgement-delay timer T2 duration (§6.7.1.2). Default 3 s per
    /// AX.25 v2.2. Set by figc4.7's <c>Set_Version_2_0</c> /
    /// <c>Set_Version_2_2</c> and seeded per port at session build. The
    /// production construction sites (<see cref="Ax25Listener"/>,
    /// <see cref="Ax25Adapter"/>) read this to defer the LM-SEIZE grant so
    /// received in-sequence I-frames coalesce into one cumulative RR
    /// (#385); <see cref="TimeSpan.Zero"/> disables the delay
    /// (ack-per-frame, as the SDL figures draw).
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

    /// <summary>
    /// Named deviations from the SDL figures where a figure is a confirmed
    /// upstream spec defect (see <see cref="Ax25SessionQuirks"/>). Defaults to
    /// the spec-correct behaviour (<see cref="Ax25SessionQuirks.Default"/>); set
    /// <see cref="Ax25SessionQuirks.StrictlyFaithful"/> to run the figures
    /// exactly as drawn for conformance testing.
    /// </summary>
    public Ax25SessionQuirks Quirks { get; set; } = Ax25SessionQuirks.Default;

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

    /// <summary>
    /// N(S) values that have already been selectively retransmitted (in response
    /// to an SREJ) since V(a) last advanced — i.e. within the current recovery
    /// cycle. A burst of redundant SREJs for the same still-outstanding gap (the
    /// figc4.4 over-SREJ: one SREJ per out-of-sequence frame) must not spawn one
    /// wire copy each — the surplus copies become stale once the receiver's V(R)
    /// wraps past them and get mis-delivered as new (the mod-8 SREJ ring-wrap
    /// duplicate). Cleared on every V(a) advance (genuine progress = new cycle, see
    /// <see cref="PruneAcknowledgedSentIFrames"/>) and per-N(S) when a fresh I-frame
    /// is emitted at that N(S). A genuinely lost retransmit is still recovered — via
    /// the T1/TimerRecovery <c>Invoke_Retransmission</c> path, which does not consult
    /// this set. direwolf reaches the same effect by deleting acknowledged
    /// <c>txdata_by_ns[ns]</c> + de-duplicating SREJ requests (ax25_link.c).
    /// </summary>
    public HashSet<byte> SelectivelyRetransmittedSinceAck { get; } = new();

    // ─── Helpers ────────────────────────────────────────────────────────

    /// <summary>Modulus used for sequence-variable arithmetic (8 or 128).</summary>
    public byte Modulus => IsExtended ? (byte)128 : (byte)8;

    /// <summary>Increment a sequence variable, wrapping at <see cref="Modulus"/>.</summary>
    public byte IncrementSeq(byte value) => (byte)((value + 1) % Modulus);

    /// <summary>Decrement a sequence variable, wrapping at <see cref="Modulus"/>.</summary>
    public byte DecrementSeq(byte value) => (byte)((value + Modulus - 1) % Modulus);

    /// <summary>
    /// True if <paramref name="ns"/> is an <em>outstanding</em> (sent-but-not-yet-
    /// acknowledged) send sequence number — i.e. it lies in the half-open window
    /// <c>[V(a), V(s))</c> in mod-<see cref="Modulus"/> arithmetic. A frame whose
    /// N(S) is outside this window has already been acknowledged (behind V(a)) or
    /// was never sent (at/after V(s)); replaying it during recovery would put a
    /// stale sequence number on the wire that the peer can mis-deliver once its
    /// V(R) has wrapped past it (the mod-8 SREJ ring-wrap duplicate, #231-class).
    /// </summary>
    public bool IsOutstanding(byte ns)
    {
        int span   = (VS - VA + Modulus) % Modulus;   // count of outstanding frames
        int offset = (ns - VA + Modulus) % Modulus;   // position of ns within the window
        return offset < span;
    }

    /// <summary>
    /// Drop every entry in <see cref="SentIFrames"/> whose N(S) is no longer
    /// outstanding (i.e. has been acknowledged — it now lies behind V(a) per
    /// <see cref="IsOutstanding"/>). Called whenever V(a) advances so a stale or
    /// duplicate REJ/SREJ cannot make the recovery path replay an already-acked
    /// frame. Mirrors direwolf's <c>cdata_delete(txdata_by_ns[...])</c> on
    /// acknowledgement (ax25_link.c).
    /// </summary>
    public void PruneAcknowledgedSentIFrames()
    {
        if (SentIFrames.Count == 0) return;
        List<byte>? toRemove = null;
        foreach (var ns in SentIFrames.Keys)
        {
            if (!IsOutstanding(ns))
            {
                (toRemove ??= new List<byte>()).Add(ns);
            }
        }
        if (toRemove is null) return;
        foreach (var ns in toRemove) SentIFrames.Remove(ns);
    }

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
        T1RemainingWhenLastStopped = TimeSpan.Zero;
        IFrameQueue.Clear();
        SentIFrames.Clear();
        StoredReceivedIFrames.Clear();
        SelectivelyRetransmittedSinceAck.Clear();
    }
}
