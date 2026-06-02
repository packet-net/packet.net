namespace Packet.Ax25.Session;

/// <summary>
/// Helpers to wire the <see cref="GuardEvaluator"/> against an
/// <see cref="Ax25SessionContext"/> + <see cref="ITimerScheduler"/>.
/// </summary>
/// <remarks>
/// The bindings table maps each identifier the SDL's guard expressions
/// can reference to a closure that reads its current value. The
/// vocabulary grows as new transcriptions land — new identifiers get
/// added here, and the evaluator throws <see cref="GuardEvaluationException"/>
/// on unbound names so typos surface fast.
/// </remarks>
public static class Ax25SessionBindings
{
    /// <summary>
    /// Build the standard binding table for an AX.25 session — every
    /// identifier the SDL transcriptions reference, mapped to a closure
    /// over the supplied context and scheduler.
    /// </summary>
    /// <param name="context">Session state (flags, sequence variables, queues).</param>
    /// <param name="scheduler">Timer scheduler (read for <c>T1_running</c> etc.).</param>
    /// <param name="currentTrigger">
    /// Optional thunk returning the event currently being dispatched.
    /// When supplied, the binding table includes frame-aware predicates
    /// (<c>P_eq_1</c>, <c>F_eq_1</c>, <c>command</c>, <c>N_s_eq_V_r</c>,
    /// <c>nr_in_window</c> / <c>V_a_le_N_r_le_V_s</c>, etc.) that read
    /// from the trigger's attached <see cref="Ax25Frame"/>. When
    /// <c>null</c>, those predicates are absent — guard expressions
    /// using them will fail with <see cref="GuardEvaluationException"/>
    /// from the evaluator's unbound-identifier check.
    /// </param>
    public static IReadOnlyDictionary<string, Func<bool>> CreateDefault(
        Ax25SessionContext context,
        ITimerScheduler scheduler,
        Func<Ax25Event?>? currentTrigger = null)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(scheduler);

        var bindings = new Dictionary<string, Func<bool>>(StringComparer.Ordinal)
        {
            // ─── Flags (§C4.3) ──────────────────────────────────────────
            ["own_receiver_busy"]          = () => context.OwnReceiverBusy,
            ["peer_receiver_busy"]         = () => context.PeerReceiverBusy,
            ["acknowledge_pending"]        = () => context.AcknowledgePending,
            ["reject_exception"]           = () => context.RejectException,
            ["selective_reject_exception"] = () => context.SelectiveRejectException,
            ["layer_3_initiated"]          = () => context.Layer3Initiated,
            ["srej_enabled"]               = () => context.SrejEnabled,
            ["version_2_2"]                = () => context.IsExtended,
            ["srej_exception_gt_0"]        = () => context.SrejExceptionCount > 0,

            // ─── Node policy (figc4.1 SABM_received decision) ──────────
            //
            // "Able to establish?" — the spec defers to the station's
            // own policy: link budget, channel busy, callsign allow-list,
            // resource limits, etc. The default reads
            // <see cref="Ax25SessionContext.AcceptIncoming"/>, which
            // defaults to <c>true</c> (always accept) — matching
            // direwolf's behaviour ("we are always willing to accept
            // connections", ax25_link.c:4337). The
            // <see cref="Ax25Listener"/> flips this flag at the session
            // boundary (false on a transient session it has chosen to
            // reject) so the SDL t15 path emits DM without any wrapper
            // closure. Callers that need richer policy (callsign
            // allow-lists, channel busy, resource limits) can still
            // override the binding entry — replace it in this dict
            // before handing it to <see cref="GuardEvaluator"/>.
            ["able_to_establish"]          = () => context.AcceptIncoming,

            // ─── Sequence-variable comparisons (mod-aware) ─────────────
            ["V_s_eq_V_a"]                 = () => context.VS == context.VA,
            ["V_s_eq_V_a_plus_k"]
                = () => ((context.VS - context.VA + context.Modulus) % context.Modulus) >= context.K,

            // ─── Timer state ───────────────────────────────────────────
            ["T1_running"]                 = () => scheduler.IsRunning("T1"),
            ["T2_running"]                 = () => scheduler.IsRunning("T2"),
            ["T3_running"]                 = () => scheduler.IsRunning("T3"),

            // ─── Retry-counter comparison ──────────────────────────────
            ["RC_eq_N2"]                   = () => context.RC == context.N2,
            ["rc_eq_0"]                    = () => context.RC == 0,

            // ─── Queue / storage state ─────────────────────────────────
            // Predicate name is figc4.4's "V(r) I-frame stored" mangled
            // into an identifier — V_r_I_frame_stored is the canonical
            // YAML spelling; vr_i_frame_stored is the same predicate
            // under the lower-case-everywhere convention used by some
            // call sites.
            ["V_r_I_frame_stored"]         = () => context.StoredReceivedIFrames.ContainsKey(context.VR),
            ["vr_i_frame_stored"]          = () => context.StoredReceivedIFrames.ContainsKey(context.VR),

            // ─── figc4.7 subroutine predicates ─────────────────────────
            // Modulus (figc4.7's `Mod 128?` / `Mod 8?` decisions).
            ["mod_128"]                    = () => context.IsExtended,
            ["mod_8"]                      = () => !context.IsExtended,
            // T1 expired (consumed by Select_T1_Value's middle branch).
            ["t1_expired"]                 = () => context.T1HadExpired,
            // Lowercase alias for the existing T1_running predicate so
            // figc4.7's lower-case-everywhere YAML doesn't break.
            ["t1_running"]                 = () => scheduler.IsRunning("T1"),
            // Out-of-sequence receive buffer presence (figc4.7's
            // Enquiry_Response decision).
            ["out_of_sequence_frames_in_receive_buffer"]
                                           = () => context.StoredReceivedIFrames.Count > 0,
            // Invoke_Retransmission loop terminator: V(s) caught up to
            // its saved-on-entry value X. Returns false if X hasn't been
            // set (i.e. we're not inside an Invoke_Retransmission call).
            ["v_s_eq_x"]                   = () => context.X.HasValue && context.VS == context.X.Value,
        };

        if (currentTrigger is not null)
        {
            // Frame-aware predicates: read fields off the trigger's
            // attached frame. If the trigger isn't a frame-receipt event
            // (e.g. a timer expiry or an upper-layer primitive), these
            // return safe defaults — `false` for P/F/command and out-of-
            // window for sequence checks. That matches the figures'
            // expectation that frame-aware predicates only get evaluated
            // on frame-arrival transitions.
            bool IncomingPollFinal()
                => GetIncomingFrame(currentTrigger())?.PollFinal == true;
            bool IncomingCommand()
                => GetIncomingFrame(currentTrigger())?.IsCommand == true;

            bindings["P_eq_1"]            = IncomingPollFinal;
            bindings["F_eq_1"]            = IncomingPollFinal;
            bindings["P_or_F_eq_1"]       = IncomingPollFinal;
            bindings["command"]           = IncomingCommand;

            bindings["N_s_eq_V_r"]        = () => IncomingNs(currentTrigger()) is byte ns && ns == context.VR;
            bindings["N_s_gt_V_r_plus_1"] = () =>
            {
                if (IncomingNs(currentTrigger()) is not byte ns) return false;
                int diff = (ns - context.VR + context.Modulus) % context.Modulus;
                return diff > 1;
            };

            // `nr_in_window` / `V_a_le_N_r_le_V_s` — both predicates the
            // figures use are the same check: incoming N(R) lies in the
            // ring-buffer window [V(a), V(s)] (inclusive of both ends in
            // mod-N arithmetic — N(R) = V(a) is "all sent frames are
            // acked" and N(R) = V(s) is "no outstanding").
            bool NrInWindow()
            {
                if (IncomingNr(currentTrigger()) is not byte nr) return false;
                int span    = (context.VS - context.VA + context.Modulus) % context.Modulus;
                int nrDelta = (nr        - context.VA + context.Modulus) % context.Modulus;
                return nrDelta <= span;
            }
            bindings["nr_in_window"]        = NrInWindow;
            bindings["V_a_le_N_r_le_V_s"]   = NrInWindow;

            // `info_field_valid` — heuristic; the figures distinguish
            // information-field-too-long vs valid. We treat "info field
            // present and within ctx.N1 octets" as valid. Mod-128 specs
            // permit longer info fields; we use ctx.N1 directly so the
            // session can be configured.
            bindings["info_field_valid"] = () =>
                GetIncomingFrame(currentTrigger()) is { Info.Length: var len }
                && len <= context.N1;

            // ─── figc4.7 frame-aware predicates ─────────────────────────
            // UI_Check uses `incoming_is_command` / `ui_info_field_valid`.
            // Same semantics as `command` / `info_field_valid` already
            // above; aliased here so the figc4.7 YAML's predicate names
            // resolve.
            bindings["incoming_is_command"]   = IncomingCommand;
            bindings["ui_info_field_valid"]   = () =>
                GetIncomingFrame(currentTrigger()) is { Info.Length: var len }
                && len <= context.N1;

            // N(r) comparisons for Check_I_Frame_Acknowledged.
            bindings["n_r_eq_v_s"] = () => IncomingNr(currentTrigger()) is byte nr && nr == context.VS;
            bindings["n_r_eq_v_a"] = () => IncomingNr(currentTrigger()) is byte nr && nr == context.VA;

            // Compound flags for Check_Need_For_Response.
            bindings["command_and_p_eq_1"]  = () =>
                GetIncomingFrame(currentTrigger()) is { IsCommand: true, PollFinal: true };
            bindings["response_and_f_eq_1"] = () =>
                GetIncomingFrame(currentTrigger()) is { IsResponse: true, PollFinal: true };

            // Enquiry_Response's first-decision compound: F bit set
            // *and* the triggering frame is an RR / RNR / I (a poll-able
            // shape). REJ / SREJ aren't included per the spec wording.
            bindings["f_eq_1_and_supervisory_or_i"] = () =>
            {
                var f = GetIncomingFrame(currentTrigger());
                if (f is null || !f.PollFinal) return false;
                bool isIFrame = (f.Control & 0x01) == 0;
                byte sBase = (byte)(f.Control & 0x0F);
                bool isRR  = sBase == 0x01;
                bool isRNR = sBase == 0x05;
                return isIFrame || isRR || isRNR;
            };

            // `response` (bare) — the figc4.5 SREJ-column decision. The
            // lone-flag form of the IsCommand/IsResponse split. Doesn't
            // correspond to any historic binding so it's registered as a
            // proper entry rather than aliased through GuardEvaluator's
            // alias map.
            bindings["response"]                  = () =>
                GetIncomingFrame(currentTrigger())?.IsResponse == true;

            // ─── ax25spec#40 receive-window discard guard ──────────────
            // figc4.4's out-of-sequence I_received path has no window guard:
            // any N(S) ≠ V(R) is SREJ'd/REJ'd, including a duplicate behind
            // V(R) — which provokes a re-send that's again out-of-window, ad
            // infinitum (the SREJ livelock). X.25 §2.4.6.4 discards any frame
            // whose N(S) is outside the receive window [V(r), V(r)+k). The
            // figure's `reject_exception` decision IS its discard-vs-reject
            // switch in that region, so we OR the out-of-window condition into
            // it (when Ax25Spec40DiscardOutOfWindowIFrames is on): such a frame
            // takes the figure's own discard path (process ack, discard data,
            // RR(V(r)) only if P=1) ahead of the srej_enabled split, covering
            // both REJ and SREJ modes. Scoped to IFrameReceived via the helper,
            // so it's inert on every other trigger. See Ax25SessionQuirks.
            if (context.Quirks.Ax25Spec40DiscardOutOfWindowIFrames)
            {
                bool IFrameOutOfWindow()
                {
                    if (currentTrigger() is not IFrameReceived) return false;
                    if (IncomingNs(currentTrigger()) is not byte ns) return false;
                    int offset = (ns - context.VR + context.Modulus) % context.Modulus;
                    return offset >= context.K;   // N(S) outside [V(r), V(r)+k)
                }

                var baseRejectException = bindings["reject_exception"];
                bindings["reject_exception"] = () => baseRejectException() || IFrameOutOfWindow();
            }

            // ─── ax25spec#43 DL-FLOW-OFF branch inversion ──────────────
            // figc4.4 gates DL-FLOW-OFF's Set-Own-Receiver-Busy/RNR actions on the
            // own_receiver_busy=Yes branch, so a not-busy station receiving
            // DL-FLOW-OFF never enters busy — the primitive can't do its one job
            // (§6.4.10; the FLOW-ON mirror correctly acts on its Yes/busy branch).
            // Invert the own_receiver_busy guard for the DL_FLOW_OFF_request
            // trigger only, so not-busy takes the action branch and already-busy
            // no-ops. Trigger-scoped: only the FLOW-OFF decision reads
            // own_receiver_busy during that dispatch, so it's inert elsewhere.
            if (context.Quirks.Ax25Spec43DlFlowOffEntersBusy)
            {
                var baseOwnReceiverBusy = bindings["own_receiver_busy"];
                bindings["own_receiver_busy"] = () =>
                    currentTrigger() is DlFlowOffRequest
                        ? !baseOwnReceiverBusy()
                        : baseOwnReceiverBusy();
            }
        }

        return bindings;

        // Note: Packet.Ax25.Sdl v0.5.0 emits predicate names in their
        // walker-normalised forms (vs_eq_va, ack_pending, SREJ_enabled, …).
        // Those aren't registered as bindings here — see
        // GuardEvaluator.PredicateAliases for the rename table the
        // evaluator consults on a miss. Keeping the alias resolution at
        // evaluation time rather than build time means test-time overrides
        // of the canonical name still apply to the new spelling.
    }

    /// <summary>
    /// Extract the attached <see cref="Ax25Frame"/> from a trigger
    /// event, mirroring <see cref="TransitionContext"/>'s frame
    /// extraction. Returns <c>null</c> for events that don't carry one
    /// (timer expiries, upper-layer primitives without payload, etc.).
    /// </summary>
    private static Ax25Frame? GetIncomingFrame(Ax25Event? e) => e switch
    {
        IFrameReceived f     => f.Frame,
        RrReceived f         => f.Frame,
        RnrReceived f        => f.Frame,
        RejReceived f        => f.Frame,
        SrejReceived f       => f.Frame,
        UiReceived f         => f.Frame,
        SabmReceived f       => f.Frame,
        SabmeReceived f      => f.Frame,
        DiscReceived f       => f.Frame,
        UaReceived f         => f.Frame,
        DmReceived f         => f.Frame,
        FrmrReceived f       => f.Frame,
        XidReceived f        => f.Frame,
        TestReceived f       => f.Frame,
        IOrSCommandReceived f => f.Frame,
        AllOtherCommands f   => f.Frame,
        _ => null,
    };

    /// <summary>Extract the N(R) bits from a mod-8 control byte, or <c>null</c> if no frame.</summary>
    private static byte? IncomingNr(Ax25Event? e)
    {
        var f = GetIncomingFrame(e);
        return f is null ? null : (byte)((f.Control >> 5) & 0x07);
    }

    /// <summary>
    /// Extract the N(S) bits from a mod-8 I-frame's control byte. Only
    /// meaningful for I-frames (the bits at the same position encode
    /// supervisory-frame type for S-frames); the caller is responsible
    /// for checking the trigger type before relying on the value.
    /// </summary>
    private static byte? IncomingNs(Ax25Event? e)
    {
        var f = GetIncomingFrame(e);
        return f is null ? null : (byte)((f.Control >> 1) & 0x07);
    }
}
