using Packet.Ax25.Sdl;
using SdlEvent = Packet.Ax25.Sdl.Ax25Event;

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
/// state, filters by the typed <see cref="SdlEvent"/> the event maps to,
/// evaluates guards, picks the matching
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

    // Run-to-completion machinery: events posted re-entrantly (from inside an
    // action's signal handler — e.g. a construction site granting LM-SEIZE by
    // posting LM-SEIZE-confirm back) are queued here and dispatched after the
    // in-flight transition commits. See PostEvent's remarks. (#327)
    private readonly Queue<Ax25Event> deferredEvents = new();
    private readonly object dispatchGate = new();
    private bool dispatching;

    // Early-inbound replay buffer. A consumer that wraps this session (the node's
    // Ax25NodeConnection) subscribes to DataLinkSignalEmitted, but for an OUTBOUND
    // session that is already connected by the time it is wrapped, the peer may have
    // sent data — a node's connect banner is the canonical case — in the window
    // between connect and subscribe, and the event fires it into the void. We buffer
    // inbound DL-DATA indications here (under dispatchGate, so it's consistent with
    // emission) until the next replay-consumer attaches via AttachConsumerWithReplay,
    // which atomically replays then subscribes and disarms. Bounded so a session that
    // is only ever tapped via the raw event (a NET/ROM interlink) can't grow it.
    //
    // Armed PER CONNECTION, not once per object: Ax25Listener caches and REUSES an
    // Ax25Session per (local, remote) across connect/disconnect cycles (it evicts only
    // on LRU overflow, never on disconnect), so a one-shot buffer would stay disarmed
    // on every re-dial and silently drop the banner on the 2nd+ connect to a peer —
    // packet.net#659, the bug the first-connect replay test never saw. RaiseDataLinkSignal
    // re-arms it whenever the link (re)establishes (DL-CONNECT confirm/indication),
    // which always precedes the peer's post-connect data, so each connection replays
    // its own early inbound exactly as a fresh session would.
    private List<DataLinkSignal>? earlyInbound = new();
    private const int MaxEarlyInbound = 32;

    // ax25spec#9 (Ax25Spec9AckProgressResetsRc): set when a committed transition
    // advances V(A); consumed at the next T1 expiry to clamp RC. See
    // PreClampRetryCountOnT1Expiry.
    private bool vaAdvancedSinceT1Expiry;

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
    /// own per-session queue (for <see cref="Ax25Listener.ConnectAsync(Packet.Core.Callsign, System.Threading.CancellationToken)"/>
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
        // Serialise the buffer-append with AttachConsumerWithReplay's replay+subscribe so a
        // late-attaching consumer neither misses nor double-receives a signal. The dispatch
        // path already holds dispatchGate (reentrant here, ~free); a custom rig calling this
        // directly is simply serialised, which is safe.
        lock (dispatchGate)
        {
            // (Re-)arm the early-inbound buffer as the link comes up. A cached session
            // re-dialled to the same peer arrives here with the buffer disarmed by the
            // PREVIOUS connection's consumer; re-arming on the fresh connect confirm/
            // indication — which the SDL emits before any post-connect I-frame — makes
            // this connection replay its own banner just like a fresh session would
            // (packet.net#659). A connect signal is never itself buffered inbound data.
            if (signal is DataLinkConnectConfirm or DataLinkConnectIndication)
            {
                earlyInbound = new();
            }
            else if (earlyInbound is { } buffered && buffered.Count < MaxEarlyInbound && signal is DataLinkDataIndication)
            {
                buffered.Add(signal);
            }
            DataLinkSignalEmitted?.Invoke(this, signal);
        }
    }

    /// <summary>
    /// Subscribe <paramref name="handler"/> to <see cref="DataLinkSignalEmitted"/>, first
    /// replaying any inbound DL-DATA indications that were emitted <em>before</em> this call —
    /// the early-inbound buffer. Atomic with respect to emission (both take the session's
    /// dispatch gate), so a signal in flight is delivered to the handler exactly once, with no
    /// loss and no duplication. Used by an outbound consumer (the node's Ax25NodeConnection)
    /// that can only wrap the session after <see cref="Ax25Listener.ConnectAsync(Packet.Core.Callsign, System.Threading.CancellationToken)"/> has already
    /// returned a connected link — closing the window in which a peer's immediate greeting
    /// (e.g. a node's connect banner) would otherwise be dropped. The attach disarms the buffer
    /// (this consumer now owns the live stream); <see cref="RaiseDataLinkSignal"/> re-arms it on
    /// the next connect confirm/indication so a re-dialled cached session replays afresh. Raw
    /// <c>DataLinkSignalEmitted += </c> subscribers are unaffected either way.
    /// </summary>
    public void AttachConsumerWithReplay(EventHandler<DataLinkSignal> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        lock (dispatchGate)
        {
            if (earlyInbound is { } buffered)
            {
                foreach (var signal in buffered)
                {
                    handler(this, signal);
                }
                earlyInbound = null;   // disarm: this consumer now owns the live stream
            }
            DataLinkSignalEmitted += handler;
        }
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
    /// <param name="guards">Guard evaluator — evaluates a transition's typed <see cref="GuardTerm"/> conjunction against the session state.</param>
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
    /// the current state, picks the first whose typed <c>On</c>
    /// (<see cref="SdlEvent"/>) matches the event (via <c>ToSdlEvent</c>) and
    /// whose guard evaluates true, runs its actions, and advances to
    /// <c>Next</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Events with no matching transition are passed to
    /// <see cref="onUnhandledEvent"/> (or silently dropped). The current
    /// state is unchanged in that case.
    /// </para>
    /// <para>
    /// Dispatch is run-to-completion: a <see cref="PostEvent"/> issued from
    /// <em>inside</em> a dispatch (an action's signal handler posting back into
    /// the session — e.g. the construction sites granting <c>LM-SEIZE</c> by
    /// answering the <c>LM-SEIZE Request</c> signal with an
    /// <see cref="LmSeizeConfirm"/>) is deferred and dispatched after the
    /// in-flight transition commits, in post order. Inline re-entrant dispatch
    /// would corrupt <see cref="CurrentTrigger"/>/<see cref="CurrentState"/>
    /// (the outer transition's <c>Next</c> would clobber the inner's), and in
    /// figc4.3's I-frame path the <c>LM-SEIZE Request</c> action runs
    /// <em>before</em> <c>Set Ack Pending</c>, so an inline confirm would
    /// match the no-ack-pending branch and silently drop the delayed ack
    /// (packet-net/packet.net#327). If a dispatch throws, any not-yet-dispatched
    /// deferred events are dropped — consistent with the #225 contract where a
    /// failed transition keeps the session alive and leaves recovery to
    /// T1/N2 rather than running follow-on work against half-applied state.
    /// </para>
    /// </remarks>
    public void PostEvent(Ax25Event evt)
    {
        ArgumentNullException.ThrowIfNull(evt);

        // Cross-thread serialisation. Real posters genuinely race: the
        // listener's inbound pump (frame events), timer-expiry callbacks
        // (SystemTimerScheduler fires them on TimeProvider timer threads), and
        // upper-layer callers (ConnectAsync / SendData on whatever thread the
        // app uses). Without the gate, two threads can both observe
        // `dispatching == false` and dispatch concurrently — corrupting the
        // deferred Queue and the run-to-completion invariant — or one thread's
        // enqueue can be wiped by the other's `deferredEvents.Clear()` (a
        // silently lost event; found by tools/Packet.LinkBench bulk transfers).
        // Monitor is reentrant, so a re-entrant post from inside a dispatch on
        // the SAME thread (an action's signal handler posting back — the #327
        // case) passes straight through to the deferred queue exactly as
        // before; only genuinely concurrent posters now wait their turn.
        lock (dispatchGate)
        {
            if (dispatching)
            {
                deferredEvents.Enqueue(evt);
                return;
            }

            dispatching = true;
            try
            {
                DispatchEvent(evt);
                DrainIFrameQueue();
                while (deferredEvents.TryDequeue(out var deferred))
                {
                    DispatchEvent(deferred);
                    DrainIFrameQueue();
                }
            }
            finally
            {
                dispatching = false;
                deferredEvents.Clear();
            }
        }
    }

    private void DispatchEvent(Ax25Event evt)
    {
        if (!transitionsByState.TryGetValue(CurrentState, out var stateTransitions))
        {
            throw new InvalidOperationException($"no transitions defined for current state '{CurrentState}'");
        }

        // ax25spec#9 (Ax25Spec9AckProgressResetsRc), step 2 of 2: the figures
        // only reset RC on the fully-acked Timer-Recovery checkpoint
        // (t18/t21/t23 …_yes_yes_yes → Connected, RCAssign0), so a sustained
        // transfer that lives in Timer Recovery with frames always in flight
        // ratchets RC across a WORKING link and dies (t21_t1_expiry_yes_no:
        // DL-ERROR I → DM) at the N2'th lifetime T1 hiccup — reproduced by
        // tools/Packet.LinkBench over net-sim. If V(A) advanced since the last
        // T1 expiry, the link is demonstrably alive, so this expiry is the
        // FIRST of a new consecutive-failure run: clamp RC to 1 before the
        // RCEqN2 guard is evaluated. Clamping (not zeroing) keeps Select_T1's
        // RC==0 Karn branch meaning what the figures intend — "no
        // retransmission in progress, round-trip sample is clean".
        if (evt is T1Expiry && Context.Quirks.Ax25Spec9AckProgressResetsRc)
        {
            if (vaAdvancedSinceT1Expiry && Context.RC > 1)
            {
                Context.RC = 1;
            }
            vaAdvancedSinceT1Expiry = false;
        }

        // Expose the trigger to frame-aware guard bindings + the
        // dispatcher's TransitionContext. Cleared in the finally so
        // a thrown action doesn't leave stale state on the session.
        CurrentTrigger = evt;
        try
        {
            var on = ToSdlEvent(evt);
            TransitionSpec? match = null;
            foreach (var t in stateTransitions)
            {
                if (t.On != on)
                {
                    continue;
                }

                if (!guards.Evaluate(t.Guard))
                {
                    continue;
                }

                match = t;
                break;
            }

            if (match is null)
            {
                onUnhandledEvent?.Invoke(evt);
                return;
            }

            // figc4.6 DM-no-degrade gap (Ax25Spec48DmRejectionDegradesToV20): a DM
            // received in AwaitingV22Connection means the peer can't do v2.2, so it
            // must degrade to v2.0/SABM exactly like the FRMR fallback — NOT honour
            // the figure-literal F=1 teardown. Substitute the matched DM transition
            // for figc4.6's own t14_frmr_received transition (the v2.0 re-establish:
            // SRT reset → Establish_Data_Link → AwaitingConnection). The companion
            // IsExtended=false force (ApplyPreExecutionQuirks) makes Establish emit
            // SABM. See ResolveDmDegradeMatch for the full rationale + scope.
            match = ResolveDmDegradeMatch(match, stateTransitions);

            // Capture the armed-timer set before running the actions. If an
            // action throws part-way through (e.g. an unexpected verb for this
            // trigger), the side-effects already applied stay, but we restore
            // the pre-transition timers so a half-applied transition can't leave
            // the link watchdog (T1) cancelled and the session silently wedged —
            // it stays live so T1 / N2 drive recovery or a clean disconnect.
            // CurrentState is only advanced on success. (packet-net/packet.net#225)
            var timerState = scheduler.CaptureState();
            var vaBefore = Context.VA;
            var stateBefore = CurrentState;
            try
            {
                var tx = new TransitionContext(Context, scheduler, evt);
                ApplyPreExecutionQuirks(match);
                // Advance CurrentState BEFORE running the actions, not after. The action
                // chain raises the upward DL signals (DL-CONNECT-confirm, DL-DISCONNECT-…)
                // that callers block on — e.g. Ax25Listener.ConnectAsync resolves on
                // DataLinkConnectConfirm. With the advance ordered after the actions, a
                // caller resuming from ConnectAsync (on another thread) could read
                // CurrentState a hair before the dispatch thread settled it and see the
                // pre-transition state ("AwaitingV22Connection" instead of "Connected") —
                // a real race that flaked the interop connect tests under CPU contention,
                // and a latent one behind every "await connect-confirm; assert Connected"
                // assertion. Settling the state first makes the signal and the observable
                // state consistent: when the confirm fires, CurrentState is already the
                // next state. Nothing in the action/guard path reads CurrentState (guards
                // matched above off the pre-transition state; SDL verbs work on Context),
                // so this only changes what synchronous signal observers see — correctly.
                CurrentState = ResolveNextState(match);
                SdlLoopExecutor.Execute(match.Actions, match.Loops, dispatcher, guards, tx);
            }
            catch
            {
                // #225: a half-applied transition must not advance CurrentState — restore
                // the pre-transition state (and timers) so the link watchdog stays live.
                scheduler.RestoreState(timerState);
                CurrentState = stateBefore;
                throw;
            }

            // ax25spec#9 (Ax25Spec9AckProgressResetsRc), step 1 of 2: note that
            // this transition advanced V(A) — the peer acknowledged NEW data.
            // The RC clamp itself happens at the next T1 expiry (see
            // PreClampRetryCountOnT1Expiry); RC is deliberately NOT zeroed here
            // because RC==0 is also the figures' Karn signal to Select_T1 ("no
            // retransmission in progress — safe to sample the round trip"), and
            // a mid-recovery zero would feed retransmit-polluted samples into
            // the SRT estimator.
            if (Context.VA != vaBefore)
            {
                vaAdvancedSinceT1Expiry = true;
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
    /// figc4.6 DM-no-degrade gap (<see cref="Ax25SessionQuirks.Ax25Spec48DmRejectionDegradesToV20"/>):
    /// when a <c>DM received</c> fires in <c>AwaitingV22Connection</c> and the quirk is on,
    /// substitute the matched DM transition (either F-branch — the F=1
    /// <c>t11_dm_received_yes</c> teardown <i>or</i> the F=0 <c>t11_dm_received_no</c>
    /// passive drop) for figc4.6's <c>FRMR received</c> transition, so a DM degrades the
    /// link to v2.0 and actively re-establishes via SABM — exactly as the FRMR fallback
    /// (<see cref="Ax25SessionQuirks.Ax25Spec45FrmrFallbackReestablishesV20"/>) does for a
    /// peer that signals "no v2.2" with an FRMR instead of a DM. The companion
    /// <see cref="ApplyPreExecutionQuirks"/> step forces
    /// <see cref="Ax25SessionContext.IsExtended"/> = <c>false</c> before the actions run,
    /// so <c>Establish_Data_Link</c> (figc4.7, branches on <c>mod_128</c>) emits a SABM.
    /// </summary>
    /// <remarks>
    /// Scope is deliberately tight: only a <c>DMReceived</c> trigger, only from
    /// <c>AwaitingV22Connection</c>, only while the link is still extended. A DM received
    /// in <c>AwaitingConnection</c> (in response to a later SABM) stays a genuine v2.0
    /// refusal → <c>Disconnected</c> (figc4.2 t03), untouched. If the FRMR transition is
    /// somehow absent from the state table the original DM match is returned unchanged
    /// (defensive — every figc4.6 table carries t14_frmr_received). See
    /// <see cref="Ax25SessionQuirks.Ax25Spec48DmRejectionDegradesToV20"/> for the full
    /// rationale, the wire evidence, and the direwolf cross-reference.
    /// </remarks>
    private TransitionSpec ResolveDmDegradeMatch(TransitionSpec match, IReadOnlyList<TransitionSpec> stateTransitions)
    {
        if (!Context.Quirks.Ax25Spec48DmRejectionDegradesToV20
            || !Context.IsExtended
            || match.On != SdlEvent.DMReceived
            || !string.Equals(match.From, "AwaitingV22Connection", StringComparison.Ordinal))
        {
            return match;
        }

        foreach (var t in stateTransitions)
        {
            if (t.On == SdlEvent.FRMRReceived)
            {
                return t;
            }
        }

        return match;   // defensive: figc4.6 always carries t14_frmr_received
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
            && match.On == SdlEvent.DLCONNECTRequest
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
            && match.On == SdlEvent.FRMRReceived)
        {
            Context.IsExtended = false;
        }

        // figc4.6 DM-no-degrade gap (Ax25Spec48DmRejectionDegradesToV20): a DM in
        // AwaitingV22Connection has had its match substituted for t14_frmr_received
        // (ResolveDmDegradeMatch), so match.On is now FRMRReceived and the Spec45
        // branch above already forces v2.0 when Spec45 is on. Key this companion
        // force on the actual DM trigger so Spec48 stays self-contained even if
        // Spec45 is off — without it, Establish_Data_Link would re-send a SABME
        // (still extended) and the degrade would loop against the non-v2.2 peer.
        if (Context.Quirks.Ax25Spec48DmRejectionDegradesToV20
            && Context.IsExtended
            && CurrentTrigger is DmReceived
            && string.Equals(match.From, "AwaitingV22Connection", StringComparison.Ordinal))
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
        if (CurrentState is not ("Connected" or "TimerRecovery"))
        {
            return false;
        }

        if (Context.PeerReceiverBusy)
        {
            return false;
        }

        int outstanding = (Context.VS - Context.VA + Context.Modulus) % Context.Modulus;
        return outstanding < Context.EffectiveWindow;
    }

    /// <summary>
    /// Map a runtime <see cref="Ax25Event"/> (the in-memory event record the
    /// orchestrator dispatches, carrying any attached frame/payload) to the
    /// typed <see cref="SdlEvent"/> the codegen-emitted
    /// <see cref="TransitionSpec.On"/> carries. The mapping is on the event's
    /// CLR type — exhaustive over the runtime event vocabulary, no string
    /// comparison or name normalisation — so dispatch is a pure enum compare.
    /// </summary>
    private static SdlEvent ToSdlEvent(Ax25Event evt) => evt switch
    {
        // ─── Upper-layer (Layer-3 → Data-Link) primitives ──────────────
        DlConnectRequest => SdlEvent.DLCONNECTRequest,
        DlDisconnectRequest => SdlEvent.DLDISCONNECTRequest,
        DlDataRequest => SdlEvent.DLDATARequest,
        DlUnitDataRequest => SdlEvent.DLUNITDATARequest,
        DlFlowOffRequest => SdlEvent.DLFLOWOFFRequest,
        DlFlowOnRequest => SdlEvent.DLFLOWONRequest,

        // ─── Management Data-Link primitives ────────────────────────────
        MdlNegotiateRequest => SdlEvent.MDLNEGOTIATERequest,
        MdlNegotiateConfirm => SdlEvent.MDLNEGOTIATEConfirm,
        MdlErrorIndicate => SdlEvent.MDLERRORIndicate,

        // ─── Frame-received events ──────────────────────────────────────
        IFrameReceived => SdlEvent.IReceived,
        RrReceived => SdlEvent.RRReceived,
        RnrReceived => SdlEvent.RNRReceived,
        RejReceived => SdlEvent.REJReceived,
        SrejReceived => SdlEvent.SREJReceived,
        UiReceived => SdlEvent.UIReceived,
        SabmReceived => SdlEvent.SABMReceived,
        SabmeReceived => SdlEvent.SABMEReceived,
        DiscReceived => SdlEvent.DISCReceived,
        UaReceived => SdlEvent.UAReceived,
        DmReceived => SdlEvent.DMReceived,
        FrmrReceived => SdlEvent.FRMRReceived,
        XidReceived => SdlEvent.XIDReceived,
        XidResponseReceived => SdlEvent.XIDResponseReceived,
        TestReceived => SdlEvent.TESTReceived,
        IOrSCommandReceived => SdlEvent.IOrSCommandReceived,

        // ─── Internal + catch-all events ────────────────────────────────
        IFramePopsOffQueue => SdlEvent.IFramePopsOffQueue,
        AllOtherCommands => SdlEvent.AllOtherCommands,
        AllOtherPrimitivesFromLowerLayer => SdlEvent.AllOtherPrimitivesFromLowerLayer,
        AllOtherPrimitivesFromUpperLayer => SdlEvent.AllOtherPrimitivesFromUpperLayer,
        ControlFieldError => SdlEvent.ControlFieldError,
        InfoNotPermittedInFrame => SdlEvent.InfoNotPermittedInFrame,
        UOrSFrameLengthError => SdlEvent.UOrSFrameLengthError,

        // ─── Link-multiplexer + timer expiries ──────────────────────────
        LmSeizeConfirm => SdlEvent.LMSEIZEConfirm,
        Tm201Expiry => SdlEvent.TM201Expiry,
        T1Expiry => SdlEvent.T1Expiry,
        T2Expiry => SdlEvent.T2Expiry,
        T3Expiry => SdlEvent.T3Expiry,

        _ => throw new InvalidOperationException(
            $"no SDL-event mapping for runtime event '{evt.GetType().Name}' ('{evt.Name}') — " +
            "add a case to Ax25Session.ToSdlEvent."),
    };
}
