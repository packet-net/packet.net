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

            // ─── Queue / storage state ─────────────────────────────────
            ["vr_i_frame_stored"]          = () => context.StoredReceivedIFrames.ContainsKey(context.VR),
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
        }

        return bindings;
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
