namespace Packet.Ax25.Session;

/// <summary>
/// Per-session toggles for deliberate, documented deviations from the AX.25
/// SDL figures, used where a figure is a confirmed upstream spec defect — and,
/// distinctly, where the published <i>wire format</i> is under-specified and we
/// match the only known interoperating implementation by default (see
/// <see cref="SegmentFirstCarriesL3Pid"/>).
/// </summary>
/// <remarks>
/// <para>
/// This is the session-layer analogue of <see cref="Packet.Core.Ax25ParseOptions"/>
/// (which covers wire-parse pragmatism). The SDL tables themselves
/// (<c>Packet.Ax25.Sdl</c>, from <c>packet-net/ax25sdl</c>) stay faithful to the
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
/// <b>Two flavours of quirk live here.</b>
/// </para>
/// <para>
/// (1) <b>Figure-defect quirks</b> — name the flag <c>Ax25Spec&lt;issue&gt;…</c>
/// after the <c>packethacking/ax25spec</c> issue it works around (so it is
/// greppable and removable once the spec is fixed), default it to the corrected
/// behaviour, document the spec prose + the de-facto implementation evidence, and
/// open a packet.net tracking issue to delete it when ax25sdl ships a figure
/// carrying the upstream resolution.
/// </para>
/// <para>
/// (2) <b>De-facto-interop quirks</b> — where the spec text is genuinely ambiguous
/// or silent and a single real implementation establishes the de-facto wire
/// format. These are <i>not</i> tied to a filed figure-defect issue, so they do
/// <b>not</b> take the <c>Ax25Spec&lt;NN&gt;</c> prefix; name them descriptively
/// after what they do (e.g. <see cref="SegmentFirstCarriesL3Pid"/>). Default them
/// on (interoperate out of the box) and turn them off under
/// <see cref="StrictlyFaithful"/> (reproduce the figure-literal reading).
/// </para>
/// </remarks>
public sealed record Ax25SessionQuirks
{
    /// <summary>
    /// <b>De-facto-interop quirk (not a figure-defect — no ax25spec issue).</b>
    /// Controls the §6.6 segmentation wire format. AX.25 v2.2 Figure 6.2 draws a
    /// segmented I-frame's info field as the 0x08 segmented-PID octet plus a single
    /// <c>FXXXXXXX</c> F/X octet (First-indicator + 7-bit remaining-count) followed
    /// directly by the segment data — there is <b>no field carrying the original
    /// Layer-3 PID</b> through a segmented series, so a figure-literal reassembly has
    /// no way to recover it and must deliver the payload as
    /// <see cref="Ax25Frame.PidNoLayer3"/> (0xF0). The §6.6 prose ("a two-octet
    /// header") is ambiguous enough to admit a second reading, and <b>Dire Wolf
    /// (WB2OSZ) — the only known v2.2 segmenter — takes it</b>: its <i>first</i>
    /// segment carries an extra <b>inner-PID octet</b> (the original L3 PID) between
    /// the F/X octet and the data, which its reassembler reads back so the
    /// reassembled payload keeps its original PID (verified byte-exact against
    /// <c>ax25_link.c</c> <c>dl_data_request</c> ~L1330–1410 + <c>dl_data_indication</c>
    /// ~L2010–2030, and on the wire via the #177 docker stack).
    /// </summary>
    /// <remarks>
    /// <para>
    /// When <c>true</c> (default), the runtime emits and expects Dire Wolf's format:
    /// the first segment's info field is
    /// <c>[F/X octet][inner-PID = original L3 PID][segment data…]</c> and subsequent
    /// segments are <c>[F/X octet][segment data…]</c>; the reassembler reads the
    /// inner PID off the first segment and delivers the reassembled payload with that
    /// <b>original L3 PID</b>. The inner-PID octet counts toward the segment budget —
    /// it occupies one of the first segment's N1−1 payload slots, leaving N1−2 for
    /// data (Dire Wolf's <c>DIVROUNDUP(len + 1, N1 − 1)</c> "+1 for the original
    /// PID"). This interoperates with Dire Wolf out of the box <i>and</i> fixes the
    /// figure-literal limitation that the L3 PID is lost across a segmented series.
    /// </para>
    /// <para>
    /// When <c>false</c> (<see cref="StrictlyFaithful"/>), the runtime emits and
    /// expects the figure-literal format: every segment is
    /// <c>[F/X octet][segment data…]</c> with no inner-PID octet, and a reassembled
    /// payload is delivered as <see cref="Ax25Frame.PidNoLayer3"/> (0xF0) — Figure 6.2
    /// exactly as drawn, for strict conformance study.
    /// </para>
    /// <para>
    /// This is a wire-format de-facto-interop quirk, <b>not</b> a figc figure defect:
    /// there is no filed <c>ax25spec</c> issue and it does not take the
    /// <c>Ax25Spec&lt;NN&gt;</c> prefix. The underlying spec gap (Figure 6.2 / §6.6's
    /// two-octet header drops the L3 PID; Dire Wolf fills it non-standardly) is a
    /// candidate <c>ax25spec</c> clarification.
    /// </para>
    /// </remarks>
    public bool SegmentFirstCarriesL3Pid { get; init; } = true;

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
    /// figc4.5. Removal tracked at packet-net/packet.net#227 ← packethacking/ax25spec#38.
    /// </remarks>
    /// <remarks>
    /// <para>
    /// <b>Scope — SREJ is response-only (packet-net/packet.net#234).</b> The redirect
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
    /// upstream window guard. Implemented in packet-net/packet.net#242 ←
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
    /// <see cref="TimeSpan"/> (packet-net/packet.net#241; reproduced by the conformance
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
    /// packet-net/packet.net#241 ← packethacking/ax25spec#41.
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
    /// packet-net/packet.net#246 ← packethacking/ax25spec#42.
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
    /// with the branches corrected. Implemented in packet-net/packet.net ←
    /// packethacking/ax25spec#43 (packet-net/ax25sdl#60, faithful figure).
    /// </remarks>
    public bool Ax25Spec43DlFlowOffEntersBusy { get; init; } = true;

    /// <summary>
    /// Work around <c>packethacking/ax25spec#44</c>: figc4.1/figc4.2 route the <c>Disconnected</c>
    /// <c>DL-CONNECT request</c> path <i>unconditionally</i> to
    /// <c>"1 Awaiting Connection"</c> — <c>Establish Data Link</c> &#8594;
    /// <c>Set Layer 3 Initiated</c> &#8594; <b>Awaiting Connection</b> — with <b>no
    /// version branch</b>, regardless of modulo. (Verified against the authoritative
    /// graphml source <c>DataLink_Disconnected.graphml</c> in <c>packet-net/ax25sdl</c>:
    /// the initiator's DL-CONNECT edge has no modulo test; version routing exists
    /// only on the <i>responder</i> SABM/SABME-received side.) That is a faithful
    /// transcription of a defective figure: a v2.2-preferred connect sends a SABME
    /// (the figc4.7 <c>Establish_Data_Link</c> subroutine <i>does</i> branch on
    /// <c>mod_128</c> and emits SABME), but then parks in <c>AwaitingConnection</c>
    /// (figc4.2) instead of <c>AwaitingV22Connection</c> (figc4.6).
    /// </summary>
    /// <remarks>
    /// <para>
    /// Two real bugs follow from the mis-routing — both fixed by this redirect:
    /// (1) <c>AwaitingConnection</c>'s T1-expiry retry sends a hardcoded
    /// <c>SABM (P==1)</c> (figc4.2 <c>t05_t1_expiry_no</c> &#8594; <c>SABMPEqEq1</c>), so a
    /// lost initial SABME <b>downgrades the link to mod-8</b> on the first retry; and
    /// (2) <c>AwaitingConnection</c> has <b>no <c>FRMR_received</c> handler at all</b>,
    /// so the §975 fallback (a pre-v2.2 peer FRMRs our SABME &#8594; we drop to v2.0/SABM)
    /// cannot fire. <c>AwaitingV22Connection</c> (figc4.6) handles both correctly:
    /// <c>t13_t1_expiry_no</c> resends <c>SABME (P=1)</c>; <c>t14_frmr_received</c>
    /// sets version 2.0, re-establishes (now mod-8), and moves to
    /// <c>AwaitingConnection</c>; <c>t11_dm_received_yes</c> tears down (§975 DM case);
    /// <c>t12_ua_received_*</c> completes the mod-128 connection.
    /// </para>
    /// <para>
    /// When <c>true</c> (default), a <c>DL_CONNECT_request</c> firing in
    /// <c>Disconnected</c> while the link is extended
    /// (<see cref="Ax25SessionContext.IsExtended"/>) has its transition target
    /// rewritten from <c>AwaitingConnection</c> to <c>AwaitingV22Connection</c>; a
    /// mod-8 connect (<c>IsExtended==false</c>) is unchanged. Keying on
    /// <c>IsExtended</c> at dispatch time is self-consistent with the FRMR fallback:
    /// figc4.6 <c>t14</c> sets version 2.0 (<c>IsExtended=false</c>) before
    /// re-establishing, so the subsequent SABM connect naturally stays mod-8. When
    /// <c>false</c>, the figure runs as drawn (a mod-128 connect parks in
    /// <c>AwaitingConnection</c> and downgrades on retry — for strict conformance
    /// study). Unlike the guard-rewriting quirks this rewrites a transition's
    /// <i>target state</i> (in <see cref="Ax25Session"/>'s dispatch path), scoped to
    /// the single <c>Disconnected</c> DL-CONNECT transition under <c>IsExtended</c>.
    /// De-facto corroboration: direwolf's author hit the identical defect —
    /// <c>ax25_link.c</c> ~L1060 <c>enter_new_state(S, S-&gt;modulo == 128 ?
    /// state_5_awaiting_v22_connection : state_1_awaiting_connection)</c> with the
    /// comment "Original always sent SABM and went to state 1 … my enhancement".
    /// Delete once ax25sdl ships a figc4.2 carrying the version branch.
    /// </para>
    /// </remarks>
    public bool Ax25Spec44Mod128ConnectRoutesToV22 { get; init; } = true;

    /// <summary>
    /// Work around <c>packethacking/ax25spec#45</c>: figc4.6's <c>FRMR received</c>
    /// handler (t14) draws <c>Establish Data Link</c> <i>before</i>
    /// <c>Set Version 2.0</c>. <c>Establish_Data_Link</c> (figc4.7) branches on
    /// <c>mod_128</c>, so while the link is still extended the §975 v2.0 fallback
    /// re-establishes with a <b>SABME</b> — but a FRMR (which only a pre-v2.2 peer
    /// sends) is precisely the signal to drop to v2.0/SABM. So the fallback as drawn
    /// fails against a real v2.0 peer (it re-sends SABME → another FRMR/DM) and
    /// produces a modulo split against a v2.2 peer (re-establish SABME, but the
    /// initiator proceeds mod-8 via the later <c>Set Version 2.0</c>).
    /// </summary>
    /// <remarks>
    /// When <c>true</c> (default), the <c>AwaitingV22Connection</c>
    /// <c>FRMR_received</c> transition forces version 2.0 (<c>IsExtended=false</c>)
    /// <i>before</i> its actions run (in <see cref="Ax25Session"/>'s dispatch path),
    /// so <c>Establish_Data_Link</c> emits a <b>SABM</b> and the fallback genuinely
    /// re-establishes as v2.0; the figure's own later <c>Set Version 2.0</c> is then
    /// a no-op. When <c>false</c>, the figure runs as drawn (re-establish SABME).
    /// De-facto corroboration: direwolf's FRMR handler calls <c>set_version_2_0</c>
    /// before <c>establish_data_link</c> ("Erratum: Need to force v2.0. This is not
    /// in flow chart." — <c>ax25_link.c</c>, state_5 ~L234). Only meaningful once
    /// <see cref="Ax25Spec44Mod128ConnectRoutesToV22"/> makes figc4.6 reachable by an
    /// initiator. Delete once ax25sdl ships a figc4.6 t14 with the actions reordered.
    /// </remarks>
    public bool Ax25Spec45FrmrFallbackReestablishesV20 { get; init; } = true;

    /// <summary>
    /// Work around <c>packethacking/ax25spec#48</c>: the figc4.6 <c>DM received</c>
    /// no-degrade gap. figc4.6's
    /// <c>AwaitingV22Connection</c> <c>DM received</c> handler splits on the P/F bit
    /// of the DM and only the <c>F=0</c> branch (<c>t11_dm_received_no</c>) drops back
    /// toward v2.0 (→ <c>AwaitingConnection</c>); the <c>F=1</c> branch
    /// (<c>t11_dm_received_yes</c>) treats the DM as a §975 connection <i>refusal</i> and
    /// tears the link down to <c>Disconnected</c> with <b>no fallback</b> — leaving
    /// <see cref="Ax25SessionContext.IsExtended"/> stuck <c>true</c>. But a DM is precisely
    /// the signal that the peer cannot do v2.2 (it does not recognise our SABME), and a peer
    /// that DMs our <i>polled</i> SABME (P=1) correctly answers <b>F=1</b> — so a real
    /// non-v2.2 peer that refuses SABME with DM(F=1) hits the teardown branch and our
    /// v2.2-preferred connect dies instead of degrading. (Verified on the wire: XRouter
    /// answers our SABME(P=1) with DM(F=1) and figc4.6 routes it straight to
    /// <c>Disconnected</c> — packet.net interop <c>XrouterViaNetsimExtendedMode</c>.) This
    /// is the DM analogue of the FRMR fallback (<see cref="Ax25Spec45FrmrFallbackReestablishesV20"/>):
    /// a pre-v2.2 peer signals "no v2.2" with <i>either</i> an FRMR (BPQ) <i>or</i> a DM
    /// (XRouter), and both must degrade to v2.0/SABM rather than fail.
    /// </summary>
    /// <remarks>
    /// <para>
    /// When <c>true</c> (default), a <c>DM_received</c> firing in
    /// <c>AwaitingV22Connection</c> — for <b>either F=0 or F=1</b> — is treated exactly
    /// like the figc4.6 <c>t14_frmr_received</c> v2.0 fallback: version 2.0 is forced
    /// (<see cref="Ax25SessionContext.IsExtended"/> = <c>false</c>) and the link is
    /// re-established via <b>SABM</b> (the matched DM transition is rewritten to run
    /// <c>t14_frmr_received</c>'s action chain — SRT reset, <c>Establish_Data_Link</c>
    /// → SABM since the link is now mod-8, <c>Set Layer 3 Initiated</c>,
    /// <c>Set Version 2.0</c> — and lands in <c>AwaitingConnection</c>, the v2.0 path).
    /// So <i>any</i> DM to our SABME means "peer can't do v2.2, retry mod-8", never a
    /// refusal. When <c>false</c> (<see cref="StrictlyFaithful"/>), the figure runs as
    /// drawn: DM(F=0) drops to <c>AwaitingConnection</c> with no re-establish and DM(F=1)
    /// tears down to <c>Disconnected</c> (the §975 refusal, for strict conformance study).
    /// </para>
    /// <para>
    /// <b>Scope is the extended-connect state only.</b> A DM received in
    /// <c>AwaitingConnection</c> (in response to a subsequent <i>SABM</i>) stays a genuine
    /// v2.0 refusal → <c>Disconnected</c> (figc4.2 <c>t03</c>), unchanged — the quirk is
    /// keyed on <c>From == AwaitingV22Connection</c>, so it is inert once the fallback has
    /// already dropped us to the mod-8 path. Like <see cref="Ax25Spec44Mod128ConnectRoutesToV22"/>
    /// / <see cref="Ax25Spec45FrmrFallbackReestablishesV20"/>, this rewrites a transition in
    /// <see cref="Ax25Session"/>'s dispatch path (the matched DM <see cref="TransitionSpec"/>
    /// is substituted for the FRMR one, and <c>IsExtended</c> is forced before the actions
    /// run so <c>Establish_Data_Link</c> emits SABM). Only meaningful once
    /// <see cref="Ax25Spec44Mod128ConnectRoutesToV22"/> makes figc4.6 reachable by an
    /// initiator. De-facto corroboration: direwolf's author hit the analogous figc4.6
    /// no-degrade gap and made his initiator fall back rather than honour the figure-literal
    /// teardown. Delete once ax25sdl ships a figc4.6 carrying the DM-degrades-to-v2.0
    /// resolution (tracked upstream at <c>packethacking/ax25spec#48</c>).
    /// </para>
    /// </remarks>
    public bool Ax25Spec48DmRejectionDegradesToV20 { get; init; } = true;

    /// <summary>
    /// Work around <c>packethacking/ax25spec#47</c>: figc4.5 (Timer Recovery) draws the
    /// in-sequence <c>I_received</c> stored-frame drain loop with
    /// <c>V(r) := V(r) - 1</c> in its body, where the structurally-identical
    /// figc4.4 (Connected) handler — same path, same pre-loop <c>V(r) := V(r) + 1</c>
    /// — uses <c>V(r) := V(r) + 1</c>. The drain delivers each consecutively-stored
    /// (previously SREJ-gap-filled) frame and must <i>advance</i> V(R) past it; a
    /// decrement makes V(R) net-stationary (or regress) the moment one stored frame
    /// is drained, so a station recovering in Timer Recovery delivers the gap-filled
    /// frames but leaves V(R) pointing back at an already-delivered sequence number.
    /// The peer's next (genuine, still-unacknowledged-window) retransmit is then
    /// taken as new data and <b>re-delivered</b>, and the link fails to converge —
    /// reproduced under simultaneous bidirectional SREJ at low n (k = 4): A delivers
    /// the peer's two-frame stream twice (<c>[0x80,0x81,0x80,0x81]</c>).
    /// </summary>
    /// <remarks>
    /// When <c>true</c> (default), the figc4.5 stored-frame drain advances V(R)
    /// (the loop-body <c>V(r) := V(r) - 1</c> verb is rewritten to
    /// <c>V(r) := V(r) + 1</c> in <see cref="ActionDispatcher"/>), matching figc4.4
    /// and §6.4.2.1 ("accepts the received I frame, increments its receive state
    /// variable"), so a Timer-Recovery stored-frame drain leaves V(R) correctly past
    /// the delivered frames. The verb <c>V(r) := V(r) - 1</c> appears <i>only</i> in
    /// these three figc4.5 drain loops (no other transition uses it), so the rewrite
    /// is inert everywhere else. When <c>false</c>, the figure runs as drawn (the
    /// decrement, reproducing the duplicate-delivery / non-convergence for strict
    /// conformance study). De-facto corroboration: direwolf's
    /// <c>dl_data_indication</c> drain (<c>ax25_link.c</c>) advances <c>state-&gt;vr</c>
    /// as it pulls each stored frame off <c>rxdata_by_ns[]</c> — it never decrements.
    /// Delete once ax25sdl ships a figc4.5 carrying the corrected increment
    /// (packethacking/ax25spec#47). The figc4.4 (Connected) handler is already correct, so no
    /// quirk is needed there.
    /// </remarks>
    public bool Ax25Spec47TimerRecoveryDrainAdvancesVR { get; init; } = true;

    /// <summary>
    /// Work around <c>packethacking/ax25spec#9</c>: the figures only reset the
    /// retry counter RC on the Timer-Recovery checkpoint branch where
    /// <i>everything</i> is acknowledged (figc4.5's RR-response F=1 path with
    /// V(S)=V(A) → Connected; re-entry re-initialises RC). The partial-ack branch
    /// (V(S)≠V(A) → invoke retransmission, stay in Timer Recovery) never touches
    /// RC — and under sustained bulk transfer the sender almost always has fresh
    /// I-frames in flight when the F=1 lands, so it lives in Timer Recovery
    /// indefinitely while making continuous forward progress, RC ratcheting +1 on
    /// every T1 hiccup and never resetting. After N2 <i>lifetime</i> hiccups —
    /// not N2 <i>consecutive</i> failures — the link is declared dead
    /// (<c>t21_t1_expiry_yes_no</c>: DL-ERROR I → DM → Disconnected) even though
    /// acks were flowing seconds earlier. Reproduced by
    /// <c>tools/Packet.LinkBench</c> on net-sim: a 32 KiB transfer at 1200 baud
    /// half-duplex died mid-transfer at the 10th T1 expiry, ~200 s into a link
    /// that was progressing the whole time (docs/link-bench-plan.md §12).
    /// </summary>
    /// <remarks>
    /// <para>
    /// When <c>true</c> (default), a T1 expiry that follows V(A)-advancing
    /// progress since the previous T1 expiry clamps RC to 1 <i>before</i> the
    /// figures' <c>RC = N2?</c> guard is evaluated — the peer acknowledging
    /// <i>new</i> data is exactly the proof RC exists to test ("they can hear
    /// us, and we can hear them"), so that expiry starts a fresh
    /// consecutive-failure run instead of continuing a lifetime tally. RC then
    /// counts <i>consecutive</i> recovery failures, and a genuinely dead link
    /// still exhausts N2 (no progress → no clamps). When <c>false</c>
    /// (<see cref="StrictlyFaithful"/>), the figures run as drawn (RC ratchets
    /// across a working link — strict conformance study).
    /// </para>
    /// <para>
    /// The clamp deliberately happens at T1-expiry time and goes to 1, not to 0
    /// at ack time: RC==0 is also the figures' Karn signal to
    /// <c>Select_T1</c>'s sampling branch ("no retransmission in progress — the
    /// round trip is clean"), so zeroing RC mid-recovery would feed
    /// retransmit-polluted samples into the SRT estimator and corrupt T1V (it
    /// measurably wedged the SREJ-under-loss suite that way). De-facto
    /// corroboration: rax25 (Thomas Habets, who filed #9) ships the
    /// equivalent fix — <c>state.rs</c> resets <c>rc</c> on every <c>va</c>
    /// update — with before/after pcaps on a 75 % loss channel in the issue
    /// (rax25's RC plays no role in its RTT estimation, so it can reset
    /// eagerly; ours clamps lazily for Karn's sake). Delete once ax25spec
    /// resolves #9 and ax25sdl ships figures carrying the reset.
    /// </para>
    /// </remarks>
    public bool Ax25Spec9AckProgressResetsRc { get; init; } = true;

    /// <summary>
    /// Enforce the Selective-Repeat window-wrap invariant for
    /// <c>packethacking/ax25spec#13</c>: when SREJ is enabled, the send window
    /// must satisfy <c>k ≤ modulus/2</c> (≤ 4 for mod-8, ≤ 64 for mod-128).
    /// Selective Repeat keys retransmission-recovery state by the bare N(S), so
    /// the sender and receiver windows must not overlap modulo the sequence
    /// space — the classic 2·W ≤ modulus bound. AX.25 lets <c>k</c> range to
    /// modulus−1 (fine for go-back-N, which buffers no out-of-order frames), and
    /// the figures never enforce the tighter Selective-Repeat bound, so a session
    /// running SREJ with <c>k &gt; modulus/2</c> can, under loss, <b>silently
    /// deliver a stale stored I-frame from the previous ring cycle</b> (one with
    /// the same N(S) exactly <c>modulus</c> frames back) in place of the live
    /// frame — exact-length, wrong-content payload corruption. Reproduced by
    /// <c>tools/Packet.LinkBench</c>: at mod-8, 5% loss, SREJ on, corruption
    /// appears at <c>k ≥ 5</c> and is absent at <c>k ≤ 4</c> (packet-net/packet.net#393).
    /// </summary>
    /// <remarks>
    /// When <c>true</c> (default), <see cref="Ax25SessionContext.EffectiveSendWindow"/>
    /// caps the outstanding-I-frame window at <c>modulus/2</c> whenever
    /// <see cref="Ax25SessionContext.SrejEnabled"/> is set — both the
    /// <c>v_s_eq_v_a_plus_k</c> guard and the I-frame-queue drain honour the cap,
    /// so no more than <c>modulus/2</c> frames are ever outstanding and two
    /// in-flight frames can never share an N(S). A configured <c>k</c> above the
    /// cap is silently run at the safe window for the duration that SREJ is in
    /// effect (the configured value is untouched and applies again if SREJ is
    /// disabled / on a go-back-N link). When <c>false</c>
    /// (<see cref="StrictlyFaithful"/>), the figures run as drawn — SREJ at
    /// <c>k &gt; modulus/2</c> is permitted and corrupts under loss, for
    /// conformance study only. Tighten or remove once ax25spec#13 resolves how
    /// the spec should bound <c>k</c>.
    /// </remarks>
    public bool Ax25Spec13ClampSrejWindowToHalfModulus { get; init; } = true;

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
        SegmentFirstCarriesL3Pid = false,
        Ax25Spec38SrejSelectiveRetransmit = false,
        Ax25Spec40DiscardOutOfWindowIFrames = false,
        Ax25Spec41KarnSrtSampling = false,
        Ax25Spec42SrejTargetsGap = false,
        Ax25Spec43DlFlowOffEntersBusy = false,
        Ax25Spec44Mod128ConnectRoutesToV22 = false,
        Ax25Spec45FrmrFallbackReestablishesV20 = false,
        Ax25Spec48DmRejectionDegradesToV20 = false,
        Ax25Spec47TimerRecoveryDrainAdvancesVR = false,
        Ax25Spec9AckProgressResetsRc = false,
        Ax25Spec13ClampSrejWindowToHalfModulus = false,
    };
}
