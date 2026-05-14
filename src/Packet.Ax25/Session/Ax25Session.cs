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

        var tx = new TransitionContext(Context, scheduler, evt);
        dispatcher.Execute(match.Actions, tx);
        CurrentState = match.Next;
    }

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
    /// peer not busy, send window not full. Mirrors the figc4.4 t19/t20
    /// guards (<c>peer_receiver_busy=No</c> + <c>V_s_eq_V_a_plus_k=No</c>).
    /// </summary>
    private bool CanTransmitIFrame()
    {
        if (Context.PeerReceiverBusy) return false;
        int outstanding = (Context.VS - Context.VA + Context.Modulus) % Context.Modulus;
        return outstanding < Context.K;
    }
}
