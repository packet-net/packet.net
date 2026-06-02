namespace Packet.Ax25.Session;

/// <summary>
/// Per-session toggles for deliberate, documented deviations from the AX.25
/// SDL figures, used where a figure is a confirmed upstream spec defect.
/// </summary>
/// <remarks>
/// <para>
/// This is the session-layer analogue of <see cref="Packet.Core.Ax25ParseOptions"/>
/// (which covers wire-parse pragmatism). The SDL tables themselves
/// (<c>Packet.Ax25.Sdl</c>, from <c>m0lte/ax25sdl</c>) stay faithful to the
/// published figures — including their defects — so the canonical transcription
/// tracks the in-progress draft. Where a figure is provably wrong, the runtime
/// corrects it here, behind a named flag, rather than diverging the tables.
/// </para>
/// <para>
/// Philosophy mirrors <c>Ax25ParseOptions</c>: the <see cref="Default"/> preset
/// does the spec-<i>correct</i> thing so the stack works out of the box;
/// <see cref="StrictlyFaithful"/> turns every quirk off, reproducing the figures
/// exactly as drawn (defects and all) for strict conformance testing.
/// </para>
/// <para>
/// <b>Pattern for adding a quirk</b> (replicable): name the flag
/// <c>Ax25Spec&lt;issue&gt;…</c> after the <c>packethacking/ax25spec</c> issue it
/// works around — so it is greppable and removable once the spec is fixed —
/// default it to the corrected behaviour, document the spec prose + the de-facto
/// implementation evidence, and open a packet.net tracking issue to delete it
/// when ax25sdl ships a figure carrying the upstream resolution.
/// </para>
/// </remarks>
public sealed record Ax25SessionQuirks
{
    /// <summary>
    /// Work around <c>packethacking/ax25spec#38</c>: figc4.5 (Timer Recovery)
    /// draws the SREJ-received retransmit path as the generic fresh-DL-DATA
    /// "Push frame onto queue" verb followed by "Invoke Retransmission"
    /// (go-back-N). That contradicts §4.3.2.4 / §6.4.8 ("retransmission of the
    /// <i>single</i> I frame numbered N(R) … frames transmitted following … are
    /// not retransmitted"), figc4.4's correct SREJ handler, and every surveyed
    /// implementation (direwolf and linbpq do single-frame selective; linux and
    /// rax25 don't implement SREJ-driven go-back-N at all). direwolf's author
    /// independently flagged the exact box as a "2006 revision … cut-n-paste
    /// from the REJ flow chart" and disabled it.
    /// </summary>
    /// <remarks>
    /// When <c>true</c> (default), an SREJ-received transition does single-frame
    /// selective retransmit — it redirects the figure's "Push frame onto queue"
    /// to the figc4.4 "Push Old I Frame N(r) on Queue" behaviour and skips the
    /// go-back-N "Invoke Retransmission". When <c>false</c>, the figc4.5 figure
    /// runs as drawn (which also throws on the payload-less push — strict
    /// conformance only). Delete this quirk once ax25sdl ships a corrected
    /// figc4.5. Removal tracked at m0lte/packet.net#227 ← packethacking/ax25spec#38.
    /// </remarks>
    /// <remarks>
    /// <para>
    /// <b>Scope — SREJ is response-only (m0lte/packet.net#234).</b> The redirect
    /// fires only on the figc4.5 SREJ <i>response</i> paths
    /// (<c>t24_srej_received_yes_yes_*_no</c>), which carry the
    /// <c>push_frame_on_queue</c> verb this quirk rewrites. The SREJ
    /// <i>command</i> paths (<c>t24_srej_received_no_yes_*</c>) carry only the
    /// go-back-N <c>Invoke Retransmission</c> (no push), so when this quirk skips
    /// it nothing is retransmitted on the command form. That is intentional and
    /// spec-aligned: AX.25 v2.2 §4.3.2.4 says "The SREJ frame is only sent as a
    /// response", and no surveyed implementation sends SREJ as a command or acts
    /// on receiving one (direwolf <c>src/ax25_link.c</c>: "Command path has been
    /// omitted because SREJ can only be response"; linbpq gates resend on
    /// <c>if (MSGFLAG &amp; RESP)</c>). The vestigial figc4.5 command-SREJ form is
    /// itself errata flagged upstream. If a future spec revision resurrects an
    /// actionable SREJ command, revisit here alongside #38.
    /// </para>
    /// </remarks>
    public bool Ax25Spec38SrejSelectiveRetransmit { get; init; } = true;

    /// <summary>
    /// Work around <c>packethacking/ax25spec#40</c>: figc4.4's out-of-sequence
    /// <c>I_received</c> handling has no receive-window guard. Any frame whose
    /// N(S) ≠ V(R) is treated as a future gap and gets SREJ'd (or REJ'd) —
    /// including a <i>duplicate</i> whose N(S) lies behind V(R), a frame the
    /// receiver has already delivered. AX.25 inherits its sequencing from
    /// ITU-T X.25 §2.4.6.4, which <i>discards</i> any I-frame whose N(S) falls
    /// outside the receive window [V(R), V(R)+k) rather than rejecting it.
    /// Without that guard a duplicate provokes an SREJ, the sender re-sends,
    /// the re-send is again out-of-window, provokes another SREJ … a livelock
    /// that never converges under multi-frame selective-reject recovery
    /// (reproduced in <c>LossRecoveryProperties</c>).
    /// </summary>
    /// <remarks>
    /// When <c>true</c> (default), an I-frame whose N(S) is outside the receive
    /// window is routed to figc4.4's own discard path (the
    /// <c>reject_exception:Yes</c> branch — process the acknowledgement, discard
    /// the data, respond RR(V(R)) only if P=1) instead of the SREJ/REJ path. The
    /// window predicate is OR'd into the figure's <c>reject_exception</c> decision
    /// — the exact point where the figure already chooses discard-over-reject — so
    /// no new transition or per-action rewrite is needed, and the fix covers both
    /// the SREJ and REJ out-of-sequence branches (the decision precedes the
    /// <c>srej_enabled</c> split). When <c>false</c>, the figure runs as drawn
    /// (out-of-window frames are SREJ'd, reproducing the livelock for strict
    /// conformance study). Delete once ax25sdl ships a figc4.4 carrying the
    /// upstream window guard. Implemented in m0lte/packet.net#242 ←
    /// packethacking/ax25spec#40.
    /// </remarks>
    public bool Ax25Spec40DiscardOutOfWindowIFrames { get; init; } = true;

    /// <summary>
    /// Work around <c>packethacking/ax25spec#41</c>: figc4.7 <c>Select_T1_Value</c>
    /// folds <c>(T1V − "Remaining Time on T1 When Last Stopped")</c> into the
    /// smoothed round-trip time without Karn's-algorithm guard. That term is only
    /// a valid round-trip sample when T1 was stopped by an acknowledgement of the
    /// frame whose transmission armed it. When the frame timed out / was
    /// retransmitted (or T1 was otherwise not freshly stopped by an ack), the
    /// remaining time is 0 and the "sample" degenerates to the full T1V (= 2·SRT).
    /// Since T1V is derived from SRT, feeding it back is self-amplifying:
    /// SRT' = 7/8·SRT + 1/8·(2·SRT) = 1.125·SRT, so SRT (and T1V) grow geometrically
    /// under sustained loss until <c>Next T1 &lt;- 2*SRT</c> overflows
    /// <see cref="TimeSpan"/> (m0lte/packet.net#241; reproduced by the conformance
    /// harness within a single multi-frame SREJ recovery).
    /// </summary>
    /// <remarks>
    /// When <c>true</c> (default), the SRT IIR update is skipped unless a genuine
    /// round-trip was measured this cycle — T1 was running and stopped by an ack,
    /// i.e. <c>T1RemainingWhenLastStopped &gt; 0</c>. On the timeout/retransmit
    /// path SRT is left unchanged (T1V still backs off via the RC term), per Karn.
    /// When <c>false</c>, the figure runs as drawn (the divergent IIR, for strict
    /// conformance study — will overflow under sustained loss). Not gated behind a
    /// T1V cap deliberately: leaving the unguarded path divergent keeps the fuzzer
    /// able to catch any *other* SRT-growth source rather than masking it. Delete
    /// once ax25sdl ships a figc4.7 carrying the Karn guard. Implemented in
    /// m0lte/packet.net#241 ← packethacking/ax25spec#41.
    /// </remarks>
    public bool Ax25Spec41KarnSrtSampling { get; init; } = true;

    /// <summary>
    /// Work around <c>packethacking/ax25spec#42</c>: figc4.4's out-of-sequence
    /// <c>I_received</c> SREJ path, when a selective-reject exception is already
    /// outstanding, does <c>N(r) := N(s)</c> before sending SREJ — so it requests
    /// retransmission of the frame that just <i>arrived</i> (and was just saved),
    /// not the missing gap. With more than one frame outstanding the real gap is
    /// never re-requested: the peer keeps resending the already-received frame and
    /// the receiver keeps SREJ'ing it, so selective-reject recovery livelocks
    /// (V(R) frozen until T1/N2 intervene; reproduced in <c>LossRecoveryProperties</c>).
    /// direwolf flags the identical erratum (<c>ax25_link.c</c>: "The SDL says ask
    /// for N(S) which is clearly wrong because that's what we just received") and
    /// requests the missing gap instead.
    /// </summary>
    /// <remarks>
    /// When <c>true</c> (default), the SREJ target is retargeted from N(S) to V(R)
    /// — the next still-missing frame — so the SREJ requests the actual gap. The
    /// rewrite fires only on an <c>I_received</c> trigger (the sole figure path
    /// carrying the <c>N(r) := N(s)</c> verb, connected.sdl.yaml), so it is inert
    /// elsewhere. When <c>false</c>, the figure runs as drawn (SREJ asks for the
    /// just-arrived frame, reproducing the livelock for strict conformance study).
    /// Delete once ax25sdl ships a figc4.4 requesting the gap. Implemented in
    /// m0lte/packet.net#246 ← packethacking/ax25spec#42.
    /// </remarks>
    public bool Ax25Spec42SrejTargetsGap { get; init; } = true;

    /// <summary>
    /// Work around <c>packethacking/ax25spec#43</c>: figc4.4's <c>DL-FLOW-OFF
    /// Request</c> handler gates its action stack (<c>Set Own Receiver Busy</c> →
    /// <c>RNR</c> → <c>Clear Acknowledge Pending</c>) on the <c>Own Receiver
    /// Busy? = Yes</c> branch, with the <c>No</c> branch returning straight to
    /// Connected. So a station that is <i>not</i> busy and receives DL-FLOW-OFF
    /// does nothing — it never enters the busy condition and never sends RNR, so
    /// the primitive can never establish flow control from a clean state (its
    /// entire purpose). §6.4.10 ("whenever a TNC enters a busy condition it sends
    /// an RNR response") and the mirror <c>DL-FLOW-ON</c> handler (correctly acting
    /// on its <c>Yes</c>/busy branch) both say the actions belong on the <c>No</c>
    /// (not-busy) branch.
    /// </summary>
    /// <remarks>
    /// When <c>true</c> (default), the <c>own_receiver_busy</c> guard is inverted
    /// for the <c>DL_FLOW_OFF_request</c> trigger only, so a not-busy station takes
    /// the action branch (enter busy + RNR) and an already-busy one no-ops —
    /// scoped to that trigger, inert elsewhere. When <c>false</c>, the figure runs
    /// as drawn (DL-FLOW-OFF is a no-op from a clean state). Unlike the other
    /// quirks this has <i>no de-facto corroboration</i> — neither direwolf nor
    /// linbpq implements DL-FLOW-OFF — so it rests on the §6.4.10 prose and the
    /// figure contradicting its own primitive. Delete once ax25sdl ships a figc4.4
    /// with the branches corrected. Implemented in m0lte/packet.net ←
    /// packethacking/ax25spec#43 (m0lte/ax25sdl#60, faithful figure).
    /// </remarks>
    public bool Ax25Spec43DlFlowOffEntersBusy { get; init; } = true;

    /// <summary>
    /// Default preset — spec-<i>correct</i> behaviour (all quirks on). This is
    /// what a session uses unless explicitly configured otherwise.
    /// </summary>
    public static Ax25SessionQuirks Default { get; } = new();

    /// <summary>
    /// Every quirk off — execute the SDL figures exactly as drawn, including
    /// known defects. For strict conformance testing against the published
    /// figures, not for on-air use.
    /// </summary>
    public static Ax25SessionQuirks StrictlyFaithful { get; } = new()
    {
        Ax25Spec38SrejSelectiveRetransmit = false,
        Ax25Spec40DiscardOutOfWindowIFrames = false,
        Ax25Spec41KarnSrtSampling = false,
        Ax25Spec42SrejTargetsGap = false,
        Ax25Spec43DlFlowOffEntersBusy = false,
    };
}
