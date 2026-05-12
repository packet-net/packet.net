using Packet.Ax25.Sdl;

namespace Packet.Ax25.Session;

/// <summary>
/// Executes the action strings recorded in an SDL transition's
/// <c>actions:</c> list. Each verb maps to one method on the dispatcher,
/// which either mutates the <see cref="Ax25SessionContext"/>, arms /
/// cancels a timer via the <see cref="ITimerScheduler"/>, or signals an
/// outgoing supervisory frame via the configured sink callback.
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
public sealed class ActionDispatcher
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

    /// <summary>
    /// Execute every action in <paramref name="actions"/> against the
    /// supplied session state and scheduler. Action <see cref="ActionStep.Kind"/>
    /// is preserved on the spec for downstream tools (figure redraw, cross-language
    /// codegen) but the dispatcher looks up handlers by <see cref="ActionStep.Verb"/>.
    /// </summary>
    public void Execute(IEnumerable<ActionStep> actions, Ax25SessionContext ctx, ITimerScheduler scheduler)
    {
        ArgumentNullException.ThrowIfNull(actions);
        ArgumentNullException.ThrowIfNull(ctx);
        ArgumentNullException.ThrowIfNull(scheduler);

        foreach (var step in actions)
        {
            Execute(step.Verb, ctx, scheduler);
        }
    }

    /// <summary>
    /// Execute every action verb in <paramref name="actions"/> against the
    /// supplied session state and scheduler. Used by hand-rolled test
    /// fixtures and ad-hoc consumers that don't need shape-class metadata.
    /// </summary>
    public void Execute(IEnumerable<string> actions, Ax25SessionContext ctx, ITimerScheduler scheduler)
    {
        ArgumentNullException.ThrowIfNull(actions);
        ArgumentNullException.ThrowIfNull(ctx);
        ArgumentNullException.ThrowIfNull(scheduler);

        foreach (var action in actions)
        {
            Execute(action, ctx, scheduler);
        }
    }

    /// <summary>
    /// Execute a single action verb. Throws on unknown actions so a typo
    /// in the SDL transcription doesn't get silently swallowed.
    /// </summary>
    public void Execute(string action, Ax25SessionContext ctx, ITimerScheduler scheduler)
    {
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
            case "RR command":                     sendSFrame(new SupervisoryFrameSpec(SupervisoryFrameType.Rr,   IsCommand: true));  break;
            case "RR response":                    sendSFrame(new SupervisoryFrameSpec(SupervisoryFrameType.Rr,   IsCommand: false)); break;
            case "RNR command":                    sendSFrame(new SupervisoryFrameSpec(SupervisoryFrameType.Rnr,  IsCommand: true));  break;
            case "RNR response":                   sendSFrame(new SupervisoryFrameSpec(SupervisoryFrameType.Rnr,  IsCommand: false)); break;
            case "REJ command":                    sendSFrame(new SupervisoryFrameSpec(SupervisoryFrameType.Rej,  IsCommand: true));  break;
            case "REJ response":                   sendSFrame(new SupervisoryFrameSpec(SupervisoryFrameType.Rej,  IsCommand: false)); break;
            case "SREJ command":                   sendSFrame(new SupervisoryFrameSpec(SupervisoryFrameType.Srej, IsCommand: true));  break;
            case "SREJ response":                  sendSFrame(new SupervisoryFrameSpec(SupervisoryFrameType.Srej, IsCommand: false)); break;

            // ─── Sequence-variable assignments ─────────────────────────
            case "RC := 0":                        ctx.RC = 0; break;
            case "V(S) := V(S) + 1":               ctx.VS = ctx.IncrementSeq(ctx.VS); break;
            case "V(R) := V(R) + 1":               ctx.VR = ctx.IncrementSeq(ctx.VR); break;

            default:
                throw new InvalidOperationException(
                    $"unknown SDL action: '{action}'. " +
                    "If this verb appears in a new transcription, add a case in ActionDispatcher.Execute.");
        }
    }
}
