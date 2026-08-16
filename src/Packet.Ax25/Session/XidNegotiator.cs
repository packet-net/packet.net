using Packet.Ax25.Xid;

namespace Packet.Ax25.Session;

/// <summary>
/// The substantive XID parameter-negotiation logic of the management data-link
/// (MDL, figc5.2): the §6.3.2 "reverts-to" merge that turns our offered
/// parameters and the peer's XID response into the agreed link parameters, and
/// the §6.3.2 ¶1 / §1436 version-2.0 default set used when a pre-v2.2 peer
/// rejects the XID command with a FRMR.
/// </summary>
/// <remarks>
/// <para>
/// Pulled out of the MDL driver into a pure static merge so the per-parameter
/// rules are unit-testable in isolation and carry their spec citations inline.
/// The MDL figc5.2 "Apply Negotiated Parameters" box is a single placeholder
/// verb in the prose-bootstrap SDL (the figc5.3–figc5.8 per-parameter
/// subroutines were not transcribed); this is its runtime body.
/// </para>
/// <para>
/// "Offered" = the parameters we put in our XID <em>command</em> (our Rx
/// capability / preference). "Response" = the parameters the peer returned in
/// its XID <em>response</em>. Per §6.3.2 ¶7 "Both TNCs set up based on the
/// values used in the XID response" — but the spec's per-parameter rules
/// (lesser / greater / min) are deterministic functions of the two offers, so
/// we re-derive the agreed value here rather than trusting the peer to have
/// applied the rule correctly. That keeps both stations convergent even if a
/// peer echoes its own offer verbatim.
/// </para>
/// </remarks>
public static class XidNegotiator
{
    /// <summary>
    /// Apply the §6.3.2 reverts-to merge of <paramref name="offered"/> (what we
    /// sent in our XID command) and <paramref name="response"/> (what the peer
    /// returned in its XID response) to <paramref name="context"/>, replacing the
    /// forced establishment defaults with the negotiated values. Each parameter
    /// absent from <em>both</em> offers retains the context's current value
    /// (§4.3.3.7 ¶1024 / §6.3.2 "if this field is not present, the current
    /// values are retained").
    /// </summary>
    /// <param name="context">The data-link session state to update in place.</param>
    /// <param name="offered">Our XID-command parameter set.</param>
    /// <param name="response">The peer's parsed XID-response parameter set.</param>
    public static void ApplyNegotiated(
        Ax25SessionContext context, XidParameters offered, XidParameters response)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(offered);
        ArgumentNullException.ThrowIfNull(response);

        // ─── HDLC Optional Functions (PI=3): reject scheme + modulo ──────────
        //
        // §6.3.2 ¶1426: "Function reverts to the lesser of the selection offered
        // in the XID command and XID response frames. Ordering is (highest to
        // lowest): selective reject and implicit reject; Modulo 128 and modulo
        // 8." So the agreed value is the LOWER of the two on each axis:
        //   reject:  SREJ (higher) vs REJ (lower)      → REJ wins if either side offers REJ
        //   modulo:  128 (higher)  vs 8   (lower)      → mod-8 wins if either side offers mod-8
        // If PI=3 is absent from both, §6.3.2 ¶1426 selects the default
        // (selective reject, modulo 128) — represented by HdlcOptionalFunctions.Default.
        var ourHdlc = offered.HdlcOptionalFunctions ?? HdlcOptionalFunctions.Default;
        var theirHdlc = response.HdlcOptionalFunctions ?? HdlcOptionalFunctions.Default;

        // "lesser of the selection": SREJ only survives if BOTH sides offer it.
        bool agreedSelectiveReject =
            ourHdlc.Reject == RejectMode.SelectiveReject &&
            theirHdlc.Reject == RejectMode.SelectiveReject;

        // "lesser of the selection": mod-128 only survives if BOTH sides offer it.
        bool agreedModulo128 = ourHdlc.Modulo128 && theirHdlc.Modulo128;

        // Segmenter/reassembler (the §1621 v2.2 capability) is a mutual
        // capability bit — enabled only if both sides advertise it. Not part of
        // the explicit reverts-to prose, but the §6.3.2 ¶1419 "enables the use of
        // the segmenter/reassembler" framing is a mutual-capability AND.
        bool agreedSegmenter = ourHdlc.SegmenterReassembler && theirHdlc.SegmenterReassembler;

        context.SrejEnabled = agreedSelectiveReject;
        context.ImplicitReject = !agreedSelectiveReject;
        context.IsExtended = agreedModulo128;
        context.SegmenterReassemblerEnabled = agreedSegmenter;

        // ─── Classes of Procedures (PI=2): duplex ────────────────────────────
        //
        // §6.3.2 ¶1424: "reverts to half-duplex if either TNC cannot support
        // full-duplex." Full-duplex survives only if BOTH sides offer it; absent
        // from both → default half-duplex.
        var ourCop = offered.ClassesOfProcedures ?? ClassesOfProcedures.HalfDuplexDefault;
        var theirCop = response.ClassesOfProcedures ?? ClassesOfProcedures.HalfDuplexDefault;
        bool agreedFullDuplex = !ourCop.HalfDuplex && !theirCop.HalfDuplex;
        context.HalfDuplex = !agreedFullDuplex;

        // ─── Window Size Receive k (PI=8): notification / min ────────────────
        //
        // §6.3.2 ¶1430 / §4.3.3.7 ¶1094: k is a NOTIFICATION of the receiver's
        // buffering capacity ("the maximum size of the window it will handle
        // without error. A transmitting TNC may not exceed this size"). So OUR
        // send window is bounded by the PEER's advertised Rx capacity (their
        // response's k); take the min of the two so neither side overruns the
        // other's buffer. If neither side advertised k, leave the context's
        // current value (which Set_Version_2_x seeded to 4/32 by modulo).
        // Bounded by the sequence space as well as by the peer: at most Modulus-1
        // I-frames can be outstanding (§4.2.4 / §6.4.4.1 - the window is measured
        // as (V(S) - V(A)) mod Modulus, which never reaches Modulus), so a peer
        // advertising k >= Modulus (e.g. 32 on a mod-8 link) is taken at the bound,
        // not verbatim. Ax25SessionContext.EffectiveWindow enforces the same bound
        // live; this keeps the stored, operator-visible k honest too.
        int? agreedK = MinPresent(offered.WindowSizeRx, response.WindowSizeRx);
        if (agreedK is { } k)
        {
            context.K = Math.Min(k, context.Modulus - 1);
        }

        // ─── I-Field Length Receive N1 (PI=6): notification / min ────────────
        //
        // §6.3.2 ¶1428 / §4.3.3.7 ¶1090: N1 is likewise a notification ("the
        // maximum size of an Information field it will handle without error. A
        // transmitting TNC may not exceed this size"). Our outbound frames must
        // not exceed the peer's advertised Rx N1; take the min. Stored in octets
        // on the context (XidParameters exposes the bits→octets bridge).
        int? agreedN1 = MinPresent(offered.IFieldLengthRxOctets, response.IFieldLengthRxOctets);
        if (agreedN1 is { } n1)
        {
            context.N1 = n1;
        }

        // ─── Acknowledge Timer T1 (PI=9): greater ────────────────────────────
        //
        // §6.3.2 ¶1432: "Function reverts to the greater of the values offered in
        // the XID command and XID response frames." A longer T1 is the safe
        // (more patient) choice on a slow/lossy link, so both sides adopt the max.
        // T1V is the operating timeout; we also re-seed SRT so T1V := 2*SRT
        // recomputations stay consistent with the negotiated value.
        int? agreedT1 = MaxPresent(offered.AckTimerMillis, response.AckTimerMillis);
        if (agreedT1 is { } t1ms)
        {
            context.T1V = TimeSpan.FromMilliseconds(t1ms);
            context.Srt = TimeSpan.FromMilliseconds(t1ms / 2.0);
        }

        // ─── Retries N2 (PI=10): greater ─────────────────────────────────────
        //
        // §6.3.2 ¶1434: "reverts to the greater of the values offered." (The
        // prose labels it "N1" but the §4.3.3.7 PI=10 table and the surrounding
        // text make clear this is the retry count N2.) More retries is the safer
        // choice, so both sides adopt the max.
        int? agreedN2 = MaxPresent(offered.Retries, response.Retries);
        if (agreedN2 is { } n2)
        {
            context.N2 = n2;
        }
    }

    /// <summary>
    /// Install the complete AX.25 version-2.0 default parameter set per §6.3.2
    /// ¶1 / §1436 — used when a pre-v2.2 peer FRMRs our XID command (figc5.2
    /// FRMR path) and "a version 2.0 connection is made." This is the FULL set,
    /// not merely <c>IsExtended = false</c>:
    /// <list type="bullet">
    /// <item>Set Half Duplex</item>
    /// <item>Set Implicit Reject (SREJ off)</item>
    /// <item>Modulo = 8 (mod-8, not extended)</item>
    /// <item>I Field Length Receive N1 = 2048 bits = 256 octets</item>
    /// <item>Window Size Receive k = 7</item>
    /// <item>Acknowledge Timer T1 = 3000 ms</item>
    /// <item>Retries N2 = 10</item>
    /// </list>
    /// </summary>
    /// <remarks>
    /// The existing data-link <c>set_version_2_0</c> dispatcher verb only clears
    /// <see cref="Ax25SessionContext.IsExtended"/> (the data-link figc4.6 fallback
    /// path runs its remaining v2.0 verbs separately). The MDL figc5.2 FRMR
    /// transition draws a single <c>Set Version 2.0</c> box, so the MDL owes the
    /// complete set here. §1436's k=7 is the version-2.0 default; note it is NOT
    /// the mod-8 XID default (k=4, §4.3.3.7 ¶1094) — the v2.0 fallback explicitly
    /// uses 7. The segmenter/reassembler is a v2.2-only capability (§1621) so it
    /// is disabled on the v2.0 fallback.
    /// </remarks>
    public static void ApplyVersion20Defaults(Ax25SessionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        context.HalfDuplex = true;                       // Set Half Duplex
        context.ImplicitReject = true;                       // Set Implicit Reject
        context.SrejEnabled = false;                      //   (REJ ⇒ no SREJ)
        context.IsExtended = false;                      // Modulo = 8
        context.N1 = 256;                         // 2048 bits = 256 octets
        context.K = 7;                           // Window Size Receive = 7
        context.T1V = TimeSpan.FromMilliseconds(3000); // Acknowledge Timer
        context.Srt = TimeSpan.FromMilliseconds(1500); //   keep T1V == 2*SRT
        context.N2 = 10;                          // Retries
        context.SegmenterReassemblerEnabled = false;         // v2.2-only (§1621)
    }

    /// <summary>Lesser of two notification values, treating absence as "no constraint".</summary>
    private static int? MinPresent(int? a, int? b)
    {
        if (a is null)
        {
            return b;
        }

        if (b is null)
        {
            return a;
        }

        return Math.Min(a.Value, b.Value);
    }

    /// <summary>Greater of two negotiated values, treating absence as "no preference".</summary>
    private static int? MaxPresent(int? a, int? b)
    {
        if (a is null)
        {
            return b;
        }

        if (b is null)
        {
            return a;
        }

        return Math.Max(a.Value, b.Value);
    }
}
