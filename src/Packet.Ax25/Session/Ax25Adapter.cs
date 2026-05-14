using Packet.Ax25.Sdl;
using Packet.Core;

namespace Packet.Ax25.Session;

/// <summary>
/// Wires an <see cref="Ax25Session"/> + <see cref="ActionDispatcher"/>
/// pair to byte-level I/O. Outbound: frame specs emitted by the
/// dispatcher are converted to <see cref="Ax25Frame"/>s via
/// <see cref="FrameSpecExtensions"/> and serialised to bytes, handed off
/// to the supplied <c>sendBytes</c> callback. Inbound:
/// <see cref="OnReceivedAx25Bytes"/> parses the bytes, classifies them
/// via <see cref="Ax25FrameClassifier"/>, and posts the resulting event
/// to the session.
/// </summary>
/// <remarks>
/// <para>
/// This is the glue layer that turns the state machine into something
/// that can talk to a real KISS TNC or AXUDP peer. The adapter doesn't
/// care which: it deals in raw AX.25 frame bytes (no KISS framing, no
/// AXUDP headers, no FCS unless the caller asks). The caller wraps the
/// adapter's outgoing bytes in whatever framing the transport needs.
/// </para>
/// <para>
/// <c>sendBytes</c> receives the AX.25 frame body without FCS — typical
/// for KISS, which adds its own framing. Callers targeting AXUDP-with-CRC
/// (Xrouter) should pass through <see cref="Ax25Frame.ToBytesWithFcs"/>
/// instead of <see cref="Ax25Frame.ToBytes"/>; this adapter chooses the
/// FCS-less form because KISS-attached TNCs add the FCS at the radio.
/// </para>
/// </remarks>
public sealed class Ax25Adapter
{
    private readonly Action<ReadOnlyMemory<byte>> sendBytes;

    /// <summary>The wrapped session — exposed so callers can drive it via <see cref="Ax25Session.PostEvent"/>.</summary>
    public Ax25Session Session { get; }

    /// <summary>The wrapped dispatcher.</summary>
    public ActionDispatcher Dispatcher { get; }

    /// <summary>Per-session state shared with the dispatcher.</summary>
    public Ax25SessionContext Context => Session.Context;

    /// <summary>
    /// Construct an adapter. The session and dispatcher are built and
    /// wired internally; <paramref name="sendBytes"/> receives every
    /// outbound AX.25 frame's body bytes (no FCS).
    /// </summary>
    /// <param name="context">Session state (addressing, sequence variables, queues).</param>
    /// <param name="scheduler">Timer scheduler driving T1/T2/T3.</param>
    /// <param name="transitions">State → transitions map (typically from the codegen tables).</param>
    /// <param name="initialState">Starting state (typically <c>"Disconnected"</c>).</param>
    /// <param name="sendBytes">Sink for outbound AX.25 bytes. The caller is responsible for KISS / AXUDP framing.</param>
    /// <param name="bindings">
    /// Guard predicate bindings. If <c>null</c>, the default bindings
    /// from <see cref="Ax25SessionBindings.CreateDefault"/> are used.
    /// Production code should extend the defaults with any
    /// figure-specific predicates the transition tables reference (e.g.
    /// <c>P_eq_1</c>, <c>command</c>, etc.).
    /// </param>
    /// <param name="subroutines">
    /// Optional registry of subroutine implementations. Defaults to a
    /// fresh <see cref="DefaultSubroutineRegistry"/> with no-op stubs.
    /// Production wiring should register real bodies for
    /// <c>Establish_Data_Link</c>, <c>UI_Check</c>, etc.
    /// </param>
    public Ax25Adapter(
        Ax25SessionContext context,
        ITimerScheduler scheduler,
        IReadOnlyDictionary<string, IReadOnlyList<TransitionSpec>> transitions,
        string initialState,
        Action<ReadOnlyMemory<byte>> sendBytes,
        IReadOnlyDictionary<string, Func<bool>>? bindings = null,
        ISubroutineRegistry? subroutines = null)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(scheduler);
        ArgumentNullException.ThrowIfNull(transitions);
        ArgumentNullException.ThrowIfNull(sendBytes);

        this.sendBytes = sendBytes;

        // Session is assigned after Dispatcher in this ctor; the closure
        // only fires once the constructor returns and a timer expires, so
        // by then Session is non-null. The `!` is for the compiler, not
        // an actual nullability concern.
        Dispatcher = new ActionDispatcher(
            onTimerExpiry: name => Session!.PostEvent(TimerExpiryEvent(name)),
            sendSFrame:    SendS,
            sendUFrame:    SendU,
            sendUiFrame:   SendUi,
            sendIFrame:    SendI,
            sendUpward:    _ => { /* DL-layer signals stay in-memory for the caller to subscribe to via Context */ },
            sendLinkMux:   _ => { /* LM signals: medium-access; passthrough to a wider link layer not yet wired */ },
            sendInternal:  _ => { /* internal signals: queue-management already mutates Context directly */ },
            subroutines:   subroutines);

        // If no custom bindings were supplied, build the default ones —
        // but with frame-awareness wired to the session we're about to
        // create. The forward-reference is harmless because the binding
        // closures only fire later, when guards.Evaluate is called.
        Ax25Session? sessionRef = null;
        var resolvedBindings = bindings
            ?? Ax25SessionBindings.CreateDefault(context, scheduler, currentTrigger: () => sessionRef?.CurrentTrigger);
        var guards = new GuardEvaluator(resolvedBindings);

        Session = new Ax25Session(
            context, scheduler, Dispatcher, guards,
            transitions, initialState);
        sessionRef = Session;
    }

    /// <summary>
    /// Feed an inbound AX.25 frame (in body-bytes form, no FCS) to the
    /// session. Parses to <see cref="Ax25Frame"/>, classifies via
    /// <see cref="Ax25FrameClassifier"/>, and posts to the session.
    /// </summary>
    /// <returns>
    /// <c>true</c> if the bytes parsed and an event was posted;
    /// <c>false</c> on a malformed frame.
    /// </returns>
    public bool OnReceivedAx25Bytes(ReadOnlySpan<byte> bytes)
    {
        if (!Ax25Frame.TryParse(bytes, out var frame))
        {
            return false;
        }
        Session.PostEvent(Ax25FrameClassifier.Classify(frame));
        return true;
    }

    private void SendS (SupervisoryFrameSpec spec) => sendBytes(spec.ToAx25Frame(Context).ToBytes());
    private void SendU (UFrameSpec           spec) => sendBytes(spec.ToAx25Frame(Context).ToBytes());
    private void SendUi(UiFrameSpec          spec) => sendBytes(spec.ToAx25Frame(Context).ToBytes());
    private void SendI (IFrameSpec           spec) => sendBytes(spec.ToAx25Frame(Context).ToBytes());

    private static Ax25Event TimerExpiryEvent(string name) => name switch
    {
        "T1" => new T1Expiry(),
        "T2" => new T2Expiry(),
        "T3" => new T3Expiry(),
        _    => throw new InvalidOperationException($"unexpected timer expiry name '{name}'"),
    };
}
