using Packet.Ax25;

namespace Packet.Ax25.Session;

/// <summary>
/// AX.25 v2.2 §2.4 / §6.6 segmentation-reassembly shim that sits at the
/// data-link primitive boundary — between Layer 3 (the upper layer) and an
/// <see cref="Ax25Session"/>. The data-link state machine and session are
/// <b>unchanged</b>: segments travel as ordinary I-frames carrying PID
/// <see cref="Ax25Frame.PidSegmented"/> (0x08), so the FSM just sends and
/// receives them. This layer is the §6.6 "the segmenter passes all other
/// signals unchanged" boundary process.
/// </summary>
/// <remarks>
/// <para>
/// One instance per data-link session — it owns the per-session
/// <see cref="Reassembler"/> (which holds in-flight multi-segment state).
/// The spec models exactly this placement (§2557 / §2560): the reassembler
/// examines the DL-DATA / DL-UNIT-DATA <em>indication</em>; a 0x08 PID
/// means reassemble, anything else passes through transparently. The
/// segmenter examines the DL-DATA / DL-UNIT-DATA <em>request</em>;
/// over-N1 means segment, otherwise pass through.
/// </para>
/// <para>
/// <b>Gating.</b> Segmentation is a v2.2, negotiated capability (§1621 —
/// "only enabled if both stations on the link are using AX.25 version 2.2 or
/// higher", set via the XID HDLC-Optional-Functions segmenter bit). This
/// layer gates the send side on
/// <see cref="Ax25SessionContext.SegmenterReassemblerEnabled"/>. If a payload
/// exceeds N1−1 (the max segment-free info-field size) and the segmenter is
/// <em>not</em> enabled, <see cref="BuildSendRequests"/> throws — the request
/// is rejected cleanly rather than silently truncated or sent as an
/// oversize frame.
/// </para>
/// <para>
/// <b>Inner PID on reassembly — gated by
/// <see cref="Ax25SessionQuirks.SegmentFirstCarriesL3Pid"/> (default on).</b>
/// Figure 6.2 defines the segment header as the 0x08 PID octet plus one F/X
/// octet — there is <b>no field carrying the original Layer-3 PID</b> through a
/// segmented series. Dire Wolf, the only known v2.2 segmenter, prepends the
/// original PID as an extra octet on the first segment so its reassembler can
/// recover it (the §6.6 "two-octet header" prose admits this reading). This shim
/// matches Dire Wolf by <b>default</b>:
/// <list type="bullet">
/// <item><b>Quirk on (default):</b> the first segment carries the inner-PID octet
/// (<see cref="Segmenter"/> writes it on send; the <see cref="Reassembler"/> reads
/// it on receive). A reassembled payload is delivered with that <b>original L3
/// PID</b> — so segmentation no longer loses it.</item>
/// <item><b>Quirk off (<see cref="Ax25SessionQuirks.StrictlyFaithful"/>):</b> the
/// figure-literal format — no inner-PID octet, and a reassembled payload is
/// delivered as <see cref="Ax25Frame.PidNoLayer3"/> (0xF0), the faithful "PID
/// unknown / raw" value (<see cref="FigureLiteralReassembledPid"/>).</item>
/// </list>
/// </para>
/// </remarks>
public sealed class SegmentationLayer
{
    private readonly Ax25SessionContext context;
    private Reassembler? reassembler;

    /// <summary>
    /// PID delivered with a reassembled payload under the <i>figure-literal</i>
    /// format (the <see cref="Ax25SessionQuirks.SegmentFirstCarriesL3Pid"/> quirk
    /// off). Per §6.6 / Figure 6.2 the segment header carries no inner Layer-3 PID,
    /// so figure-literal reassembled data is delivered as
    /// <see cref="Ax25Frame.PidNoLayer3"/> (0xF0). With the quirk on (default) the
    /// inner-PID octet recovers the original L3 PID instead.
    /// </summary>
    public const byte FigureLiteralReassembledPid = Ax25Frame.PidNoLayer3;

    /// <summary>Construct a shim over the supplied session context.</summary>
    /// <param name="context">The session's context — read for the negotiated
    /// segmenter-enabled flag, N1, and the segmentation-format quirk. The quirk is
    /// read <i>lazily</i> (at first send/receive), because callers such as
    /// <see cref="Ax25Listener"/> construct the shim before their
    /// <c>ConfigureSession</c> hook has set <see cref="Ax25SessionContext.Quirks"/>.</param>
    public SegmentationLayer(Ax25SessionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        this.context = context;
    }

    /// <summary>Whether the Dire-Wolf first-segment inner-PID format is in effect
    /// for this session — read live from the context's quirks (default on).</summary>
    private bool InnerPidFormat => context.Quirks.SegmentFirstCarriesL3Pid;

    /// <summary>
    /// Send-side shim. Given an upper-layer payload + its Layer-3 PID, return
    /// the sequence of <see cref="DlDataRequest"/>s to post to the session:
    /// <list type="bullet">
    /// <item>If the segmenter is enabled and the payload exceeds N1−1, one
    /// <see cref="DlDataRequest"/> per segment, each carrying PID
    /// <see cref="Ax25Frame.PidSegmented"/> (0x08); the session enqueues +
    /// sends each as a normal I-frame.</item>
    /// <item>Otherwise a single <see cref="DlDataRequest"/> with the original
    /// payload + PID, unchanged.</item>
    /// </list>
    /// </summary>
    /// <param name="data">The upper-layer payload.</param>
    /// <param name="pid">The Layer-3 PID for the (un-segmented) request.</param>
    /// <exception cref="InvalidOperationException">If the payload exceeds N1−1
    /// and the segmenter has not been negotiated (v2.0 / not enabled) — the
    /// request can't be honoured without violating N1, so it's rejected
    /// cleanly.</exception>
    public IReadOnlyList<DlDataRequest> BuildSendRequests(ReadOnlyMemory<byte> data, byte pid = Ax25Frame.PidNoLayer3)
    {
        // N1 is the max info-field octet count. An un-segmented info field is
        // the whole payload (one PID, no segment-control byte), so the
        // pass-through ceiling is N1 itself. A *segment's* info field is the
        // F/X control byte + payload, so per-segment payload is N1−1.
        bool fits = data.Length <= context.N1;

        if (fits)
        {
            return new[] { new DlDataRequest(data, pid) };
        }

        if (!context.SegmenterReassemblerEnabled)
        {
            throw new InvalidOperationException(
                $"payload of {data.Length} bytes exceeds N1={context.N1} and the segmenter/reassembler " +
                "has not been negotiated (AX.25 v2.2 §6.6 — segmentation requires both peers to advertise " +
                "the XID HDLC-Optional-Functions segmenter bit). Cannot send without segmenting; rejecting " +
                "the request rather than truncating or producing an oversize frame.");
        }

        // Segment into PID-0x08 info fields and post each as its own I-frame
        // request. With the inner-PID quirk on (default), the first segment also
        // carries the original L3 PID after the F/X byte (Dire Wolf's format) so
        // the receiver can recover it; with the quirk off (StrictlyFaithful) the
        // figure-literal format is emitted (no inner PID).
        var segments = Segmenter.Segment(data.Span, context.N1, InnerPidFormat ? pid : (byte?)null);
        var requests = new DlDataRequest[segments.Count];
        for (int i = 0; i < segments.Count; i++)
        {
            requests[i] = new DlDataRequest(segments[i], Ax25Frame.PidSegmented);
        }
        return requests;
    }

    /// <summary>
    /// Receive-side shim. Given a <see cref="DataLinkDataIndication"/> the
    /// session raised, either:
    /// <list type="bullet">
    /// <item>If its PID is <see cref="Ax25Frame.PidSegmented"/> (0x08), feed
    /// the info field to the per-session <see cref="Reassembler"/> and return
    /// the completed payload as a single reassembled
    /// <see cref="DataLinkDataIndication"/> on the last segment, or
    /// <c>null</c> while more segments are expected (nothing to deliver yet).</item>
    /// <item>Otherwise return the indication unchanged (pass-through).</item>
    /// </list>
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Malformed / protocol-violating segments are dropped cleanly at this
    /// seam.</b> The wire is untrusted: a hostile peer or RF corruption can deliver
    /// a PID-0x08 indication that is empty, a non-First segment with no prior First,
    /// an inner-PID First missing its PID octet, or an out-of-sequence continuation.
    /// <see cref="Reassembler.Push"/> rejects each of these by throwing
    /// (its strict, documented contract — see <see cref="Reassembler"/>). This
    /// boundary process is the right place to turn that strict contract into a
    /// graceful drop: it catches the documented
    /// <see cref="ArgumentException"/> / <see cref="InvalidOperationException"/>,
    /// <b>resets any in-progress reassembly</b> (so a corrupt series can't poison
    /// the next valid one), and returns <c>null</c> — the same "nothing to deliver
    /// yet" signal as a legitimate mid-series segment. Nothing propagates to the
    /// caller and nothing relies on <see cref="Ax25Listener"/>'s inbound catch-all.
    /// This matches the §6.6 / Fig C5.2 reassembler treating a bad segment as a
    /// discardable error, and Dire Wolf (the only known v2.2 segmenter), whose
    /// reassembler logs a "Reassembler Protocol Error" and drops.
    /// </para>
    /// <para>
    /// The low-level <see cref="Reassembler.Push"/> contract is deliberately
    /// <i>unchanged</i> — direct callers still get the strict throw; only this
    /// wire-facing seam softens it to a drop.
    /// </para>
    /// </remarks>
    /// <param name="indication">The indication the session raised.</param>
    /// <returns>The indication to deliver upward, or <c>null</c> when a
    /// segment was consumed but the series is incomplete — or when a malformed
    /// segment was dropped (and the reassembler reset).</returns>
    public DataLinkDataIndication? OnDataIndication(DataLinkDataIndication indication)
    {
        ArgumentNullException.ThrowIfNull(indication);

        if (indication.Pid != Ax25Frame.PidSegmented)
        {
            return indication;   // not a segment — pass through transparently
        }

        // Construct the per-session reassembler lazily on first use, reading the
        // segmentation-format quirk live (the context's Quirks may have been set by
        // a ConfigureSession hook that ran after this shim was constructed).
        reassembler ??= new Reassembler(expectInnerPid: InnerPidFormat);

        byte[]? completed;
        try
        {
            completed = reassembler.Push(indication.Info.Span);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            // Malformed / protocol-violating segment off the wire. Drop it cleanly
            // and discard any partially-accumulated series so a corrupt run can't
            // poison the next valid one — the dropped reassembler is replaced lazily
            // on the next segment, back in the "waiting for a First" state. We
            // swallow *only* Push's two documented contract exceptions; any other
            // (crash-class) exception would be a genuine bug and is left to surface.
            reassembler = null;
            return null;
        }

        if (completed is null) return null;   // mid-series segment — nothing to deliver yet

        // With the inner-PID quirk on, the reassembler recovered the original L3
        // PID off the first segment — deliver with it. With the quirk off
        // (figure-literal) there is no inner PID, so deliver as PidNoLayer3.
        var pid = reassembler.LastRecoveredPid ?? FigureLiteralReassembledPid;
        return new DataLinkDataIndication(completed, pid);
    }
}
