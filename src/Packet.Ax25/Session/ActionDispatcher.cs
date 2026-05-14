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
    /// frame. The session is responsible for translating the spec into
    /// an actual <see cref="Ax25Frame"/> and shipping it on the wire.
    /// </param>
    public ActionDispatcher(
        Action<string> onTimerExpiry,
        Action<SupervisoryFrameSpec> sendSFrame)
    {
        this.onTimerExpiry = onTimerExpiry ?? throw new ArgumentNullException(nameof(onTimerExpiry));
        this.sendSFrame    = sendSFrame    ?? throw new ArgumentNullException(nameof(sendSFrame));
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
            case "start_T1":                       scheduler.Arm("T1", T1Duration, () => onTimerExpiry("T1")); break;
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
