using Packet.Ax25.Sdl;

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
    public void Execute(IEnumerable<string> actions, TransitionContext tx)
    {
        ArgumentNullException.ThrowIfNull(actions);
        ArgumentNullException.ThrowIfNull(tx);

        foreach (var action in actions)
        {
            Execute(action, tx);
        }
    }

    /// <summary>
    /// Test-ergonomics overload: execute a single verb with no triggering
    /// frame. Constructs a <see cref="TransitionContext"/> with a sentinel
    /// trigger that has no attached <see cref="Ax25Frame"/>; verbs that
    /// require <see cref="TransitionContext.IncomingFrame"/> (e.g.
    /// <c>V(a) := N(r)</c>) throw when invoked through this path.
    /// </summary>
    public void Execute(string action, Ax25SessionContext context, ITimerScheduler scheduler)
        => Execute(action, new TransitionContext(context, scheduler, SyntheticTrigger.Instance));

    /// <summary>
    /// Test-ergonomics overload: execute multiple verbs with no triggering
    /// frame. See <see cref="Execute(string, Ax25SessionContext, ITimerScheduler)"/>.
    /// </summary>
    public void Execute(IEnumerable<string> actions, Ax25SessionContext context, ITimerScheduler scheduler)
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
    /// Execute a single action verb. Throws on unknown actions so a typo
    /// in the SDL transcription doesn't get silently swallowed.
    /// </summary>
    public void Execute(string action, TransitionContext tx)
    {
        ArgumentNullException.ThrowIfNull(tx);

        var ctx = tx.Session;
        var scheduler = tx.Scheduler;

        switch (action)
        {
            // ─── Flag mutations ────────────────────────────────────────
            case "set_own_receiver_busy":          ctx.OwnReceiverBusy    = true;  break;
            case "clear_own_receiver_busy":        ctx.OwnReceiverBusy    = false; break;
            case "set_peer_receiver_busy":         ctx.PeerReceiverBusy   = true;  break;
            case "clear_peer_receiver_busy":       ctx.PeerReceiverBusy   = false; break;
            case "set_acknowledge_pending":        ctx.AcknowledgePending = true;  break;
            case "clear_acknowledge_pending":      ctx.AcknowledgePending = false; break;
            case "set_layer_3_initiated":          ctx.Layer3Initiated    = true;  break;
            case "clear_layer_3_initiated":        ctx.Layer3Initiated    = false; break;

            // ─── Timer operations ──────────────────────────────────────
            //
            // T1 uses the per-session <see cref="Ax25SessionContext.T1V"/>
            // value — refreshed dynamically by figc4.7's Select_T1_Value
            // subroutine and by figc4.1/4.2's link-establishment paths
            // (SRT := Initial Default; T1V := 2 * SRT). T2 and T3 stay on
            // the dispatcher's static defaults until per-session values
            // get wired (the SDL pages don't currently mutate them).
            case "start_T1":                       scheduler.Arm("T1", ctx.T1V,     () => onTimerExpiry("T1")); break;
            case "start_T2":                       scheduler.Arm("T2", T2Duration, () => onTimerExpiry("T2")); break;
            case "start_T3":                       scheduler.Arm("T3", T3Duration, () => onTimerExpiry("T3")); break;
            case "stop_T1":                        scheduler.Cancel("T1"); break;
            case "stop_T2":                        scheduler.Cancel("T2"); break;
            case "stop_T3":                        scheduler.Cancel("T3"); break;

            // ─── Queue operations ──────────────────────────────────────
            case "discard_i_frame_queue":          ctx.IFrameQueue.Clear(); break;

            // ─── Supervisory-frame transmissions ───────────────────────
            //
            // Verb spellings here are figure-canonical (`RR_command`,
            // `RR`, `RNR_response`, `REJ`, `SREJ`) — what figc4.4 draws.
            // The bare verbs (`RR`, `REJ`, `SREJ`) are response-form per
            // SDL convention; the `_command` / `_response` suffixed verbs
            // are explicit. Each emission consumes `tx.Pending.Nr` and
            // `tx.Pending.PfBit`, both of which must have been populated
            // by an earlier processing verb in the same chain.
            case "RR_command":                     sendSFrame(BuildSFrame(SupervisoryFrameType.Rr,   isCommand: true,  tx, action)); break;
            case "RR":                             sendSFrame(BuildSFrame(SupervisoryFrameType.Rr,   isCommand: false, tx, action)); break;
            case "RNR_response":                   sendSFrame(BuildSFrame(SupervisoryFrameType.Rnr,  isCommand: false, tx, action)); break;
            case "REJ":                            sendSFrame(BuildSFrame(SupervisoryFrameType.Rej,  isCommand: false, tx, action)); break;
            case "SREJ":                           sendSFrame(BuildSFrame(SupervisoryFrameType.Srej, isCommand: false, tx, action)); break;

            // ─── Unnumbered-frame transmissions ────────────────────────
            //
            // Verb spellings track the figure as drawn. Spellings with an
            // explicit poll/final qualifier (`SABM (P == 1)`,
            // `SABME (P = 1)`, `DISC (P = 1)`, `DM (F = 1)`) force the P/F
            // bit to 1 regardless of `tx.Pending`. Bare verbs (`UA`,
            // `DM`, `Expedited UA`, `Expedited DM`) consume
            // `tx.Pending.PfBit`, defaulting to false.
            //
            // The `DM (F = 1)` cluster (figc4.2 `DM F=1`, figc4.3
            // `DM F = 1`, figc4.6 `DM (F = 1)`) is normalised to the
            // parenthesised canonical by spec-sdl/actions.yaml.
            //
            // `Expedited UA` / `Expedited DM` from figc4.3 hint that the
            // frame should jump ahead of the normal TX queue. The bit
            // pattern on the wire is identical to UA / DM; the wire-
            // translation layer reads <see cref="UFrameSpec.IsExpedited"/>
            // to prioritise transmission.
            case "UA":                             sendUFrame(BuildUFrame(UFrameType.Ua,    isCommand: false, pfBitOverride: null, isExpedited: false, tx)); break;
            case "DM":                             sendUFrame(BuildUFrame(UFrameType.Dm,    isCommand: false, pfBitOverride: null, isExpedited: false, tx)); break;
            case "DM (F = 1)":                     sendUFrame(BuildUFrame(UFrameType.Dm,    isCommand: false, pfBitOverride: true, isExpedited: false, tx)); break;
            case "Expedited UA":                   sendUFrame(BuildUFrame(UFrameType.Ua,    isCommand: false, pfBitOverride: null, isExpedited: true,  tx)); break;
            case "Expedited DM":                   sendUFrame(BuildUFrame(UFrameType.Dm,    isCommand: false, pfBitOverride: null, isExpedited: true,  tx)); break;
            case "SABM (P == 1)":                  sendUFrame(BuildUFrame(UFrameType.Sabm,  isCommand: true,  pfBitOverride: true, isExpedited: false, tx)); break;
            case "SABME (P = 1)":                  sendUFrame(BuildUFrame(UFrameType.Sabme, isCommand: true,  pfBitOverride: true, isExpedited: false, tx)); break;
            case "DISC (P = 1)":                   sendUFrame(BuildUFrame(UFrameType.Disc,  isCommand: true,  pfBitOverride: true, isExpedited: false, tx)); break;

            // ─── UI-frame transmissions ────────────────────────────────
            //
            // `UI_command` is drawn in every state's DL_UNIT_DATA_request
            // column (figc4.1 t02, figc4.2 t12, figc4.3 t07, figc4.4 t23,
            // figc4.6 t02). The payload + PID come from the triggering
            // DL_UNIT_DATA_request primitive; the P/F bit defaults to
            // false unless populated via `tx.Pending.PfBit` by a
            // preceding processing verb.
            case "UI_command":                     sendUiFrame(BuildUiFrame(isCommand: true, tx)); break;

            // ─── I-frame transmission ─────────────────────────────────
            //
            // `I_command` is figc4.4's signal_lower verb for I-frame
            // emission. The action chain preceding it (`N(s) := V(s);
            // N(r) := V(r); p := 0`) sets pending fields; this verb
            // reads them along with the payload + PID from the
            // triggering <see cref="IFramePopsOffQueue"/> event. The
            // emitted I-frame's payload is also stored in
            // <see cref="Ax25SessionContext.SentIFrames"/> keyed by
            // N(S), so figc4.4's REJ/SREJ recovery paths can retrieve
            // it via <c>push_old_I_frame_N_r_on_queue</c>.
            case "I_command":                      EmitIFrame(tx); break;

            // ─── DL upper-layer signals (signal_upper) ────────────────
            //
            // Pure event-out. Each verb maps to a DataLinkSignal record
            // forwarded via `sendUpward` to the upper-layer consumer.
            // DL_DATA_indication carries the triggering I-frame's Info
            // and Pid; the others have no payload.
            case "DL_CONNECT_indication":          sendUpward(new DataLinkConnectIndication()); break;
            case "DL_CONNECT_confirm":             sendUpward(new DataLinkConnectConfirm()); break;
            case "DL_DISCONNECT_indication":       sendUpward(new DataLinkDisconnectIndication()); break;
            case "DL_DISCONNECT_confirm":          sendUpward(new DataLinkDisconnectConfirm()); break;
            case "DL_DATA_indication":             sendUpward(BuildDataIndication(tx)); break;

            // DL_ERROR_indication_* — per §C5 error-code letter table.
            // The suffix after `DL_ERROR_indication_` is the code:
            // single letters C/D/E/F/G/K/L/M/N/O, plus the C_D pair
            // (figure draws the combined code on one event).
            case "DL_ERROR_indication_C_D":        sendUpward(new DataLinkErrorIndication("C_D")); break;
            case "DL_ERROR_indication_D":          sendUpward(new DataLinkErrorIndication("D"));   break;
            case "DL_ERROR_indication_E":          sendUpward(new DataLinkErrorIndication("E"));   break;
            case "DL_ERROR_indication_F":          sendUpward(new DataLinkErrorIndication("F"));   break;
            case "DL_ERROR_indication_G":          sendUpward(new DataLinkErrorIndication("G"));   break;
            case "DL_ERROR_indication_K":          sendUpward(new DataLinkErrorIndication("K"));   break;
            case "DL_ERROR_indication_L":          sendUpward(new DataLinkErrorIndication("L"));   break;
            case "DL_ERROR_indication_M":          sendUpward(new DataLinkErrorIndication("M"));   break;
            case "DL_ERROR_indication_N":          sendUpward(new DataLinkErrorIndication("N"));   break;
            case "DL_ERROR_indication_O":          sendUpward(new DataLinkErrorIndication("O"));   break;

            // ─── Link-multiplexer signals (signal_lower) ──────────────
            //
            // The LM_*_request verbs go to the link multiplexer (the
            // medium-access arbiter), not directly to the radio. See
            // figc4.4 t53–t59 for the seize/release/data flow that
            // implements P-persistence on the channel.
            case "LM_seize_request":               sendLinkMux(new LinkMultiplexerSeizeRequest()); break;
            case "LM_release_request":             sendLinkMux(new LinkMultiplexerReleaseRequest()); break;
            case "LM_data_request":                sendLinkMux(new LinkMultiplexerDataRequest()); break;

            // ─── Internal-out signals ──────────────────────────────────
            //
            // MDL_NEGOTIATE_request triggers the management data-link to
            // start XID negotiation with the peer (figc4.6). The push_*
            // verbs enqueue an I-frame onto the internal TX queue, which
            // the session pops via I_frame_pops_off_queue events.
            case "MDL_NEGOTIATE_request":          sendInternal(new MdlNegotiateRequestSignal()); break;
            case "push_on_I_frame_queue":          PushOnIFrameQueue(tx); break;
            case "push_frame_on_queue":            PushOnIFrameQueue(tx); break;
            case "push_old_I_frame_N_r_on_queue":  PushOldIFrameNrOnQueue(tx); break;

            // ─── Processing verbs (queue / storage / flags / counters) ─
            //
            // Two "discard queue" spellings exist across figures — both
            // clear the I-frame transmit queue. `discard_I_frame_queue`
            // is a third spelling (figc4.4); kept as a separate case so
            // the catalogue can record all three as canonical names
            // referring to the same operation.
            case "discard_frame_queue":            ctx.IFrameQueue.Clear(); break;
            case "discard_queue":                  ctx.IFrameQueue.Clear(); break;
            case "discard_I_frame_queue":          ctx.IFrameQueue.Clear(); break;

            // `discard_I_frame` and `discard_contents_of_I_frame` drop
            // the current incoming frame's payload — explicit "we are
            // not delivering this upward". No context mutation required;
            // we just don't fire DL_DATA_indication for this trigger.
            case "discard_I_frame":                /* no-op: incoming not stored anywhere */ break;
            case "discard_contents_of_I_frame":    /* no-op: incoming not stored anywhere */ break;

            // `discard_primitive` is the catch-all "drop the trigger and
            // do nothing" verb (figc4.1 t06: all-other-primitives-from-
            // upper-layer column).
            case "discard_primitive":              /* no-op */ break;

            // SREJ exception bookkeeping. SrejExceptionCount tracks the
            // number of outstanding SREJ exceptions per §C4.3. The
            // SelectiveRejectException bool flag is set when the count
            // transitions 0 → 1+ and cleared on 1+ → 0.
            case "set_reject_exception":           ctx.RejectException = true; break;
            case "clear_reject_exception":         ctx.RejectException = false; break;
            case "increment_srej_exception":
                ctx.SrejExceptionCount++;
                ctx.SelectiveRejectException = true;
                break;
            case "decrement_srej_exception_if_gt_0":
                if (ctx.SrejExceptionCount > 0)
                {
                    ctx.SrejExceptionCount--;
                    if (ctx.SrejExceptionCount == 0) ctx.SelectiveRejectException = false;
                }
                break;

            // Out-of-sequence I-frame storage. `save_contents_of_I_frame`
            // stashes the trigger I-frame's Info + Pid keyed by its N(S);
            // `retrieve_stored_V_r_I_frame` pulls back the frame whose
            // N(S) == V(r) (i.e. the next expected) when V(r) advances
            // and ships it upward as DL_DATA_indication.
            case "save_contents_of_I_frame":       SaveIncomingIFrame(tx); break;
            case "retrieve_stored_V_r_I_frame":    RetrieveStoredVrIFrame(tx, sendUpward); break;

            // Modulus selection. figc4.1 / figc4.4's SABM(E) paths use
            // these to lock in mod-8 vs mod-128 once the peer accepts.
            case "set_version_2_0":                ctx.IsExtended = false; break;
            case "set_version_2_2":                ctx.IsExtended = true;  break;

            // ─── Link-parameter assignments (SRT, T1V) ────────────────
            //
            // Smoothed Round-Trip Time and T1 timeout value per §6.7.1.
            // The dispatcher's <see cref="T1Duration"/> property is the
            // *initial* T1V for new sessions; <c>ctx.T1V</c> is the
            // *current* per-session value mutated by these verbs and
            // (in production) by RTT smoothing in figc4.7's
            // Select_T1_Value subroutine.
            case "SRT := Initial Default":         ctx.Srt = TimeSpan.FromMilliseconds(3000); break;
            case "T1V := 2 * SRT":                 ctx.T1V = ctx.Srt + ctx.Srt; break;

            // ─── Subroutine calls ──────────────────────────────────────
            //
            // Each routes through the registry. DefaultSubroutineRegistry
            // pre-populates with no-op stubs for every known name — the
            // figc4.7 transcription will eventually replace those with
            // real action chains. Tests can register custom impls to
            // observe / mock subroutine invocations.
            case "Establish_Data_Link":            subroutines.Invoke("Establish_Data_Link", tx); break;
            case "Establish_Extended_Data_Link":   subroutines.Invoke("Establish_Extended_Data_Link", tx); break;
            case "Clear_Exception_Conditions":     subroutines.Invoke("Clear_Exception_Conditions", tx); break;
            case "UI_Check":                       subroutines.Invoke("UI_Check", tx); break;
            case "Select_T1_Value":                subroutines.Invoke("Select_T1_Value", tx); break;
            case "Check_I_Frame_Acknowledged":     subroutines.Invoke("Check_I_Frame_Acknowledged", tx); break;
            case "Check_I_Frames_Acknowledged":    subroutines.Invoke("Check_I_Frames_Acknowledged", tx); break;
            case "Check_Need_For_Response":        subroutines.Invoke("Check_Need_For_Response", tx); break;
            case "Transmit_Enquiry":               subroutines.Invoke("Transmit_Enquiry", tx); break;
            case "Invoke_Retransmission":          subroutines.Invoke("Invoke_Retransmission", tx); break;
            case "N_r_Error_Recovery":             subroutines.Invoke("N_r_Error_Recovery", tx); break;
            case "Enquiry_Response":               subroutines.Invoke("Enquiry_Response", tx); break;
            case "Set_Version_2_0":                subroutines.Invoke("Set_Version_2_0", tx); break;
            case "Set_Version_2_2":                subroutines.Invoke("Set_Version_2_2", tx); break;
            // Legacy names that still appear in connected.sdl.yaml — kept
            // distinct from Enquiry_Response so existing tests / transitions
            // continue to record them under their own names. The registry
            // adds them as no-op stubs alongside the generated specs; a
            // follow-up PR can update connected.sdl.yaml to call
            // Enquiry_Response directly and retire these.
            case "Enquiry_Response_F_0":           subroutines.Invoke("Enquiry_Response_F_0", tx); break;
            case "Enquiry_Response_F_1":           subroutines.Invoke("Enquiry_Response_F_1", tx); break;

            // ─── Sequence-variable assignments (pure context) ──────────
            //
            // Verb spellings here track the figure-canonical lowercase form
            // (`V(s)`, `V(r)`, `V(a)`) used in /spec-sdl/*.sdl.yaml. The
            // catalogue (`spec-sdl/actions.yaml`) reserves these as the
            // canonical names.
            case "V(s) := 0":                      ctx.VS = 0; break;
            case "V(s) := V(s) + 1":               ctx.VS = ctx.IncrementSeq(ctx.VS); break;
            case "V(r) := 0":                      ctx.VR = 0; break;
            case "V(r) := V(r) + 1":               ctx.VR = ctx.IncrementSeq(ctx.VR); break;
            case "V(a) := 0":                      ctx.VA = 0; break;
            case "RC := 0":                        ctx.RC = 0; break;
            case "RC := 1":                        ctx.RC = 1; break;
            case "RC := RC + 1":                   ctx.RC++; break;

            // ─── Sequence-variable assignments (reads from incoming frame) ─
            //
            // `V(a) := N(r)` advances the acknowledge state to the N(R) of
            // the just-received frame. Only valid when the trigger is a
            // frame-receipt event; throws otherwise.
            case "V(a) := N(r)":                   ctx.VA = ExtractNr(tx); break;

            // ─── Pending-frame field assignments (write side) ──────────
            //
            // These populate <see cref="TransitionContext.Pending"/>; the
            // signal_lower verb that follows in the same chain consumes
            // pending state to build a complete outgoing frame spec.
            //
            // For F := P / N(r) := N(s) the right-hand side is a field of
            // the triggering incoming frame; the helper throws cleanly if
            // the trigger doesn't carry one. For constant assignments
            // (F := 0, F := 1, p := 0) no incoming frame is required.
            //
            // `p` (lowercase) is the spec's spelling of the outgoing P bit
            // on a command frame. Treated as the same bit as F on the wire
            // — both end up in <see cref="PendingFrame.PfBit"/>.
            case "N(r) := V(r)":                   tx.Pending.Nr = ctx.VR; break;
            case "N(s) := V(s)":                   tx.Pending.Ns = ctx.VS; break;
            case "N(r) := N(s)":                   tx.Pending.Nr = ExtractNs(tx); break;
            case "F := 0":                         tx.Pending.PfBit = false; break;
            case "F := 1":                         tx.Pending.PfBit = true;  break;
            case "F := P":                         tx.Pending.PfBit = ExtractPollFinal(tx); break;
            case "p := 0":                         tx.Pending.PfBit = false; break;

            default:
                throw new InvalidOperationException(
                    $"unknown SDL action: '{action}'. " +
                    "If this verb appears in a new transcription, add a case in ActionDispatcher.Execute.");
        }
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
        SupervisoryFrameType type, bool isCommand, TransitionContext tx, string verb)
    {
        byte nr     = tx.Pending.Nr    ?? tx.Session.VR;
        bool pfBit  = tx.Pending.PfBit ?? false;
        return new SupervisoryFrameSpec(type, IsCommand: isCommand, Nr: nr, PfBit: pfBit);
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
        return new UFrameSpec(type, IsCommand: isCommand, PfBit: pfBit, IsExpedited: isExpedited);
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
    /// Implement <c>push_old_I_frame_N_r_on_queue</c>: find the
    /// previously-sent I-frame whose N(S) equals the incoming frame's
    /// N(R), and put it back on the transmit queue for retransmission.
    /// Used in figc4.4's REJ/SREJ recovery paths.
    /// </summary>
    private void PushOldIFrameNrOnQueue(TransitionContext tx)
    {
        byte nr = ExtractNr(tx);
        if (!tx.Session.SentIFrames.TryGetValue(nr, out var entry))
        {
            // figc4.4 assumes the frame is still in storage. Real impls
            // (linbpq/direwolf) silently skip if it's been evicted; we do
            // the same rather than throw, since the figure's behaviour
            // here is implementation-defined when state is lost.
            return;
        }
        tx.Session.IFrameQueue.Enqueue(entry);
        sendInternal(new PushIFrameQueueSignal(entry.Data));
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
            Pid:       popped.Pid));
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
    /// Implement <c>retrieve_stored_V_r_I_frame</c>: pull the stored
    /// I-frame whose N(S) equals the session's current V(R), ship its
    /// payload upward as <see cref="DataLinkDataIndication"/>, and
    /// remove it from storage.
    /// </summary>
    /// <remarks>
    /// In the spec figures, V(r) is typically incremented in the same
    /// action chain before this verb fires, so V(r) points at the
    /// next-expected seqno that the just-stored out-of-order frame had.
    /// If no stored frame matches, this is a no-op — matching what
    /// linbpq / direwolf do.
    /// </remarks>
    private static void RetrieveStoredVrIFrame(TransitionContext tx, Action<DataLinkSignal> sendUpward)
    {
        if (!tx.Session.StoredReceivedIFrames.Remove(tx.Session.VR, out var stored))
        {
            return;
        }
        sendUpward(new DataLinkDataIndication(stored.Info, stored.Pid));
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
        return new UiFrameSpec(IsCommand: isCommand, PfBit: pfBit, Info: dud.Data, Pid: dud.Pid);
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
}
