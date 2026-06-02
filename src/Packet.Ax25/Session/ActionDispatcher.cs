using Packet.Ax25.Sdl;
using Packet.Core;

namespace Packet.Ax25.Session;

/// <summary>
/// Consumes the action chain attached to an SDL transition. Implementations
/// decide how (or whether) to enforce action handlers — production uses
/// <see cref="ActionDispatcher"/>, tests can substitute a recording stub.
/// </summary>
public interface IActionDispatcher
{
    /// <summary>
    /// Execute the supplied <paramref name="actions"/> in order against the
    /// supplied transition context. Every verb may read or mutate fields on
    /// <see cref="TransitionContext.Session"/>, arm / cancel timers on
    /// <see cref="TransitionContext.Scheduler"/>, read fields on
    /// <see cref="TransitionContext.IncomingFrame"/>, or accumulate fields
    /// onto <see cref="TransitionContext.Pending"/> for the next outgoing
    /// frame in the chain.
    /// </summary>
    void Execute(IEnumerable<ActionStep> actions, TransitionContext tx);
}

/// <summary>
/// Executes the action strings recorded in an SDL transition's
/// <c>actions:</c> list. Each verb maps to one method on the dispatcher,
/// which either mutates the <see cref="Ax25SessionContext"/>, arms /
/// cancels a timer via the <see cref="ITimerScheduler"/>, reads fields
/// from the triggering frame, or signals an outgoing supervisory frame
/// via the configured sink callback.
/// </summary>
/// <remarks>
/// <para>
/// The action vocabulary is bounded by the SDL transcriptions we've
/// produced. New verbs land here as new pages are transcribed — one
/// <c>case</c> arm per verb. Unknown actions throw, which keeps
/// transcription typos from being silent.
/// </para>
/// <para>
/// Timer durations default to spec values (T1=3000ms, T2=1500ms,
/// T3=30000ms). The actual values are negotiated via XID and should be
/// updated on the dispatcher when negotiation completes — for now we
/// expose them as <c>init</c>-only properties.
/// </para>
/// </remarks>
public sealed class ActionDispatcher : IActionDispatcher
{
    private readonly Action<string> onTimerExpiry;
    private readonly Action<SupervisoryFrameSpec> sendSFrame;
    private readonly Action<UFrameSpec> sendUFrame;
    private readonly Action<UiFrameSpec> sendUiFrame;
    private readonly Action<IFrameSpec> sendIFrame;
    private readonly Action<DataLinkSignal> sendUpward;
    private readonly Action<LinkMultiplexerSignal> sendLinkMux;
    private readonly Action<InternalSignal> sendInternal;
    private readonly ISubroutineRegistry subroutines;

    /// <summary>
    /// The subroutine registry this dispatcher consults when a
    /// <c>kind: subroutine</c> action fires. Exposed so callers (e.g.
    /// <see cref="Ax25Session"/>) can wire a <see cref="DefaultSubroutineRegistry"/>
    /// with the guard evaluator it needs to walk
    /// <see cref="SubroutineSpec"/> paths.
    /// </summary>
    public ISubroutineRegistry Subroutines => subroutines;

    /// <summary>Default acknowledgement timer (T1).</summary>
    public TimeSpan T1Duration { get; init; } = TimeSpan.FromMilliseconds(3000);

    /// <summary>Default response-delay timer (T2).</summary>
    public TimeSpan T2Duration { get; init; } = TimeSpan.FromMilliseconds(1500);

    /// <summary>Default inactive-link timer (T3).</summary>
    public TimeSpan T3Duration { get; init; } = TimeSpan.FromMilliseconds(30000);

    /// <summary>
    /// Construct a dispatcher.
    /// </summary>
    /// <param name="onTimerExpiry">
    /// Called with the timer name ("T1" / "T2" / "T3") when a started
    /// timer expires. The session uses this to translate the expiry into
    /// an <see cref="Ax25Event"/> (e.g., <see cref="T1Expiry"/>) and feed
    /// it back into the dispatcher loop.
    /// </param>
    /// <param name="sendSFrame">
    /// Called when an action requests transmission of a supervisory
    /// frame (RR / RNR / REJ / SREJ). The session translates the spec
    /// into an <see cref="Ax25Frame"/> and ships it on the wire.
    /// </param>
    /// <param name="sendUFrame">
    /// Called when an action requests transmission of an unnumbered
    /// frame (SABM, SABME, DISC, UA, DM, FRMR, XID, TEST). The session
    /// translates the spec into an <see cref="Ax25Frame"/> and ships it.
    /// Defaults to a no-op sink when omitted so the dispatcher can run
    /// in test harnesses that don't care about U-frame emission.
    /// </param>
    /// <param name="sendUiFrame">
    /// Called when an action requests transmission of a UI frame
    /// (typically <c>UI_command</c> triggered by a
    /// <see cref="DlUnitDataRequest"/>). The session translates the
    /// spec into an <see cref="Ax25Frame"/> and ships it. Defaults to a
    /// no-op sink.
    /// </param>
    /// <param name="sendUpward">
    /// Called when an action raises a signal to Layer 3 — the five DL
    /// primitives (<c>DL_CONNECT_indication</c>, <c>DL_CONNECT_confirm</c>,
    /// <c>DL_DISCONNECT_indication</c>, <c>DL_DISCONNECT_confirm</c>,
    /// <c>DL_DATA_indication</c>) and the error-indication letter
    /// variants (<c>DL_ERROR_indication_*</c>). Defaults to a no-op sink.
    /// </param>
    /// <param name="sendLinkMux">
    /// Called when an action raises a signal to the link multiplexer
    /// (<c>LM_SEIZE_request</c>, <c>LM_RELEASE_request</c>,
    /// <c>LM_DATA_request</c>). Defaults to a no-op sink.
    /// </param>
    /// <param name="sendInternal">
    /// Called when an action raises an internal signal — to the
    /// management data-link (<c>MDL_NEGOTIATE_request</c>) or the
    /// internal I-frame queue (<c>push_*</c> verbs). Defaults to a
    /// no-op sink.
    /// </param>
    /// <param name="sendIFrame">
    /// Called when an action requests transmission of an information
    /// (I) frame. Triggered by the <c>I_command</c> verb after the
    /// session pops a payload off <see cref="Ax25SessionContext.IFrameQueue"/>
    /// and posts a synthetic <see cref="IFramePopsOffQueue"/> event.
    /// Defaults to a no-op sink.
    /// </param>
    /// <param name="subroutines">
    /// Registry for SDL subroutine action chains
    /// (<c>Establish_Data_Link</c>, <c>UI_Check</c>,
    /// <c>Select_T1_Value</c>, …). Defaults to a fresh
    /// <see cref="DefaultSubroutineRegistry"/> with no-op stubs for
    /// every known subroutine — sufficient for testing transition flow
    /// without figc4.7's real bodies wired.
    /// </param>
    public ActionDispatcher(
        Action<string> onTimerExpiry,
        Action<SupervisoryFrameSpec> sendSFrame,
        Action<UFrameSpec>? sendUFrame = null,
        Action<UiFrameSpec>? sendUiFrame = null,
        Action<DataLinkSignal>? sendUpward = null,
        Action<LinkMultiplexerSignal>? sendLinkMux = null,
        Action<InternalSignal>? sendInternal = null,
        Action<IFrameSpec>? sendIFrame = null,
        ISubroutineRegistry? subroutines = null)
    {
        this.onTimerExpiry = onTimerExpiry ?? throw new ArgumentNullException(nameof(onTimerExpiry));
        this.sendSFrame    = sendSFrame    ?? throw new ArgumentNullException(nameof(sendSFrame));
        this.sendUFrame    = sendUFrame    ?? (_ => { });
        this.sendUiFrame   = sendUiFrame   ?? (_ => { });
        this.sendUpward    = sendUpward    ?? (_ => { });
        this.sendLinkMux   = sendLinkMux   ?? (_ => { });
        this.sendInternal  = sendInternal  ?? (_ => { });
        this.sendIFrame    = sendIFrame    ?? (_ => { });
        this.subroutines   = subroutines   ?? new DefaultSubroutineRegistry();
    }

    /// <inheritdoc/>
    public void Execute(IEnumerable<ActionStep> actions, TransitionContext tx)
    {
        ArgumentNullException.ThrowIfNull(actions);
        ArgumentNullException.ThrowIfNull(tx);

        foreach (var step in actions)
        {
            Execute(step.Verb, tx);
        }
    }

    /// <summary>
    /// Execute every action verb in <paramref name="actions"/> against the
    /// supplied transition context. Used by hand-rolled test fixtures and
    /// ad-hoc consumers that don't need shape-class metadata.
    /// </summary>
    public void Execute(IEnumerable<Ax25ActionVerb> actions, TransitionContext tx)
    {
        ArgumentNullException.ThrowIfNull(actions);
        ArgumentNullException.ThrowIfNull(tx);

        foreach (var verb in actions)
        {
            Execute(verb, tx);
        }
    }

    /// <summary>
    /// Test-ergonomics overload: execute a single verb with no triggering
    /// frame. Constructs a <see cref="TransitionContext"/> with a sentinel
    /// trigger that has no attached <see cref="Ax25Frame"/>; verbs that
    /// require <see cref="TransitionContext.IncomingFrame"/> (e.g.
    /// <c>V(a) := N(r)</c>) throw when invoked through this path.
    /// </summary>
    public void Execute(Ax25ActionVerb verb, Ax25SessionContext context, ITimerScheduler scheduler)
        => Execute(verb, new TransitionContext(context, scheduler, SyntheticTrigger.Instance));

    /// <summary>
    /// Test-ergonomics overload: execute multiple verbs with no triggering
    /// frame. See <see cref="Execute(Ax25ActionVerb, Ax25SessionContext, ITimerScheduler)"/>.
    /// </summary>
    public void Execute(IEnumerable<Ax25ActionVerb> actions, Ax25SessionContext context, ITimerScheduler scheduler)
        => Execute(actions, new TransitionContext(context, scheduler, SyntheticTrigger.Instance));

    /// <summary>
    /// Sentinel <see cref="Ax25Event"/> used by the test-ergonomics
    /// overloads when no real trigger is supplied. Has no attached frame
    /// and a deliberately distinctive name so any verb that ends up
    /// dereferencing it can point a clear error back at the test.
    /// </summary>
    private sealed record SyntheticTrigger() : Ax25Event("__synthetic_trigger__")
    {
        public static SyntheticTrigger Instance { get; } = new();
    }

    /// <summary>
    /// Execute a single action verb against the transition context.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The verb is a generated <see cref="Ax25ActionVerb"/> — the codegen
    /// in <c>Packet.Ax25.Sdl</c> (0.8.0+) is the SOLE canonicaliser, so
    /// every figure-spelling variant has already been folded to one typed
    /// member before it reaches us (no runtime alias map, no string
    /// normalisation). The dispatch below is a switch <em>expression</em>
    /// over that closed set: a new or renamed verb that lands without a
    /// matching arm is a compile error (CS8509), which with
    /// <c>TreatWarningsAsErrors</c> on fails the build — killing the
    /// verb-vs-dispatch bug class (UI-reception #258, DL-DATA-while-
    /// connecting #263) at compile time rather than at runtime.
    /// </para>
    /// <para>
    /// Each arm has side effects (mutate ctx, arm/cancel timers, send
    /// frames, raise signals, call subroutines) and most call void
    /// methods, so they are wrapped in the <see cref="Do"/> helper to
    /// yield a value; the whole switch is assigned to a discard. Behaviour
    /// is a 1:1 port of the prior string switch — keep each arm identical.
    /// </para>
    /// </remarks>
    public void Execute(Ax25ActionVerb verb, TransitionContext tx)
    {
        ArgumentNullException.ThrowIfNull(tx);

        var ctx = tx.Session;
        var scheduler = tx.Scheduler;

        // Quirk Ax25Spec38SrejSelectiveRetransmit (default on): figc4.5 draws the
        // SREJ-received retransmit as the generic fresh-DL-DATA push + go-back-N
        // "Invoke Retransmission", contradicting §4.3.2.4/figc4.4 and every
        // implementation (packethacking/ax25spec#38). On an SREJ trigger we do
        // single-frame selective retransmit instead: redirect the push to the
        // figc4.4 "Push Old I Frame N(r) on Queue" behaviour, and skip the
        // go-back-N. Remove once ax25sdl ships a corrected figc4.5.
        //
        // Note (m0lte/packet.net#234): this rewrite only has a push to redirect on
        // the SREJ *response* paths (t24_srej_received_yes_yes_*_no). The SREJ
        // *command* paths (t24_srej_received_no_yes_*) carry only Invoke_Retransmission,
        // so skipping it retransmits nothing on the command form. That is
        // deliberate and spec-aligned — §4.3.2.4 makes SREJ response-only and no
        // deployed stack sends or acts on an SREJ command (direwolf omits the
        // command path; linbpq gates resend on RESP). The command-SREJ form is
        // vestigial errata; see Ax25SessionQuirks and DataLinkSrejUnderLossTests.
        //
        // The push family (push_on_I_frame_queue / push_frame_on_queue and the
        // figure word-order variants) all collapse to the same enum members; any
        // of them on an SREJ trigger redirects to the single-frame retransmit.
        if (ctx.Quirks.Ax25Spec38SrejSelectiveRetransmit && tx.Trigger is SrejReceived)
        {
            if (verb is Ax25ActionVerb.PushOnIFrameQueue
                     or Ax25ActionVerb.PushOnIFrameQueueNoteWordOrder
                     or Ax25ActionVerb.PushFrameOnQueue
                     or Ax25ActionVerb.PushIFrameOnIQueue)
            {
                verb = Ax25ActionVerb.PushOldIFrameNROnQueue;
            }
            else if (verb is Ax25ActionVerb.InvokeRetransmission)
            {
                return;
            }
        }

        // Quirk Ax25Spec42SrejTargetsGap (default on): figc4.4's out-of-sequence
        // I_received SREJ path (with a selective-reject exception already
        // outstanding) does `N(r) := N(s)` before SREJ — requesting the frame that
        // just arrived rather than the missing gap, so multi-frame SREJ recovery
        // livelocks (packethacking/ax25spec#42; direwolf flags the same erratum and
        // requests the gap). Retarget the SREJ to V(R), the next still-missing
        // frame. `N(r) := N(s)` appears only in this one I_received figure path, so
        // gating on the I_received trigger scopes the rewrite precisely. Remove once
        // ax25sdl ships a corrected figc4.4. m0lte/packet.net#246.
        if (ctx.Quirks.Ax25Spec42SrejTargetsGap
            && tx.Trigger is IFrameReceived
            && verb == Ax25ActionVerb.NRAssignNS)
        {
            verb = Ax25ActionVerb.NRAssignVR;
        }

        // Exhaustive dispatch. Every arm yields a value (via Do for the
        // void-returning ones) so the compiler enforces completeness; the
        // result is discarded. A missing or renamed enum member fails the
        // build with CS8509 (an error here, TreatWarningsAsErrors) — that is
        // the whole point: a new verb cannot ship without a handler.
        //
        // CS8524 is the *separate* "you didn't handle an out-of-band integer
        // cast of the enum" diagnostic. We suppress only that one: the verb
        // always originates from the generated Packet.Ax25.Sdl tables, so it
        // is only ever a defined member; an `(Ax25ActionVerb)999` cast is
        // unreachable. Suppressing CS8524 here (and deliberately NOT adding a
        // `_ =>` wildcard arm, which would swallow future named members) keeps
        // CS8509's named-member exhaustiveness fully active.
#pragma warning disable CS8524
        _ = verb switch
        {
            // ─── Flag mutations ────────────────────────────────────────
            Ax25ActionVerb.SetOwnReceiverBusy     => Do(() => ctx.OwnReceiverBusy    = true),
            Ax25ActionVerb.ClearOwnReceiverBusy   => Do(() => ctx.OwnReceiverBusy    = false),
            Ax25ActionVerb.SetPeerReceiverBusy    => Do(() => ctx.PeerReceiverBusy   = true),
            Ax25ActionVerb.ClearPeerReceiverBusy  => Do(() => ctx.PeerReceiverBusy   = false),
            Ax25ActionVerb.SetAcknowledgePending  => Do(() => ctx.AcknowledgePending = true),
            Ax25ActionVerb.ClearAcknowledgePending => Do(() => ctx.AcknowledgePending = false),
            Ax25ActionVerb.SetLayer3Initiated     => Do(() => ctx.Layer3Initiated    = true),
            Ax25ActionVerb.ClearLayer3Initiated   => Do(() => ctx.Layer3Initiated    = false),

            // ─── Timer operations ──────────────────────────────────────
            //
            // T1 uses the per-session <see cref="Ax25SessionContext.T1V"/>
            // value — refreshed dynamically by figc4.7's Select_T1_Value
            // subroutine and by figc4.1/4.2's link-establishment paths
            // (SRT := Initial Default; T1V := 2 * SRT). T2 and T3 stay on
            // the dispatcher's static defaults until per-session values
            // get wired (the SDL pages don't currently mutate them). The
            // figc4.7 title-case spellings (Start T1 / Stop T3 / …) fold to
            // the same enum members as the snake_case state-page forms.
            Ax25ActionVerb.StartT1                => Do(() => scheduler.Arm("T1", ctx.T1V,    () => onTimerExpiry("T1"))),
            Ax25ActionVerb.StartT3                => Do(() => scheduler.Arm("T3", T3Duration, () => onTimerExpiry("T3"))),
            // stop_T1: capture remaining time BEFORE cancelling so the next
            // Select_T1_Value gets a real round-trip sample for its IIR. The
            // spec calls this "Remaining Time on T1 When Last Stopped"; after
            // Cancel the timer is gone and queries return zero.
            Ax25ActionVerb.StopT1                 => Do(() =>
            {
                ctx.T1RemainingWhenLastStopped = scheduler.TimeRemaining("T1");
                scheduler.Cancel("T1");
            }),
            Ax25ActionVerb.StopT3                 => Do(() => scheduler.Cancel("T3")),

            // ─── Supervisory-frame transmissions ───────────────────────
            //
            // The bare verbs (RR, REJ, SREJ) are response-form per SDL
            // convention; the Command/Response-suffixed verbs are explicit.
            // Each emission consumes tx.Pending.Nr and tx.Pending.PfBit,
            // both of which must have been populated by an earlier
            // processing verb in the same chain. The figc4.7 full-word
            // spellings (RR Command, RNR Response, …) and the qualifier
            // variants (RR Command (P = 0), RNR Response (F = 0)) collapse
            // to these same members.
            Ax25ActionVerb.RRCommand              => Do(() => sendSFrame(BuildSFrame(SupervisoryFrameType.Rr,   isCommand: true,  tx))),
            Ax25ActionVerb.RRCommandPEq0          => Do(() => sendSFrame(BuildSFrame(SupervisoryFrameType.Rr,   isCommand: true,  tx))),
            Ax25ActionVerb.RR                     => Do(() => sendSFrame(BuildSFrame(SupervisoryFrameType.Rr,   isCommand: false, tx))),
            Ax25ActionVerb.RRResponse             => Do(() => sendSFrame(BuildSFrame(SupervisoryFrameType.Rr,   isCommand: false, tx))),
            Ax25ActionVerb.RNRCommand             => Do(() => sendSFrame(BuildSFrame(SupervisoryFrameType.Rnr,  isCommand: true,  tx))),
            Ax25ActionVerb.RNR                    => Do(() => sendSFrame(BuildSFrame(SupervisoryFrameType.Rnr,  isCommand: false, tx))),
            Ax25ActionVerb.RNRResponse            => Do(() => sendSFrame(BuildSFrame(SupervisoryFrameType.Rnr,  isCommand: false, tx))),
            Ax25ActionVerb.RNRResponseFEq0        => Do(() => sendSFrame(BuildSFrame(SupervisoryFrameType.Rnr,  isCommand: false, tx))),
            Ax25ActionVerb.REJ                    => Do(() => sendSFrame(BuildSFrame(SupervisoryFrameType.Rej,  isCommand: false, tx))),
            Ax25ActionVerb.SREJ                   => Do(() => sendSFrame(BuildSFrame(SupervisoryFrameType.Srej, isCommand: false, tx))),

            // ─── Unnumbered-frame transmissions ────────────────────────
            //
            // Spellings with an explicit poll/final qualifier (SABM (P == 1),
            // SABME (P = 1), DISC (P = 1), DM (F = 1)) force the P/F bit to 1
            // regardless of tx.Pending. Bare verbs (UA, DM, Expedited UA,
            // Expedited DM, DM Response (F = 0)) consume tx.Pending.PfBit,
            // defaulting to false. The figc4.7 bare SABM / SABME spellings
            // force P=1 (they are command frames soliciting a response).
            //
            // Expedited UA / Expedited DM (figc4.3) hint the frame should
            // jump ahead of the normal TX queue. The wire bit pattern is
            // identical to UA / DM; the wire-translation layer reads
            // <see cref="UFrameSpec.IsExpedited"/> to prioritise it.
            Ax25ActionVerb.UA                     => Do(() => sendUFrame(BuildUFrame(UFrameType.Ua,    isCommand: false, pfBitOverride: null, isExpedited: false, tx))),
            Ax25ActionVerb.DM                     => Do(() => sendUFrame(BuildUFrame(UFrameType.Dm,    isCommand: false, pfBitOverride: null, isExpedited: false, tx))),
            Ax25ActionVerb.DMResponseFEq0         => Do(() => sendUFrame(BuildUFrame(UFrameType.Dm,    isCommand: false, pfBitOverride: null, isExpedited: false, tx))),
            Ax25ActionVerb.DMFEq1                 => Do(() => sendUFrame(BuildUFrame(UFrameType.Dm,    isCommand: false, pfBitOverride: true, isExpedited: false, tx))),
            Ax25ActionVerb.ExpeditedUA            => Do(() => sendUFrame(BuildUFrame(UFrameType.Ua,    isCommand: false, pfBitOverride: null, isExpedited: true,  tx))),
            Ax25ActionVerb.ExpeditedDM            => Do(() => sendUFrame(BuildUFrame(UFrameType.Dm,    isCommand: false, pfBitOverride: null, isExpedited: true,  tx))),
            Ax25ActionVerb.SABM                   => Do(() => sendUFrame(BuildUFrame(UFrameType.Sabm,  isCommand: true,  pfBitOverride: true, isExpedited: false, tx))),
            Ax25ActionVerb.SABMPEqEq1             => Do(() => sendUFrame(BuildUFrame(UFrameType.Sabm,  isCommand: true,  pfBitOverride: true, isExpedited: false, tx))),
            Ax25ActionVerb.SABME                  => Do(() => sendUFrame(BuildUFrame(UFrameType.Sabme, isCommand: true,  pfBitOverride: true, isExpedited: false, tx))),
            Ax25ActionVerb.SABMEPEq1              => Do(() => sendUFrame(BuildUFrame(UFrameType.Sabme, isCommand: true,  pfBitOverride: true, isExpedited: false, tx))),
            Ax25ActionVerb.DISCPEq1               => Do(() => sendUFrame(BuildUFrame(UFrameType.Disc,  isCommand: true,  pfBitOverride: true, isExpedited: false, tx))),

            // ─── UI-frame transmissions ────────────────────────────────
            //
            // UI Command is drawn in every state's DL_UNIT_DATA_request
            // column. The payload + PID come from the triggering
            // DL_UNIT_DATA_request primitive; the P/F bit defaults to false
            // unless populated via tx.Pending.PfBit by a preceding verb.
            Ax25ActionVerb.UICommand              => Do(() => sendUiFrame(BuildUiFrame(isCommand: true, tx))),

            // ─── I-frame transmission ─────────────────────────────────
            //
            // I Command is figc4.4's signal_lower verb for I-frame emission.
            // The action chain preceding it (N(s):=V(s); N(r):=V(r); p:=0)
            // sets pending fields; this verb reads them along with the
            // payload + PID from the triggering IFramePopsOffQueue event.
            // The emitted I-frame's payload is also stored in
            // <see cref="Ax25SessionContext.SentIFrames"/> keyed by N(S), so
            // figc4.4's REJ/SREJ recovery paths can retrieve it via
            // Push Old I Frame N(r) on Queue.
            Ax25ActionVerb.ICommand               => Do(() => EmitIFrame(tx)),

            // ─── DL upper-layer signals (signal_upper) ────────────────
            //
            // Pure event-out. Each verb maps to a DataLinkSignal record
            // forwarded via sendUpward to the upper-layer consumer.
            // DL_DATA_indication carries the triggering I-frame's Info and
            // Pid; the simple ones have no payload.
            Ax25ActionVerb.DLCONNECTIndication    => Do(() => sendUpward(new DataLinkConnectIndication())),
            Ax25ActionVerb.DLCONNECTConfirm       => Do(() => sendUpward(new DataLinkConnectConfirm())),
            Ax25ActionVerb.DLDISCONNECTIndication => Do(() => sendUpward(new DataLinkDisconnectIndication())),
            Ax25ActionVerb.DLDISCONNECTConfirm    => Do(() => sendUpward(new DataLinkDisconnectConfirm())),
            Ax25ActionVerb.DLDATAIndication       => Do(() => sendUpward(BuildDataIndication(tx))),
            // DL_UNIT_DATA_indication delivers a received UI frame's payload
            // upward. Previously the case label was left in display form so
            // the normalised verb fell through to the default throw — every
            // UI reception (UI_Check → DL-UNIT-DATA Indication) crashed
            // (m0lte/packet.net#258). The typed verb makes that impossible.
            Ax25ActionVerb.DLUNITDATAIndication   => Do(() => sendUpward(new DataLinkUnitDataIndication(ExtractIncomingInfo(tx), ExtractIncomingPid(tx)))),

            // DL_ERROR_indication_* — per §C5 error-code letter table. The
            // letter (or the C_D pair / the (add) annotation) is surfaced as
            // the error code. The §C5 forms (C_D, D, E, F, G, I, K, L, M, N,
            // O, T, U) and the figc4.7 verbatim forms (A, J, K, Q, add)
            // collapse onto distinct members per letter.
            Ax25ActionVerb.DLERRORIndicationCD    => Do(() => sendUpward(new DataLinkErrorIndication("C_D"))),
            Ax25ActionVerb.DLERRORIndicationD     => Do(() => sendUpward(new DataLinkErrorIndication("D"))),
            Ax25ActionVerb.DLERRORIndicationE     => Do(() => sendUpward(new DataLinkErrorIndication("E"))),
            Ax25ActionVerb.DLERRORIndicationF     => Do(() => sendUpward(new DataLinkErrorIndication("F"))),
            Ax25ActionVerb.DLERRORIndicationG     => Do(() => sendUpward(new DataLinkErrorIndication("G"))),
            Ax25ActionVerb.DLERRORIndicationI     => Do(() => sendUpward(new DataLinkErrorIndication("I"))),
            Ax25ActionVerb.DLERRORIndicationK     => Do(() => sendUpward(new DataLinkErrorIndication("K"))),
            Ax25ActionVerb.DLERRORIndicationL     => Do(() => sendUpward(new DataLinkErrorIndication("L"))),
            Ax25ActionVerb.DLERRORIndicationM     => Do(() => sendUpward(new DataLinkErrorIndication("M"))),
            Ax25ActionVerb.DLERRORIndicationN     => Do(() => sendUpward(new DataLinkErrorIndication("N"))),
            Ax25ActionVerb.DLERRORIndicationO     => Do(() => sendUpward(new DataLinkErrorIndication("O"))),
            Ax25ActionVerb.DLERRORIndicationT     => Do(() => sendUpward(new DataLinkErrorIndication("T"))),
            Ax25ActionVerb.DLERRORIndicationU     => Do(() => sendUpward(new DataLinkErrorIndication("U"))),
            Ax25ActionVerb.DLERRORIndicationA     => Do(() => sendUpward(new DataLinkErrorIndication("A"))),
            Ax25ActionVerb.DLERRORIndicationJ     => Do(() => sendUpward(new DataLinkErrorIndication("J"))),
            Ax25ActionVerb.DLERRORIndicationQ     => Do(() => sendUpward(new DataLinkErrorIndication("Q"))),
            Ax25ActionVerb.DLERRORIndicationAdd   => Do(() => sendUpward(new DataLinkErrorIndication("add"))),

            // ─── Link-multiplexer signals (signal_lower) ──────────────
            //
            // The LM_*_request verbs go to the link multiplexer (the
            // medium-access arbiter), not directly to the radio. See
            // figc4.4 t53–t59 for the seize/release/data flow that
            // implements P-persistence on the channel.
            Ax25ActionVerb.LMSeizeRequest         => Do(() => sendLinkMux(new LinkMultiplexerSeizeRequest())),
            Ax25ActionVerb.LMReleaseRequest       => Do(() => sendLinkMux(new LinkMultiplexerReleaseRequest())),
            Ax25ActionVerb.LMDataRequest          => Do(() => sendLinkMux(new LinkMultiplexerDataRequest())),

            // ─── Internal-out signals ──────────────────────────────────
            //
            // MDL_NEGOTIATE_request triggers the management data-link to
            // start XID negotiation with the peer (figc4.6). The push_*
            // verbs enqueue an I-frame onto the internal TX queue, which the
            // session pops via I_frame_pops_off_queue events. The push-fresh
            // family (Push on I Frame Queue and its word-order / spelling
            // variants) all enqueue from the triggering DL_DATA_request.
            Ax25ActionVerb.MDLNEGOTIATERequest    => Do(() => sendInternal(new MdlNegotiateRequestSignal())),
            Ax25ActionVerb.PushOnIFrameQueue      => Do(() => PushOnIFrameQueue(tx)),
            Ax25ActionVerb.PushOnIFrameQueueNoteWordOrder => Do(() => PushOnIFrameQueue(tx)),
            Ax25ActionVerb.PushFrameOnQueue       => Do(() => PushOnIFrameQueue(tx)),
            Ax25ActionVerb.PushIFrameOnIQueue     => Do(() => PushOnIFrameQueue(tx)),
            // Push Old I Frame N(r) on Queue (figc4.4 REJ/SREJ retransmit):
            // re-emit the previously-sent I-frame at the incoming N(R).
            Ax25ActionVerb.PushOldIFrameNROnQueue => Do(() => PushOldIFrameNrOnQueue(tx)),

            // ─── Processing verbs (queue / storage / flags / counters) ─
            //
            // Multiple "discard queue" spellings exist across figures
            // (discard_frame_queue, discard_queue, discard_I_frame_queue,
            // Discard I Queue Entries) — all clear the I-frame transmit
            // queue.
            Ax25ActionVerb.DiscardFrameQueue      => Do(() => ctx.IFrameQueue.Clear()),
            Ax25ActionVerb.DiscardQueue           => Do(() => ctx.IFrameQueue.Clear()),
            Ax25ActionVerb.DiscardIFrameQueue     => Do(() => ctx.IFrameQueue.Clear()),
            Ax25ActionVerb.DiscardIQueueEntries   => Do(() => ctx.IFrameQueue.Clear()),

            // discard_I_frame / discard_contents_of_I_frame drop the current
            // incoming frame's payload — explicit "we are not delivering this
            // upward". No context mutation; we just don't fire a
            // DL_DATA_indication for this trigger.
            Ax25ActionVerb.DiscardIFrame          => Do(() => { /* no-op: incoming not stored anywhere */ }),
            Ax25ActionVerb.DiscardContentsOfIFrame => Do(() => { /* no-op: incoming not stored anywhere */ }),

            // discard_primitive is the catch-all "drop the trigger and do
            // nothing" verb (figc4.1 t06: all-other-primitives-from-upper-
            // layer column).
            Ax25ActionVerb.DiscardPrimitive       => Do(() => { /* no-op */ }),

            // SREJ exception bookkeeping. SrejExceptionCount tracks the number
            // of outstanding SREJ exceptions per §C4.3. The
            // SelectiveRejectException bool flag is set when the count
            // transitions 0 → 1+ and cleared on 1+ → 0. The figc4.7 title-case
            // "Clear Sreject Condition" clears both.
            Ax25ActionVerb.SetRejectException     => Do(() => ctx.RejectException = true),
            Ax25ActionVerb.ClearRejectException   => Do(() => ctx.RejectException = false),
            Ax25ActionVerb.ClearRejectCondition   => Do(() => ctx.RejectException = false),
            Ax25ActionVerb.ClearSrejectCondition  => Do(() =>
            {
                ctx.SelectiveRejectException = false;
                ctx.SrejExceptionCount = 0;
            }),
            Ax25ActionVerb.IncrementSrejectException => Do(() =>
            {
                ctx.SrejExceptionCount++;
                ctx.SelectiveRejectException = true;
            }),
            Ax25ActionVerb.DecrementSrejectExceptionIf0 => Do(() =>
            {
                if (ctx.SrejExceptionCount > 0)
                {
                    ctx.SrejExceptionCount--;
                    if (ctx.SrejExceptionCount == 0) ctx.SelectiveRejectException = false;
                }
            }),
            // Sreject := Sreject + 1 (figc4.x SREJ-exception spelling variant) —
            // same bookkeeping as increment_srej_exception.
            Ax25ActionVerb.SrejectAssignSrejectPlus1 => Do(() =>
            {
                ctx.SrejExceptionCount++;
                ctx.SelectiveRejectException = true;
            }),

            // Out-of-sequence I-frame storage. Save Contents of I Frame stashes
            // the trigger I-frame's Info + Pid keyed by its N(S); Retrieve
            // Stored V(r) I Frame pulls back the frame whose N(S) == V(r) (the
            // next expected) when V(r) advances and stages it for delivery.
            Ax25ActionVerb.SaveContentsOfIFrame   => Do(() => SaveIncomingIFrame(tx)),
            Ax25ActionVerb.RetrieveStoredVRIFrame => Do(() => RetrieveStoredVrIFrame(tx)),

            // ─── Set_Version_2_0 / Set_Version_2_2 (modulus + link params) ─
            //
            // set_version_2_0 / Set Version 2.2 are the figc4.1 / figc4.4
            // SABM(E) processing verbs that lock in mod-8 vs mod-128 once the
            // peer accepts. They are emitted as Processing-kind verbs (not
            // subroutine calls) by the state tables.
            Ax25ActionVerb.SetVersion20           => Do(() => ctx.IsExtended = false),
            Ax25ActionVerb.SetVersion22           => Do(() => ctx.IsExtended = true),

            // Individual Set_Version_2_x body verbs (figc4.7), used inside the
            // subroutine bodies via the walker.
            Ax25ActionVerb.SetHalfDuplex          => Do(() => ctx.HalfDuplex = true),
            Ax25ActionVerb.SetImplicitReject      => Do(() =>
            {
                ctx.ImplicitReject = true;
                ctx.SrejEnabled = false;
            }),
            Ax25ActionVerb.SetSelectiveReject     => Do(() =>
            {
                ctx.ImplicitReject = false;
                ctx.SrejEnabled = true;
            }),
            Ax25ActionVerb.ModuloAssign8          => Do(() => ctx.IsExtended = false),
            Ax25ActionVerb.ModuloAssign128        => Do(() => ctx.IsExtended = true),
            Ax25ActionVerb.N1Assign2048           => Do(() => ctx.N1 = 2048),
            Ax25ActionVerb.KAssign8               => Do(() => ctx.K = 8),
            Ax25ActionVerb.KAssign32              => Do(() => ctx.K = 32),
            Ax25ActionVerb.T2Assign3000           => Do(() => ctx.T2 = TimeSpan.FromMilliseconds(3000)),
            Ax25ActionVerb.N2Assign10             => Do(() => ctx.N2 = 10),

            // ─── Link-parameter assignments (SRT, T1V) ────────────────
            //
            // Smoothed Round-Trip Time and T1 timeout value per §6.7.1. The
            // dispatcher's <see cref="T1Duration"/> property is the *initial*
            // T1V for new sessions; ctx.T1V is the *current* per-session value
            // mutated by these verbs and (in production) by RTT smoothing in
            // figc4.7's Select_T1_Value subroutine. T1V := 2 * SRT and the
            // figc4.7 Next T1 := 2 * SRT spelling fold to distinct members.
            Ax25ActionVerb.SRTAssignInitialDefault => Do(() => ctx.Srt = TimeSpan.FromMilliseconds(3000)),
            Ax25ActionVerb.T1VAssign2TimesSRT     => Do(() => ctx.T1V = ctx.Srt + ctx.Srt),
            Ax25ActionVerb.NextT1Assign2TimesSRT  => Do(() => ctx.T1V = ctx.Srt * 2),
            Ax25ActionVerb.NextT1AssignRCTimes025PlusSRTTimes2 => Do(() =>
            {
                ctx.T1V = TimeSpan.FromMilliseconds(ctx.RC * 250 + ctx.Srt.TotalMilliseconds * 2);
                ctx.T1HadExpired = false;
            }),
            // Select_T1_Value IIR (figc4.7). The SRT formula is figure-verbatim;
            // the new-sample term is (T1V - remaining_when_stopped): the elapsed
            // portion of T1 from arm to stop. The sample is a valid round-trip
            // ONLY when T1 was stopped by an acknowledgement of the frame that
            // armed it — i.e. it was running, so remaining > 0. On a
            // timeout/retransmit remaining is 0, the sample degenerates to the
            // full T1V (= 2·SRT), and since T1V derives from SRT the IIR
            // self-amplifies (SRT' = 1.125·SRT) → unbounded growth → overflow.
            // Karn's algorithm: skip the update when there is no clean
            // measurement. packethacking/ax25spec#41; gated behind
            // Ax25Spec41KarnSrtSampling (m0lte/packet.net#241).
            Ax25ActionVerb.SRTAssign7SRT8PlusT18RemainingTimeOnT1WhenLastStopped8 => Do(() =>
            {
                var sample = ctx.T1V - ctx.T1RemainingWhenLastStopped;
                if (sample < TimeSpan.Zero) sample = TimeSpan.Zero;
                bool cleanMeasurement = ctx.T1RemainingWhenLastStopped > TimeSpan.Zero;
                if (!ctx.Quirks.Ax25Spec41KarnSrtSampling || cleanMeasurement)
                {
                    ctx.Srt = TimeSpan.FromMilliseconds(
                        0.875 * ctx.Srt.TotalMilliseconds + 0.125 * sample.TotalMilliseconds);
                }
                ctx.T1HadExpired = false;
                ctx.T1RemainingWhenLastStopped = TimeSpan.Zero;
            }),

            // ─── Subroutine calls ──────────────────────────────────────
            //
            // Each routes through the registry. DefaultSubroutineRegistry
            // pre-populates with no-op stubs for every known name, upgraded to
            // figc4.7-walking implementations by Wire(). Tests can register
            // custom impls. The (F = 0)/(F = 1) Enquiry_Response variants and
            // the legacy N(r) Recovery / Select_T1_Value spellings map onto the
            // registry's alias names; Establish_Extended_Data_Link is no longer
            // emitted as a verb (the codegen folds it away) so it isn't dispatched
            // here, only walked by name from within a subroutine body.
            Ax25ActionVerb.EstablishDataLink      => Do(() => subroutines.Invoke("Establish_Data_Link", tx)),
            // Clear Exception Conditions appears both as a subroutine call and as
            // a multi-line processing box (figc4.7's Establish_Data_Link draws it
            // inline). Both collapse to this member; routing through the registry
            // runs the canonical six-clear body either way (clear peer/own RX
            // busy, clear reject + sreject conditions, clear ack pending, discard
            // I-queue entries).
            Ax25ActionVerb.ClearExceptionConditions => Do(() => subroutines.Invoke("Clear_Exception_Conditions", tx)),
            Ax25ActionVerb.UICheck                => Do(() => subroutines.Invoke("UI_Check", tx)),
            Ax25ActionVerb.SelectT1Value          => Do(() => subroutines.Invoke("Select_T1_Value", tx)),
            Ax25ActionVerb.CheckIFrameAcknowledged => Do(() => subroutines.Invoke("Check_I_Frame_Acknowledged", tx)),
            Ax25ActionVerb.CheckNeedForResponse   => Do(() => subroutines.Invoke("Check_Need_For_Response", tx)),
            Ax25ActionVerb.TransmitEnquiry        => Do(() => subroutines.Invoke("Transmit_Enquiry", tx)),
            // "Transmit Enquery" is the figure's typo spelling; same subroutine.
            Ax25ActionVerb.TransmitEnquery        => Do(() => subroutines.Invoke("Transmit_Enquiry", tx)),
            Ax25ActionVerb.InvokeRetransmission   => Do(() => subroutines.Invoke("Invoke_Retransmission", tx)),
            Ax25ActionVerb.NRErrorRecovery        => Do(() => subroutines.Invoke("N_r_Error_Recovery", tx)),
            // "N(r) Recovery" is a figure spelling variant of N(r) Error Recovery.
            Ax25ActionVerb.NRRecovery             => Do(() => subroutines.Invoke("N_r_Error_Recovery", tx)),
            Ax25ActionVerb.EnquiryResponseFEq0    => Do(() => subroutines.Invoke("Enquiry_Response_F_0", tx)),
            Ax25ActionVerb.EnquiryResponseF1      => Do(() => subroutines.Invoke("Enquiry_Response_F_1", tx)),

            // ─── Sequence-variable assignments (pure context) ──────────
            //
            // The figure-canonical lowercase forms (V(s), V(r), V(a)) and the
            // figc4.7 arrow forms (X <- V(s), V(s) <- N(r), …) collapse to these
            // members.
            Ax25ActionVerb.VSAssign0              => Do(() => ctx.VS = 0),
            Ax25ActionVerb.VSAssignVSPlus1        => Do(() => ctx.VS = ctx.IncrementSeq(ctx.VS)),
            Ax25ActionVerb.VRAssign0              => Do(() => ctx.VR = 0),
            Ax25ActionVerb.VRAssignVRPlus1        => Do(() => ctx.VR = ctx.IncrementSeq(ctx.VR)),
            // figc4.5 Timer Recovery draws the stored-frame drain with
            // V(r) := V(r) - 1. The decrement is surprising for a drain and is
            // flagged for spec-author confirmation (ax25sdl#49); encoded
            // faithfully here pending that review.
            Ax25ActionVerb.VRAssignVR1            => Do(() => ctx.VR = ctx.DecrementSeq(ctx.VR)),
            Ax25ActionVerb.VAAssign0              => Do(() => ctx.VA = 0),
            Ax25ActionVerb.XAssignVS              => Do(() => ctx.X = ctx.VS),
            Ax25ActionVerb.VSAssignNR             => Do(() => ctx.VS = ExtractNr(tx)),
            Ax25ActionVerb.RCAssign0              => Do(() => ctx.RC = 0),
            Ax25ActionVerb.RCAssign1              => Do(() => ctx.RC = 1),
            Ax25ActionVerb.RCAssignRCPlus1        => Do(() => ctx.RC++),

            // ─── Sequence-variable assignments (reads from incoming frame) ─
            //
            // V(a) := N(r) advances the acknowledge state to the N(R) of the
            // just-received frame. Only valid when the trigger is a frame-receipt
            // event; throws otherwise. The figc4.7 arrow form folds here too.
            Ax25ActionVerb.VAAssignNR             => Do(() => ctx.VA = ExtractNr(tx)),

            // ─── Pending-frame field assignments (write side) ──────────
            //
            // These populate <see cref="TransitionContext.Pending"/>; the
            // signal_lower verb that follows in the same chain consumes pending
            // state to build a complete outgoing frame spec. For F := P /
            // N(r) := N(s) the right-hand side is a field of the triggering
            // incoming frame; the helper throws cleanly if the trigger doesn't
            // carry one. For constant assignments no incoming frame is required.
            //
            // P / F / p spellings (and figc4.5's P := 0/1 for the poll-bit on the
            // frame being sent) are the same operation: the runtime stores both
            // bits in the single PfBit field on Pending. Inbound frames
            // distinguish P vs F via IsCommand; outbound frames set the bit
            // unilaterally regardless of nomenclature.
            Ax25ActionVerb.NRAssignVR             => Do(() => tx.Pending.Nr = ctx.VR),
            Ax25ActionVerb.NSAssignVS             => Do(() => tx.Pending.Ns = ctx.VS),
            Ax25ActionVerb.NRAssignNS             => Do(() => tx.Pending.Nr = ExtractNs(tx)),
            Ax25ActionVerb.FAssign0               => Do(() => tx.Pending.PfBit = false),
            Ax25ActionVerb.FAssign1               => Do(() => tx.Pending.PfBit = true),
            Ax25ActionVerb.FAssignP               => Do(() => tx.Pending.PfBit = ExtractPollFinal(tx)),
            Ax25ActionVerb.PAssign0               => Do(() => tx.Pending.PfBit = false),
            Ax25ActionVerb.PAssign1               => Do(() => tx.Pending.PfBit = true),

            // ─── Invoke_Retransmission body markers ────────────────────
            //
            // Backtrack is an informational marker in figc4.7's
            // Invoke_Retransmission loop (no state effect).
            Ax25ActionVerb.Backtrack              => Do(() => { /* informational marker */ }),
            // Push Old I Frame onto Queue (the Invoke_Retransmission loop body):
            // figc4.7 rewinds V(s):=N(r) and re-sends each stored frame as V(s)
            // climbs back to X. Each must go out with its ORIGINAL N(s) (= the
            // current rewound V(s)); emit it directly rather than enqueue, because
            // the fresh-frame drain (figc4.4 t03 "I frame pops off queue") would
            // renumber it to the post-loop V(s) (=X). See EmitOldIFrame.
            Ax25ActionVerb.PushOldIFrameOntoQueue => Do(() => EmitOldIFrame(tx, ctx.VS)),
        };
#pragma warning restore CS8524
    }

    /// <summary>
    /// Run a side-effecting action arm and yield a value, so a switch
    /// <em>expression</em> (which requires every arm to produce a result and
    /// thereby gives compile-time exhaustiveness) can wrap the dispatcher's
    /// void-returning verb handlers. The returned value is always discarded.
    /// </summary>
    private static bool Do(Action action)
    {
        action();
        return true;
    }

    /// <summary>
    /// Read the N(R) field from the incoming frame's control byte. Assumes
    /// mod-8 (1-byte control) for now; mod-128 needs the 2-byte extended
    /// control form which Ax25Frame doesn't model yet.
    /// </summary>
    private static byte ExtractNr(TransitionContext tx)
    {
        var frame = RequireIncomingFrame(tx, "V(a) := N(r)");
        RequireMod8(tx, "N(R)");
        // mod-8: bits 7..5 of the 1-byte control field carry N(R) per §4.2.2.
        return (byte)((frame.Control >> 5) & 0x07);
    }

    /// <summary>
    /// Read the N(S) field from the incoming I-frame's control byte. Only
    /// I-frames carry N(S); on an S-frame the same bits encode the S type
    /// + P/F, so this returns a meaningless value if called against the
    /// wrong frame type. The caller (the SDL transcription) decides when
    /// it's valid to invoke <c>N(r) := N(s)</c>.
    /// </summary>
    private static byte ExtractNs(TransitionContext tx)
    {
        var frame = RequireIncomingFrame(tx, "N(r) := N(s)");
        RequireMod8(tx, "N(S)");
        // mod-8 I-frame: control = (N(R) << 5) | (P << 4) | (N(S) << 1) | 0.
        return (byte)((frame.Control >> 1) & 0x07);
    }

    /// <summary>
    /// Read the P/F bit (bit 4 of the control byte, mod-8) from the
    /// incoming frame. Used by <c>F := P</c> to echo the peer's poll bit
    /// back in the final.
    /// </summary>
    private static bool ExtractPollFinal(TransitionContext tx)
    {
        var frame = RequireIncomingFrame(tx, "F := P");
        // The P/F bit lives at bit 4 in both mod-8 and mod-128 control
        // fields, so no extended-mode gate is needed here.
        return frame.PollFinal;
    }

    /// <summary>Information field of the triggering frame. Throws if no frame attached.</summary>
    private static ReadOnlyMemory<byte> ExtractIncomingInfo(TransitionContext tx)
        => RequireIncomingFrame(tx, "DL-UNIT-DATA Indication").Info;

    /// <summary>PID byte of the triggering frame, or 0xF0 (no L3) if absent.</summary>
    private static byte ExtractIncomingPid(TransitionContext tx)
        => RequireIncomingFrame(tx, "DL-UNIT-DATA Indication").Pid ?? Ax25Frame.PidNoLayer3;

    /// <summary>
    /// Build a <see cref="SupervisoryFrameSpec"/> from the transition's
    /// <see cref="PendingFrame"/>, applying spec-implicit defaults for any
    /// field not explicitly set by a preceding processing verb.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The AX.25 SDL figures don't always populate N(R) and F/P before
    /// every <c>signal_lower</c> verb. For example, figc4.4 t23
    /// (DL_FLOW_OFF, own_receiver_busy=Yes) draws bare
    /// <c>set_own_receiver_busy; RNR_response; clear_acknowledge_pending</c>
    /// with no N(R) or F-bit setup beforehand — the spec assumes the
    /// implementation fills in <c>N(R) = V(R)</c> (current receive state)
    /// and <c>F = 0</c> implicitly. Other transitions are explicit when
    /// they need a specific value (e.g. <c>F := 1; N(r) := V(r); RR</c>
    /// for response-with-poll-final-set).
    /// </para>
    /// <para>
    /// Defaults applied here: <c>Nr</c> falls back to
    /// <see cref="Ax25SessionContext.VR"/>; <c>PfBit</c> falls back to
    /// <c>false</c>. These match the spec's implicit "no special value
    /// requested" semantics.
    /// </para>
    /// </remarks>
    private static SupervisoryFrameSpec BuildSFrame(
        SupervisoryFrameType type, bool isCommand, TransitionContext tx)
    {
        byte nr     = tx.Pending.Nr    ?? tx.Session.VR;
        bool pfBit  = tx.Pending.PfBit ?? false;
        return new SupervisoryFrameSpec(type, IsCommand: isCommand, Nr: nr, PfBit: pfBit, Path: ReversedTriggerPath(tx));
    }

    /// <summary>
    /// Build a <see cref="UFrameSpec"/> for a U-frame signal_lower verb.
    /// Reads <see cref="PendingFrame.PfBit"/> for bare verbs, or forces
    /// it when the verb name carries an explicit qualifier
    /// (<c>SABM (P == 1)</c>, <c>SABME (P = 1)</c>, <c>DISC (P = 1)</c>,
    /// <c>DM (F = 1)</c>).
    /// </summary>
    /// <param name="pfBitOverride">
    /// <c>null</c> to read <see cref="PendingFrame.PfBit"/> (default
    /// <c>false</c> if unset); non-null to force the P/F bit value
    /// regardless of pending state.
    /// </param>
    private static UFrameSpec BuildUFrame(
        UFrameType type, bool isCommand, bool? pfBitOverride, bool isExpedited, TransitionContext tx)
    {
        bool pfBit = pfBitOverride ?? tx.Pending.PfBit ?? false;
        return new UFrameSpec(
            type,
            IsCommand: isCommand,
            PfBit: pfBit,
            IsExpedited: isExpedited,
            Path: ReversedTriggerPath(tx));
    }

    /// <summary>
    /// Implement <c>push_on_I_frame_queue</c> / <c>push_frame_on_queue</c>:
    /// pull the payload + PID from the triggering <see cref="DlDataRequest"/>
    /// and append it to <see cref="Ax25SessionContext.IFrameQueue"/>.
    /// Also raises the corresponding <see cref="InternalSignal"/> so
    /// observers can see the push.
    /// </summary>
    private void PushOnIFrameQueue(TransitionContext tx)
    {
        if (tx.Trigger is not DlDataRequest dr)
        {
            throw new InvalidOperationException(
                $"action `push_*_queue` requires the trigger to be DL_DATA_request, but it was '{tx.Trigger.Name}'.");
        }
        tx.Session.IFrameQueue.Enqueue((dr.Data, dr.Pid));
        sendInternal(new PushIFrameQueueSignal(dr.Data));
    }

    /// <summary>
    /// Implement <c>push_old_I_frame_N_r_on_queue</c>: retransmit the
    /// previously-sent I-frame whose N(S) equals the incoming frame's N(R).
    /// Used in figc4.4/figc4.5's selective SREJ/REJ recovery paths.
    /// </summary>
    private void PushOldIFrameNrOnQueue(TransitionContext tx)
        => EmitOldIFrame(tx, ExtractNr(tx));

    /// <summary>
    /// Retransmit a previously-sent I-frame, preserving its ORIGINAL N(S).
    /// Used by figc4.4's selective SREJ/REJ recovery
    /// (<c>push_old_I_frame_N_r_on_queue</c>) and figc4.7's go-back-N
    /// <c>Invoke_Retransmission</c> loop (<c>Push Old I Frame onto Queue</c>).
    /// </summary>
    /// <remarks>
    /// Emits directly rather than via <see cref="Ax25SessionContext.IFrameQueue"/>:
    /// the queue + fresh-frame drain (figc4.4 t03 "I frame pops off queue")
    /// assigns <c>N(S):=V(S)</c> and increments V(S) — correct for a *fresh*
    /// frame, but it renumbers a *retransmitted* one to the current V(S), so the
    /// peer never sees the missing sequence number and the gap never fills (the
    /// figure assumes the push + transmit interleave; the runtime decoupled them
    /// into push-now / drain-later, losing the N(S) semantics). Retransmits also
    /// go out unconditionally — they are already-counted frames being replayed,
    /// not new transmissions subject to the send window. N(S) is the supplied
    /// original sequence; N(R) piggybacks the current V(R); P=0 (the poll, when
    /// needed, is a separate enquiry). Silently skips if the frame has been
    /// evicted from storage — matches linbpq/direwolf.
    /// </remarks>
    private void EmitOldIFrame(TransitionContext tx, byte ns)
    {
        if (!tx.Session.SentIFrames.TryGetValue(ns, out var entry)) return;
        sendIFrame(new IFrameSpec(
            IsCommand: true,
            PBit:      false,
            Nr:        tx.Session.VR,
            Ns:        ns,
            Info:      entry.Data,
            Pid:       entry.Pid,
            Path:      ReversedTriggerPath(tx)));
    }

    /// <summary>
    /// Implement <c>I_command</c>: build an <see cref="IFrameSpec"/>
    /// from pending fields + the triggering <see cref="IFramePopsOffQueue"/>
    /// event's payload, emit it via <c>sendIFrame</c>, and stash it in
    /// <see cref="Ax25SessionContext.SentIFrames"/> for potential
    /// retransmission.
    /// </summary>
    private void EmitIFrame(TransitionContext tx)
    {
        if (tx.Trigger is not IFramePopsOffQueue popped)
        {
            throw new InvalidOperationException(
                $"action `I_command` requires the trigger to be I_frame_pops_off_queue, but it was '{tx.Trigger.Name}'.");
        }
        byte ns   = tx.Pending.Ns    ?? tx.Session.VS;
        byte nr   = tx.Pending.Nr    ?? tx.Session.VR;
        bool pBit = tx.Pending.PfBit ?? false;
        sendIFrame(new IFrameSpec(
            IsCommand: true,
            PBit:      pBit,
            Nr:        nr,
            Ns:        ns,
            Info:      popped.Data,
            Pid:       popped.Pid,
            Path:      ReversedTriggerPath(tx)));
        tx.Session.SentIFrames[ns] = (popped.Data, popped.Pid);
    }

    /// <summary>
    /// Implement <c>save_contents_of_I_frame</c>: store the triggering
    /// I-frame's <c>Info</c> + <c>Pid</c> keyed by its N(S), so a later
    /// <c>retrieve_stored_V_r_I_frame</c> can pull it back when V(r)
    /// advances to that seqno.
    /// </summary>
    private static void SaveIncomingIFrame(TransitionContext tx)
    {
        var frame = RequireIncomingFrame(tx, "save_contents_of_I_frame");
        RequireMod8(tx, "N(S)");
        byte ns = (byte)((frame.Control >> 1) & 0x07);
        if (frame.Pid is not byte pid)
        {
            throw new InvalidOperationException(
                "action `save_contents_of_I_frame` requires an I-frame trigger (which carries PID), but the frame has no PID.");
        }
        tx.Session.StoredReceivedIFrames[ns] = (frame.Info, pid);
    }

    /// <summary>
    /// Implement <c>retrieve_stored_V_r_I_frame</c>: pull the stored I-frame
    /// whose N(S) equals the session's current V(R), remove it from storage,
    /// and <em>stage</em> it on <see cref="TransitionContext.RetrievedStoredFrame"/>
    /// for the next <c>DL-DATA Indication</c> in the chain to deliver.
    /// </summary>
    /// <remarks>
    /// The figc4.4 / figc4.5 stored-frame drain loop draws retrieval and
    /// delivery as two separate actions (Retrieve, then DL-DATA Indication),
    /// so this stages rather than delivers — otherwise the loop body's own
    /// DL-DATA Indication would double-deliver. If no stored frame matches,
    /// this is a no-op (matching linbpq / direwolf).
    /// </remarks>
    private static void RetrieveStoredVrIFrame(TransitionContext tx)
    {
        if (tx.Session.StoredReceivedIFrames.Remove(tx.Session.VR, out var stored))
        {
            tx.RetrievedStoredFrame = (stored.Info, stored.Pid);
        }
    }

    /// <summary>
    /// Build a <see cref="DataLinkDataIndication"/> from the triggering
    /// I-frame. Reads <see cref="Ax25Frame.Info"/> and <see cref="Ax25Frame.Pid"/>
    /// from the incoming frame; throws if the trigger isn't a frame-receipt
    /// event or the frame doesn't carry a PID.
    /// </summary>
    /// <remarks>
    /// In figc4.4 some paths fire <c>DL_DATA_indication</c> after a
    /// <c>retrieve_stored_V_r_I_frame</c> verb, which dequeues a
    /// previously-saved out-of-sequence frame. That delivery path needs
    /// to plumb the stored frame's Info/Pid through — not yet wired,
    /// because <c>retrieve_stored_V_r_I_frame</c> itself isn't wired.
    /// The current implementation only handles the simple case (deliver
    /// the trigger frame's Info/Pid).
    /// </remarks>
    private static DataLinkDataIndication BuildDataIndication(TransitionContext tx)
    {
        // Inside the stored-frame drain loop, a preceding
        // `Retrieve Stored V(r) I Frame` stages the frame to deliver here;
        // consume it. Outside the loop, deliver the triggering frame.
        if (tx.RetrievedStoredFrame is { } staged)
        {
            tx.RetrievedStoredFrame = null;
            return new DataLinkDataIndication(staged.Info, staged.Pid);
        }

        var frame = RequireIncomingFrame(tx, "DL_DATA_indication");
        if (frame.Pid is not byte pid)
        {
            throw new InvalidOperationException(
                "action `DL_DATA_indication` requires the incoming frame to carry a PID octet, " +
                "but the frame has none (only I and UI frames have PID).");
        }
        return new DataLinkDataIndication(frame.Info, pid);
    }

    /// <summary>
    /// Build a <see cref="UiFrameSpec"/> for the <c>UI_command</c> verb.
    /// Payload + PID come from the triggering
    /// <see cref="DlUnitDataRequest"/>; P/F bit consumes
    /// <see cref="PendingFrame.PfBit"/> (default false). Throws if the
    /// trigger isn't a DL-UNIT-DATA request.
    /// </summary>
    private static UiFrameSpec BuildUiFrame(bool isCommand, TransitionContext tx)
    {
        if (tx.Trigger is not DlUnitDataRequest dud)
        {
            throw new InvalidOperationException(
                $"action `UI_command` requires the trigger to be DL_UNIT_DATA_request, but it was '{tx.Trigger.Name}'.");
        }
        bool pfBit = tx.Pending.PfBit ?? false;
        return new UiFrameSpec(
            IsCommand: isCommand,
            PfBit: pfBit,
            Info: dud.Data,
            Pid: dud.Pid,
            Path: ReversedTriggerPath(tx));
    }

    private static Ax25Frame RequireIncomingFrame(TransitionContext tx, string verb)
    {
        return tx.IncomingFrame
            ?? throw new InvalidOperationException(
                $"action `{verb}` requires an incoming frame, but the trigger '{tx.Trigger.Name}' is not a frame-receipt event.");
    }

    private static void RequireMod8(TransitionContext tx, string fieldName)
    {
        if (tx.Session.IsExtended)
        {
            throw new NotSupportedException(
                $"extracting {fieldName} from an extended (mod-128) frame's 2-byte control field is not yet implemented; only mod-8 is wired today.");
        }
    }

    /// <summary>
    /// Return the trigger's inbound digipeater chain reversed, or
    /// <c>null</c> when the trigger has no inbound frame or the chain is
    /// empty. The wire-translation layer treats <c>null</c> as "use the
    /// session context's <see cref="Ax25SessionContext.Digipeaters"/>";
    /// non-null overrides it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Implements AX.25 v2.2 §C.2 (Path Construction): a response to a
    /// digipeated frame is sent along the reversed inbound chain so the
    /// digipeater closest to the responder is the first hop. For example,
    /// inbound SABM via <c>[GB7CIP, MB7UR]</c> (source-to-destination
    /// transmission order) → outbound UA via <c>[MB7UR, GB7CIP]</c>.
    /// </para>
    /// <para>
    /// Returns <c>null</c> (rather than an empty list) when the trigger
    /// has no inbound frame OR the inbound frame has no digipeaters —
    /// this lets the wire-translation layer fall back to the context's
    /// <see cref="Ax25SessionContext.Digipeaters"/> for direct links and
    /// for outbound-initiated frames (SABM via our own configured digi
    /// chain), and keeps spec-record equality holding for tests that
    /// build specs without a Path override.
    /// </para>
    /// </remarks>
    private static Callsign[]? ReversedTriggerPath(TransitionContext tx)
    {
        var frame = tx.IncomingFrame;
        if (frame is null || frame.Digipeaters.Count == 0)
        {
            return null;
        }
        var src = frame.Digipeaters;
        var reversed = new Callsign[src.Count];
        for (int i = 0; i < src.Count; i++)
        {
            reversed[i] = src[src.Count - 1 - i].Callsign;
        }
        return reversed;
    }
}
