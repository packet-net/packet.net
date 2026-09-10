namespace Packet.Ax25.Session;

/// <summary>
/// Per-transition execution context handed to the <see cref="IActionDispatcher"/>.
/// Bundles every piece of state an action verb might read or mutate while
/// running one transition's action chain.
/// </summary>
/// <remarks>
/// <para>
/// One <see cref="TransitionContext"/> is created per
/// <see cref="Ax25Session.PostEvent"/> call. Its lifetime spans a single
/// action-chain execution. The dispatcher resets the
/// <see cref="Pending"/> frame builder at entry, so accumulated state
/// from a previous transition cannot leak forward.
/// </para>
/// <para>
/// Why this exists: the SDL action vocabulary includes verbs that read
/// fields of the triggering frame (e.g. <c>V(a) := N(r)</c> assigns the
/// session's V(A) from the incoming frame's N(R)) and verbs that
/// accumulate fields on a forthcoming outgoing frame (e.g.
/// <c>N(r) := V(r)</c> sets the pending frame's N(R) to be consumed by
/// the next <c>signal_lower</c>). The previous dispatcher signature
/// (separate <c>context</c> + <c>scheduler</c> args) couldn't express
/// either pattern. This wraps them together.
/// </para>
/// </remarks>
public sealed class TransitionContext
{
    /// <summary>The session's mutable per-connection state.</summary>
    public Ax25SessionContext Session { get; }

    /// <summary>Timer scheduler the dispatcher arms / cancels.</summary>
    public ITimerScheduler Scheduler { get; }

    /// <summary>The event that fired this transition.</summary>
    public Ax25Event Trigger { get; }

    /// <summary>
    /// The AX.25 frame attached to <see cref="Trigger"/>, or <c>null</c> if
    /// the trigger is an upper-layer primitive / timer expiry / internal
    /// event. Frame-receipt triggers (e.g. <see cref="IFrameReceived"/>,
    /// <see cref="RrReceived"/>) carry the inbound frame here.
    /// </summary>
    public Ax25Frame? IncomingFrame { get; }

    /// <summary>
    /// Builder accumulating fields for the next outgoing frame in the
    /// chain. Processing verbs (<c>N(r) := V(r)</c>, <c>F := P</c>, …)
    /// write here; the subsequent <c>signal_lower</c> verb reads here to
    /// construct a complete outgoing frame spec. Reset at the start of
    /// each <see cref="IActionDispatcher.Execute"/> call.
    /// </summary>
    public PendingFrame Pending { get; } = new();

    /// <summary>
    /// A stored out-of-sequence I-frame just dequeued by the
    /// <c>Retrieve Stored V(r) I Frame</c> verb, staged for the immediately
    /// following <c>DL-DATA Indication</c> to deliver. Null when the
    /// indication should deliver the triggering frame instead. This is the
    /// retrieve/deliver split for the figc4.4 / figc4.5 stored-frame drain
    /// loop: the figure draws retrieval and delivery as two separate actions,
    /// so a single iteration stages here then delivers.
    /// </summary>
    public (ReadOnlyMemory<byte> Info, byte Pid)? RetrievedStoredFrame { get; set; }

    /// <summary>
    /// True once this transition has replayed a previously-sent I frame inline
    /// (<c>Push Old I Frame onto Queue</c> in figc4.7's Invoke_Retransmission loop,
    /// or figc4.4/4.5's <c>Push Old I Frame N(r) on Queue</c>). In the figure those
    /// frames are re-queued and pop off after the arm, each pop clearing
    /// Acknowledge Pending; the runtime emits them at once with their original
    /// N(s) and runs the pop's acknowledgement bookkeeping there, so a
    /// <c>Set Acknowledge Pending</c> later in the same chain must not re-set the
    /// flag those pops would have cleared (packet-net/packet.net#812).
    /// </summary>
    public bool RetransmittedInline { get; internal set; }

    /// <summary>Construct a transition context, extracting any frame attached to the trigger.</summary>
    public TransitionContext(Ax25SessionContext session, ITimerScheduler scheduler, Ax25Event trigger)
    {
        Session = session ?? throw new ArgumentNullException(nameof(session));
        Scheduler = scheduler ?? throw new ArgumentNullException(nameof(scheduler));
        Trigger = trigger ?? throw new ArgumentNullException(nameof(trigger));
        IncomingFrame = ExtractFrame(trigger);
    }

    private static Ax25Frame? ExtractFrame(Ax25Event e) => e switch
    {
        IFrameReceived f => f.Frame,
        RrReceived f => f.Frame,
        RnrReceived f => f.Frame,
        RejReceived f => f.Frame,
        SrejReceived f => f.Frame,
        UiReceived f => f.Frame,
        SabmReceived f => f.Frame,
        SabmeReceived f => f.Frame,
        DiscReceived f => f.Frame,
        UaReceived f => f.Frame,
        DmReceived f => f.Frame,
        FrmrReceived f => f.Frame,
        XidReceived f => f.Frame,
        XidResponseReceived f => f.Frame,
        TestReceived f => f.Frame,
        IOrSCommandReceived f => f.Frame,
        AllOtherCommands f => f.Frame,
        _ => null,
    };
}

/// <summary>
/// Fields being accumulated for the next outgoing frame in a transition's
/// action chain. The <c>signal_lower</c> action that follows reads these
/// to construct a complete frame spec.
/// </summary>
/// <remarks>
/// <para>
/// Nullable so the dispatcher can distinguish "not assigned" from
/// "assigned to zero". A processing verb sets the relevant slot; the
/// signal_lower verb reads it and (optionally) checks it was set.
/// </para>
/// <para>
/// Today only the read side of <c>V(a) := N(r)</c> uses
/// <see cref="TransitionContext.IncomingFrame"/>. The write side of the
/// SDL vocabulary (<c>N(r) := V(r)</c>, <c>N(s) := V(s)</c>,
/// <c>F := P</c>, <c>p := 0</c>) will populate <see cref="PendingFrame"/>
/// in a later PR, and frame emission will be refactored to consume it.
/// </para>
/// </remarks>
public sealed class PendingFrame
{
    /// <summary>N(R) bits to set on the outgoing frame, or null if not yet assigned.</summary>
    public byte? Nr { get; set; }

    /// <summary>N(S) bits to set on the outgoing frame, or null if not yet assigned.</summary>
    public byte? Ns { get; set; }

    /// <summary>P/F bit value to set on the outgoing frame, or null if not yet assigned.</summary>
    public bool? PfBit { get; set; }
}
