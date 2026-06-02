using Packet.Ax25.Sdl;

namespace Packet.Ax25.Session;

/// <summary>
/// One AX.25 connection's runtime state machine. Sits between the link
/// layer (frames arriving as events) and the upper-layer service-access
/// point (DL primitives, also as events).
/// </summary>
/// <remarks>
/// <para>
/// The session is fed events via <see cref="PostEvent"/>. For each event
/// it looks up the codegen-emitted transition table for the current
/// state, filters by event name, evaluates guards, picks the matching
/// transition, executes its action chain, and updates the current state.
/// </para>
/// <para>
/// Transitions are sourced from the static tables in
/// <c>Packet.Ax25.Sdl</c> — one table per <c>(machine, state)</c>
/// transcription. Until we have a richer codegen-derived registry, the
/// caller passes in the table-by-state-name map at construction.
/// </para>
/// </remarks>
public sealed class Ax25Session
{
    private readonly IReadOnlyDictionary<string, IReadOnlyList<TransitionSpec>> transitionsByState;
    private readonly IActionDispatcher dispatcher;
    private readonly GuardEvaluator guards;
    private readonly ITimerScheduler scheduler;
    private readonly Action<Ax25Event>? onUnhandledEvent;

    /// <summary>The session's mutable per-connection state.</summary>
    public Ax25SessionContext Context { get; }

    /// <summary>Current state name, matching the SDL <c>state:</c> field.</summary>
    public string CurrentState { get; private set; }

    /// <summary>
    /// The event currently being dispatched. Non-null only during
    /// guard evaluation and action execution; <c>null</c> outside of
    /// <see cref="PostEvent"/>. Frame-aware bindings read this to
    /// resolve predicates like <c>P_eq_1</c>, <c>command</c>,
    /// <c>N_s_eq_V_r</c> against the triggering frame.
    /// </summary>
    public Ax25Event? CurrentTrigger { get; private set; }

    /// <summary>
    /// Raised when the session's dispatcher emits a Layer-3 signal
    /// (DL-CONNECT-confirm, DL-DATA-indication, DL-DISCONNECT-indication,
    /// DL-ERROR-indication, …). Consumers external to the session
    /// rig — typically <see cref="Ax25Listener"/>-side UI / app code —
    /// subscribe here instead of supplying a <c>sendUpward</c>
    /// closure at dispatcher-construction time.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The Listener wires every session's dispatcher with a
    /// <c>sendUpward</c> shim that fan-outs into both the listener's
    /// own per-session queue (for <see cref="Ax25Listener.ConnectAsync"/>
    /// to await DL-CONNECT-confirm) AND this event (for UI / app
    /// observers that want push-style delivery). Pre-listener callers
    /// that build sessions directly via the <see cref="Ax25Session"/>
    /// constructor can still wire their own sink — this event is
    /// raised additively, never replacing the dispatcher's
    /// construction-time callback.
    /// </para>
    /// <para>
    /// Subscribers run synchronously on the thread that posted the
    /// triggering event into <see cref="PostEvent"/>. Keep handlers
    /// fast — long-running work belongs on a different task.
    /// </para>
    /// </remarks>
    public event EventHandler<DataLinkSignal>? DataLinkSignalEmitted;

    /// <summary>
    /// Raise the <see cref="DataLinkSignalEmitted"/> event. Called by
    /// the dispatcher's <c>sendUpward</c> shim — typically wired by
    /// <see cref="Ax25Listener"/> at session-creation time. Public so
    /// custom rigs that build their own dispatcher can chain into the
    /// event from their own <c>sendUpward</c> callback.
    /// </summary>
    public void RaiseDataLinkSignal(DataLinkSignal signal)
    {
        ArgumentNullException.ThrowIfNull(signal);
        DataLinkSignalEmitted?.Invoke(this, signal);
    }

    /// <summary>
    /// Raised after a transition fires: its guard matched the triggering event,
    /// its action chain ran to completion, and <see cref="CurrentState"/> has
    /// advanced to the transition's <c>Next</c>. The argument is the matched
    /// <see cref="TransitionSpec"/> (its <c>From</c> is the pre-transition state,
    /// its <c>Id</c> the codegen transition id). An observability hook — for
    /// transition-coverage instrumentation, tracing, or metrics — that fires
    /// only on a handled event (an unhandled event goes to
    /// <c>onUnhandledEvent</c>, not here) and only once the transition has
    /// committed (a transition whose actions throw is rolled back and not
    /// raised). Handlers run synchronously on the posting thread, after the
    /// state has advanced; keep them fast and non-throwing.
    /// </summary>
    public event EventHandler<TransitionSpec>? TransitionFired;

    /// <summary>
    /// Construct a session.
    /// </summary>
    /// <param name="context">Mutable session state — sequence variables, flags, queues.</param>
    /// <param name="scheduler">Timer scheduler the dispatcher arms / cancels.</param>
    /// <param name="dispatcher">Action interpreter — turns action strings into context mutations and frame-send signals.</param>
    /// <param name="guards">Guard evaluator — turns expression strings into booleans against the session state.</param>
    /// <param name="transitionsByState">
    /// Lookup of state name → transitions out of that state. Typically populated
    /// from the codegen's static tables (e.g.
    /// <c>{ ["Connected"] = DataLink_Connected.Transitions }</c>).
    /// </param>
    /// <param name="initialState">State the session starts in. Must be a key in <paramref name="transitionsByState"/>.</param>
    /// <param name="onUnhandledEvent">
    /// Optional hook fired when an event arrives that has no matching transition.
    /// Defaults to silently dropping the event (matching SDL semantics — events
    /// in states that don't handle them are ignored).
    /// </param>
    public Ax25Session(
        Ax25SessionContext context,
        ITimerScheduler scheduler,
        IActionDispatcher dispatcher,
        GuardEvaluator guards,
        IReadOnlyDictionary<string, IReadOnlyList<TransitionSpec>> transitionsByState,
        string initialState,
        Action<Ax25Event>? onUnhandledEvent = null)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(scheduler);
        ArgumentNullException.ThrowIfNull(dispatcher);
        ArgumentNullException.ThrowIfNull(guards);
        ArgumentNullException.ThrowIfNull(transitionsByState);
        ArgumentNullException.ThrowIfNull(initialState);

        if (!transitionsByState.ContainsKey(initialState))
        {
            throw new ArgumentException($"initial state '{initialState}' is not present in the transition map", nameof(initialState));
        }

        this.Context = context;
        this.scheduler = scheduler;
        this.dispatcher = dispatcher;
        this.guards = guards;
        this.transitionsByState = transitionsByState;
        this.CurrentState = initialState;
        this.onUnhandledEvent = onUnhandledEvent;

        // Auto-wire the default subroutine registry so figc4.7-generated
        // SubroutineSpecs walk their paths through this session's
        // dispatcher + guards. Custom ISubroutineRegistry implementations
        // and tests that pre-register delegates via Register() are
        // unaffected (Register is sticky — Wire skips overridden names).
        if (dispatcher is ActionDispatcher ad && ad.Subroutines is DefaultSubroutineRegistry defaultReg)
        {
            defaultReg.Wire(dispatcher, guards);
        }
    }

    /// <summary>
    /// Drive one event through the state machine. Looks up transitions for
    /// the current state, picks the first whose <c>On</c> matches the
    /// event's <see cref="Ax25Event.Name"/> and whose guard evaluates true,
    /// runs its actions, and advances to <c>Next</c>.
    /// </summary>
    /// <remarks>
    /// Events with no matching transition are passed to
    /// <see cref="onUnhandledEvent"/> (or silently dropped). The current
    /// state is unchanged in that case.
    /// </remarks>
    public void PostEvent(Ax25Event evt)
    {
        ArgumentNullException.ThrowIfNull(evt);
        DispatchEvent(evt);
        DrainIFrameQueue();
    }

    private void DispatchEvent(Ax25Event evt)
    {
        if (!transitionsByState.TryGetValue(CurrentState, out var stateTransitions))
        {
            throw new InvalidOperationException($"no transitions defined for current state '{CurrentState}'");
        }

        // Expose the trigger to frame-aware guard bindings + the
        // dispatcher's TransitionContext. Cleared in the finally so
        // a thrown action doesn't leave stale state on the session.
        CurrentTrigger = evt;
        try
        {
            TransitionSpec? match = null;
            foreach (var t in stateTransitions)
            {
                if (!string.Equals(t.On, evt.Name, StringComparison.Ordinal)) continue;
                if (!guards.Evaluate(t.Guard)) continue;
                match = t;
                break;
            }

            if (match is null)
            {
                onUnhandledEvent?.Invoke(evt);
                return;
            }

            // Capture the armed-timer set before running the actions. If an
            // action throws part-way through (e.g. an unexpected verb for this
            // trigger), the side-effects already applied stay, but we restore
            // the pre-transition timers so a half-applied transition can't leave
            // the link watchdog (T1) cancelled and the session silently wedged —
            // it stays live so T1 / N2 drive recovery or a clean disconnect.
            // CurrentState is only advanced on success. (m0lte/packet.net#225)
            var timerState = scheduler.CaptureState();
            try
            {
                var tx = new TransitionContext(Context, scheduler, evt);
                ApplyPreExecutionQuirks(match);
                SdlLoopExecutor.Execute(match.Actions, match.Loops, dispatcher, guards, tx);
                CurrentState = ResolveNextState(match);
            }
            catch
            {
                scheduler.RestoreState(timerState);
                throw;
            }

            // Transition committed (state advanced, timers kept) — notify
            // observers. Raised here rather than inside the try so a throwing
            // observer can't trip the timer-rollback path.
            TransitionFired?.Invoke(this, match);
        }
        finally
        {
            CurrentTrigger = null;
        }
    }

    /// <summary>
    /// Compute the state a just-committed transition advances to — normally
    /// <see cref="TransitionSpec.Next"/>, but with the figc4.2 connect-routing
    /// defect corrected when the relevant quirk is on.
    /// </summary>
    /// <remarks>
    /// <para>
    /// figc4.2 routes the <c>Disconnected</c> <c>DL-CONNECT request</c>
    /// (<c>t03_dl_connect_request</c>) unconditionally to <c>AwaitingConnection</c>,
    /// with no version branch — so a v2.2-preferred connect (which the figc4.7
    /// <c>Establish_Data_Link</c> subroutine correctly sends as a <i>SABME</i>,
    /// branching on <c>mod_128</c>) ends up parked in the mod-8 establishment state.
    /// That state's T1 retry resends a hardcoded SABM (downgrading the link) and it
    /// has no FRMR handler (so the §975 v2.0 fallback can't fire). When
    /// <see cref="Ax25SessionQuirks.Ax25Spec44Mod128ConnectRoutesToV22"/> is on
    /// (default) and the link is extended at dispatch time, the target is rewritten
    /// to <c>AwaitingV22Connection</c> (figc4.6), which resends SABME on retry and
    /// handles the FRMR/DM fallbacks. See <see cref="Ax25SessionQuirks"/> for the
    /// full rationale, the graphml citation, and the direwolf cross-reference.
    /// </para>
    /// <para>
    /// Scope is deliberately tight: only the exact <c>Disconnected</c> DL-CONNECT
    /// transition (matched on <c>From</c> + <c>On</c> + <c>Next</c>, so it survives a
    /// transition-id renumber), only when <see cref="Ax25SessionContext.IsExtended"/>.
    /// Every other transition is returned unchanged — a mod-8 connect (not extended)
    /// keeps figc4.2's <c>AwaitingConnection</c> target, and the figc4.6 FRMR fallback
    /// (<c>t14</c>) which forces version 2.0 — clearing <c>IsExtended</c> — and routes
    /// to <c>AwaitingConnection</c> is untouched, so the redirect is self-consistent
    /// with the fallback (a later connect from that mod-8 state stays mod-8).
    /// </para>
    /// </remarks>
    private string ResolveNextState(TransitionSpec match)
    {
        if (Context.Quirks.Ax25Spec44Mod128ConnectRoutesToV22
            && Context.IsExtended
            && string.Equals(match.From, "Disconnected", StringComparison.Ordinal)
            && string.Equals(match.On, "DL_CONNECT_request", StringComparison.Ordinal)
            && string.Equals(match.Next, "AwaitingConnection", StringComparison.Ordinal))
        {
            return "AwaitingV22Connection";
        }

        return match.Next;
    }

    /// <summary>
    /// Apply quirks that must take effect <em>before</em> a transition's actions
    /// run. Currently just the figc4.6 t14 FRMR-fallback ordering fix
    /// (<see cref="Ax25SessionQuirks.Ax25Spec45FrmrFallbackReestablishesV20"/>).
    /// </summary>
    /// <remarks>
    /// figc4.6's <c>FRMR received</c> handler (t14) draws <c>Establish Data Link</c>
    /// <em>before</em> <c>Set Version 2.0</c>. <c>Establish_Data_Link</c> (figc4.7)
    /// branches on <c>mod_128</c>, so while the link is still extended the §975 v2.0
    /// fallback re-establishes with a <i>SABME</i> — the opposite of what a FRMR
    /// (which only a pre-v2.2 peer sends) calls for. Forcing version 2.0
    /// (<c>IsExtended=false</c>) up front — before the actions — makes
    /// <c>Establish_Data_Link</c> emit a <i>SABM</i>; the figure's later
    /// <c>Set Version 2.0</c> action then re-applies it as a no-op. This mirrors
    /// direwolf's FRMR handler, which calls <c>set_version_2_0</c> before
    /// <c>establish_data_link</c> ("Erratum: Need to force v2.0. This is not in flow
    /// chart."). Scoped to the <c>AwaitingV22Connection</c> <c>FRMR_received</c>
    /// transition; inert otherwise.
    /// </remarks>
    private void ApplyPreExecutionQuirks(TransitionSpec match)
    {
        if (Context.Quirks.Ax25Spec45FrmrFallbackReestablishesV20
            && Context.IsExtended
            && string.Equals(match.From, "AwaitingV22Connection", StringComparison.Ordinal)
            && string.Equals(match.On, "FRMR_received", StringComparison.Ordinal))
        {
            Context.IsExtended = false;
        }
    }

    // Loop expansion (SDL loop_while, Packet.Ax25.Sdl 0.7.0+) lives in
    // SdlLoopExecutor, shared with the subroutine path so Invoke_Retransmission
    // — itself a subroutine — iterates identically.

    /// <summary>
    /// After every event dispatch, if <see cref="Ax25SessionContext.IFrameQueue"/>
    /// has entries and conditions allow transmission (peer not busy, V(s)
    /// within window k beyond V(a)), pop entries one at a time and
    /// synthesise <see cref="IFramePopsOffQueue"/> events. Stops when the
    /// queue empties, the peer goes busy, the send window fills, or the
    /// session leaves a state that handles the synthetic event (e.g. moves
    /// to Disconnected).
    /// </summary>
    /// <remarks>
    /// This is the I-frame session-loop machinery the SDL figures rely on.
    /// figc4.4 t18 enqueues data via <c>push_on_I_frame_queue</c>; t19/t20
    /// handle the synthetic pop event by emitting the I-frame on the wire.
    /// Without this drain, t19/t20 would never fire and DL_DATA_request
    /// would silently sit on the queue.
    /// </remarks>
    private void DrainIFrameQueue()
    {
        while (Context.IFrameQueue.Count > 0 && CanTransmitIFrame())
        {
            var entry = Context.IFrameQueue.Dequeue();
            DispatchEvent(new IFramePopsOffQueue(entry.Data, entry.Pid));
        }
    }

    /// <summary>
    /// True if the link conditions allow an I-frame to be sent right now —
    /// the link is in an information-transfer state, the peer isn't busy, and the
    /// send window isn't full. Mirrors the figc4.4 t19/t20 guards
    /// (<c>peer_receiver_busy=No</c> + <c>V_s_eq_V_a_plus_k=No</c>).
    /// </summary>
    /// <remarks>
    /// I-frames are only transmitted from <c>Connected</c> (figc4.4) and
    /// <c>TimerRecovery</c> (figc4.5). In the establishment / release / disconnected
    /// states, data handed down (DL-DATA-request) is *buffered* on the I-frame queue
    /// and not sent until the link comes up — figc4.3's AwaitingConnection
    /// <c>DL_DATA_request</c> / <c>I_frame_pops_off_queue</c> both just "Push Frame
    /// on Queue". Without this state gate the drain would pop the just-queued frame
    /// in AwaitingConnection, routing <c>I_frame_pops_off_queue</c> into a push verb
    /// that has no DL-DATA trigger to read (it threw), and a faithful re-queue would
    /// instead spin the drain. The buffered frames flush automatically: the
    /// post-dispatch drain runs again after the UA_received transition advances the
    /// state to Connected.
    /// </remarks>
    private bool CanTransmitIFrame()
    {
        if (CurrentState is not ("Connected" or "TimerRecovery")) return false;
        if (Context.PeerReceiverBusy) return false;
        int outstanding = (Context.VS - Context.VA + Context.Modulus) % Context.Modulus;
        return outstanding < Context.K;
    }
}
