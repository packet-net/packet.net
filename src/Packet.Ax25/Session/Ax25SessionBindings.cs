using Packet.Ax25.Sdl;

namespace Packet.Ax25.Session;

/// <summary>
/// Helpers to wire the <see cref="GuardEvaluator"/> against an
/// <see cref="Ax25SessionContext"/> + <see cref="ITimerScheduler"/>.
/// </summary>
/// <remarks>
/// The binding table maps every <see cref="Ax25Guard"/> atom the SDL's guard
/// expressions can reference to a closure that reads its current value. The
/// table is <em>exhaustive</em> over the <see cref="Ax25Guard"/> closed set —
/// the codegen emits typed atoms, and the <c>switch</c> below binds every
/// member, so a newly-introduced atom is a compile error (CS8509) here rather
/// than an unbound-identifier surprise at runtime.
/// </remarks>
public static class Ax25SessionBindings
{
    /// <summary>
    /// Build the standard binding table for an AX.25 session — every
    /// <see cref="Ax25Guard"/> atom mapped to a closure over the supplied
    /// context and scheduler.
    /// </summary>
    /// <param name="context">Session state (flags, sequence variables, queues).</param>
    /// <param name="scheduler">Timer scheduler (read for <c>T1_running</c> etc.).</param>
    /// <param name="currentTrigger">
    /// Optional thunk returning the event currently being dispatched. The
    /// frame-aware atoms (<see cref="Ax25Guard.PEq1"/>, <see cref="Ax25Guard.Command"/>,
    /// <see cref="Ax25Guard.NsEqVr"/>, <see cref="Ax25Guard.VaLeNrLeVs"/>, …) read
    /// the trigger's attached <see cref="Ax25Frame"/>. When <c>null</c> (or when
    /// the thunk returns <c>null</c>, i.e. the event isn't a frame-receipt), those
    /// atoms evaluate to safe defaults — <c>false</c> for P/F/command and
    /// out-of-window for sequence checks — matching the figures' expectation that
    /// frame-aware predicates only matter on frame-arrival transitions.
    /// </param>
    public static IReadOnlyDictionary<Ax25Guard, Func<bool>> CreateDefault(
        Ax25SessionContext context,
        ITimerScheduler scheduler,
        Func<Ax25Event?>? currentTrigger = null)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(scheduler);

        // Frame-aware helpers — read fields off the trigger's attached frame.
        // A null trigger thunk, or a trigger that isn't a frame-receipt event
        // (timer expiry, upper-layer primitive), yields a null frame and the
        // closures fall back to safe defaults.
        Ax25Frame? IncomingFrame() => GetIncomingFrame(currentTrigger?.Invoke());
        bool IncomingPollFinal() => IncomingFrame()?.PollFinal == true;
        bool IncomingCommand()   => IncomingFrame()?.IsCommand == true;

        bool NsEqVr()
            => IncomingNs(currentTrigger?.Invoke()) is byte ns && ns == context.VR;
        bool NsGtVrPlus1()
        {
            if (IncomingNs(currentTrigger?.Invoke()) is not byte ns) return false;
            int diff = (ns - context.VR + context.Modulus) % context.Modulus;
            return diff > 1;
        }

        // `va_le_nr_le_vs` — incoming N(R) lies in the ring-buffer window
        // [V(a), V(s)] (inclusive of both ends in mod-N arithmetic — N(R) = V(a)
        // is "all sent frames acked"; N(R) = V(s) is "no outstanding").
        bool NrInWindow()
        {
            if (IncomingNr(currentTrigger?.Invoke()) is not byte nr) return false;
            int span    = (context.VS - context.VA + context.Modulus) % context.Modulus;
            int nrDelta = (nr        - context.VA + context.Modulus) % context.Modulus;
            return nrDelta <= span;
        }

        // `info_field_length_le_N1_and_content_is_octet_aligned` — heuristic:
        // info field present and within ctx.N1 octets. Mod-128 specs permit
        // longer info fields; ctx.N1 is configurable so the session can be set up.
        bool InfoFieldValid()
            => IncomingFrame() is { Info.Length: var len } && len <= context.N1;

        bool NrEqVs() => IncomingNr(currentTrigger?.Invoke()) is byte nr && nr == context.VS;
        bool NrEqVa() => IncomingNr(currentTrigger?.Invoke()) is byte nr && nr == context.VA;

        bool CommandAndPEq1()
            => IncomingFrame() is { IsCommand: true, PollFinal: true };
        bool ResponseAndFEq1()
            => IncomingFrame() is { IsResponse: true, PollFinal: true };
        bool Response() => IncomingFrame()?.IsResponse == true;

        // Enquiry_Response's first-decision compound: F bit set *and* the
        // triggering frame is an RR / RNR / I (a poll-able shape). REJ / SREJ
        // aren't included per the spec wording.
        bool FEq1AndSupervisoryOrI()
        {
            var f = IncomingFrame();
            if (f is null || !f.PollFinal) return false;
            bool isIFrame = (f.Control & 0x01) == 0;
            byte sBase = (byte)(f.Control & 0x0F);
            bool isRR  = sBase == 0x01;
            bool isRNR = sBase == 0x05;
            return isIFrame || isRR || isRNR;
        }

        // Resolve each Ax25Guard atom to its closure. Exhaustive by construction:
        // a missing *named* member trips CS8509 (a build error, not a runtime
        // surprise) — that's the whole point of the typed closed set. Each atom
        // binds to the SAME closure the pre-typed string-keyed table registered
        // under its legacy name (recorded in spec-sdl/predicates.yaml
        // `# legacy binding:` comments), so protocol behaviour is identical by
        // construction.
        //
        // CS8524 (only the unnamed out-of-range cast is unhandled — i.e. every
        // named member IS handled) is suppressed: we never construct an
        // out-of-range Ax25Guard, and adding a `_ =>` arm would silently swallow
        // a future named member instead of failing the build via CS8509.
#pragma warning disable CS8524
        Func<bool> BindAtom(Ax25Guard atom)
            => atom switch
            {
                // ─── Session flags (§6.x) ──────────────────────────────────
                // legacy binding: own_receiver_busy
                Ax25Guard.OwnReceiverBusy   => () => context.OwnReceiverBusy,
                // legacy binding: peer_receiver_busy
                Ax25Guard.PeerReceiverBusy  => () => context.PeerReceiverBusy,
                // legacy binding: acknowledge_pending
                Ax25Guard.AckPending        => () => context.AcknowledgePending,
                Ax25Guard.RejectException   => () => context.RejectException,
                Ax25Guard.Layer3Initiated   => () => context.Layer3Initiated,
                // legacy binding: srej_enabled
                Ax25Guard.SREJEnabled       => () => context.SrejEnabled,
                // legacy binding: srej_exception_gt_0
                Ax25Guard.SrejectExceptionGt0 => () => context.SrejExceptionCount > 0,
                Ax25Guard.OutOfSequenceFramesInReceiveBuffer
                                            => () => context.StoredReceivedIFrames.Count > 0,
                // legacy binding: vr_i_frame_stored (figc4.4/figc4.5 stored-frame drain loop)
                Ax25Guard.VrIFrameStored    => () => context.StoredReceivedIFrames.ContainsKey(context.VR),

                // ─── Node policy (figc4.1 SABM_received decision) ──────────
                Ax25Guard.AbleToEstablish   => () => context.AcceptIncoming,

                // ─── Version / modulus (figc4.7 Mod 128? / Mod 8?) ─────────
                Ax25Guard.Mod128            => () => context.IsExtended,
                Ax25Guard.Mod8              => () => !context.IsExtended,
                Ax25Guard.Version22         => () => context.IsExtended,

                // ─── Sequence-variable comparisons (mod-aware) ─────────────
                // legacy binding: V_s_eq_V_a
                Ax25Guard.VsEqVa            => () => context.VS == context.VA,
                // legacy binding: V_s_eq_V_a_plus_k
                Ax25Guard.VsEqVaPlusK
                    => () => ((context.VS - context.VA + context.Modulus) % context.Modulus) >= context.K,
                // legacy binding: v_s_eq_x — Invoke_Retransmission loop terminator:
                // V(s) caught up to its saved-on-entry value X. False if X unset.
                Ax25Guard.VsEqX             => () => context.X.HasValue && context.VS == context.X.Value,

                // ─── Timer state ───────────────────────────────────────────
                Ax25Guard.T1Running         => () => scheduler.IsRunning("T1"),
                // legacy binding: t1_expired (Select_T1_Value middle branch)
                Ax25Guard.T1Expired         => () => context.T1HadExpired,

                // ─── Retry-counter comparisons ─────────────────────────────
                // legacy binding: rc_eq_0
                Ax25Guard.RCEq0             => () => context.RC == 0,
                Ax25Guard.RCEqN2            => () => context.RC == context.N2,
                // figc5.2's TM201-expiry retry-limit diamond: RC == NM201, with
                // NM201 carried in the (MDL) context's N2. Never reached by the
                // data-link machine; the MDL driver overrides this entry with the
                // identical closure over its own context.
                Ax25Guard.RCEqNM201         => () => context.RC == context.N2,

                // ─── Frame-aware: poll/final + command/response ────────────
                Ax25Guard.PEq1              => IncomingPollFinal,
                Ax25Guard.FEq1              => IncomingPollFinal,
                Ax25Guard.POrFEq1           => IncomingPollFinal,
                Ax25Guard.Command           => IncomingCommand,
                Ax25Guard.Response          => Response,
                // legacy binding: command_and_p_eq_1
                Ax25Guard.CommandAndPEq1    => CommandAndPEq1,
                // legacy binding: response_and_f_eq_1
                Ax25Guard.ResponseAndFEq1   => ResponseAndFEq1,
                // legacy binding: f_eq_1_and_supervisory_or_i
                Ax25Guard.FEq1AndFrameEqRROrFrameEqRNROrFrameEqI => FEq1AndSupervisoryOrI,

                // ─── Frame-aware: received N(s)/N(r) comparisons ───────────
                // legacy binding: N_s_eq_V_r
                Ax25Guard.NsEqVr            => NsEqVr,
                // legacy binding: N_s_gt_V_r_plus_1
                Ax25Guard.NsGtVrPlus1       => NsGtVrPlus1,
                // legacy binding: V_a_le_N_r_le_V_s
                Ax25Guard.VaLeNrLeVs        => NrInWindow,
                // legacy binding: n_r_eq_v_a
                Ax25Guard.NrEqVa            => NrEqVa,
                // legacy binding: n_r_eq_v_s
                Ax25Guard.NrEqVs            => NrEqVs,
                // vs_eq_nr (figc4.5/figc4.4 recovery-complete after "V(a) := N(r)")
                // is the same comparison as n_r_eq_v_s (N(r) == V(s)). legacy
                // binding: n_r_eq_v_s.
                Ax25Guard.VsEqNr            => NrEqVs,

                // ─── Frame-content validity ────────────────────────────────
                // legacy binding: info_field_valid
                Ax25Guard.InfoFieldLengthLeN1AndContentIsOctetAligned => InfoFieldValid,
            };
#pragma warning restore CS8524

        var bindings = new Dictionary<Ax25Guard, Func<bool>>();
        foreach (Ax25Guard atom in Enum.GetValues<Ax25Guard>())
        {
            bindings[atom] = BindAtom(atom);
        }

        // ─── ax25spec#40 receive-window discard guard ──────────────────────
        // figc4.4's out-of-sequence I_received path has no window guard: any
        // N(S) ≠ V(R) is SREJ'd/REJ'd, including a duplicate behind V(R) — which
        // provokes a re-send that's again out-of-window, ad infinitum (the SREJ
        // livelock). X.25 §2.4.6.4 discards any frame whose N(S) is outside the
        // receive window [V(r), V(r)+k). The figure's `reject_exception` decision
        // IS its discard-vs-reject switch in that region, so we OR the
        // out-of-window condition into it (when the quirk is on): such a frame
        // takes the figure's own discard path (process ack, discard data,
        // RR(V(r)) only if P=1) ahead of the srej_enabled split, covering both
        // REJ and SREJ modes. Scoped to IFrameReceived, so inert on every other
        // trigger. See Ax25SessionQuirks.
        if (context.Quirks.Ax25Spec40DiscardOutOfWindowIFrames)
        {
            bool IFrameOutOfWindow()
            {
                if (currentTrigger?.Invoke() is not IFrameReceived) return false;
                if (IncomingNs(currentTrigger.Invoke()) is not byte ns) return false;
                int offset = (ns - context.VR + context.Modulus) % context.Modulus;
                return offset >= context.K;   // N(S) outside [V(r), V(r)+k)
            }

            var baseRejectException = bindings[Ax25Guard.RejectException];
            bindings[Ax25Guard.RejectException] = () => baseRejectException() || IFrameOutOfWindow();
        }

        // ─── ax25spec#43 DL-FLOW-OFF branch inversion ───────────────────────
        // figc4.4 gates DL-FLOW-OFF's Set-Own-Receiver-Busy/RNR actions on the
        // own_receiver_busy=Yes branch, so a not-busy station receiving
        // DL-FLOW-OFF never enters busy — the primitive can't do its one job
        // (§6.4.10; the FLOW-ON mirror correctly acts on its Yes/busy branch).
        // Invert the own_receiver_busy guard for the DL_FLOW_OFF_request trigger
        // only, so not-busy takes the action branch and already-busy no-ops.
        // Trigger-scoped: only the FLOW-OFF decision reads own_receiver_busy
        // during that dispatch, so it's inert elsewhere.
        if (context.Quirks.Ax25Spec43DlFlowOffEntersBusy)
        {
            var baseOwnReceiverBusy = bindings[Ax25Guard.OwnReceiverBusy];
            bindings[Ax25Guard.OwnReceiverBusy] = () =>
                currentTrigger?.Invoke() is DlFlowOffRequest
                    ? !baseOwnReceiverBusy()
                    : baseOwnReceiverBusy();
        }

        return bindings;
    }

    /// <summary>
    /// Extract the attached <see cref="Ax25Frame"/> from a trigger event,
    /// mirroring <see cref="TransitionContext"/>'s frame extraction. Returns
    /// <c>null</c> for events that don't carry one (timer expiries, upper-layer
    /// primitives without payload, etc.).
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
        XidResponseReceived f => f.Frame,
        TestReceived f       => f.Frame,
        IOrSCommandReceived f => f.Frame,
        AllOtherCommands f   => f.Frame,
        _ => null,
    };

    /// <summary>Extract N(R) from the incoming frame (mode-aware: 3-bit mod-8 /
    /// 7-bit extended mod-128), or <c>null</c> if no frame.</summary>
    private static byte? IncomingNr(Ax25Event? e) => GetIncomingFrame(e)?.Nr;

    /// <summary>
    /// Extract the N(S) bits from an incoming I-frame (mode-aware: 3-bit mod-8 /
    /// 7-bit extended mod-128). Only meaningful for I-frames (the bits at the
    /// same position encode supervisory-frame type for S-frames); the caller is
    /// responsible for checking the trigger type before relying on the value.
    /// </summary>
    private static byte? IncomingNs(Ax25Event? e) => GetIncomingFrame(e)?.Ns;
}
