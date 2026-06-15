namespace Packet.Ax25.Session;

/// <summary>
/// One armed timer captured by <see cref="ITimerScheduler.CaptureState"/>:
/// its name, the time remaining at capture, and its expiry callback.
/// </summary>
public sealed record ArmedTimer(string Name, TimeSpan Remaining, Action OnExpiry);

/// <summary>
/// Abstraction over the AX.25 timer set (T1 / T2 / T3 plus any phy-layer
/// timers). The session uses one scheduler instance; implementations
/// decide whether to drive timers off real wall-clock time
/// (<see cref="SystemTimerScheduler"/>) or off virtual time controlled by
/// the test harness.
/// </summary>
public interface ITimerScheduler
{
    /// <summary>
    /// Arm a timer named <paramref name="name"/>. If a timer with that name
    /// is already running, it is cancelled and replaced. When the timer
    /// fires, <paramref name="onExpiry"/> is invoked on the implementation's
    /// chosen thread / scheduling context.
    /// </summary>
    void Arm(string name, TimeSpan duration, Action onExpiry);

    /// <summary>
    /// Cancel a named timer if it's currently armed. No-op otherwise.
    /// </summary>
    void Cancel(string name);

    /// <summary>True if a timer named <paramref name="name"/> is currently armed.</summary>
    bool IsRunning(string name);

    /// <summary>
    /// Time remaining until <paramref name="name"/> expires. Returns
    /// <see cref="TimeSpan.Zero"/> when the timer isn't running (either
    /// never armed, already fired, or cancelled).
    /// </summary>
    /// <remarks>
    /// AX.25 v2.2 §6.7.1.2's SRT update needs the "remaining time on T1
    /// when last stopped" sample. Callers typically read this immediately
    /// before <see cref="Cancel"/> and stash it on session state for the
    /// next <c>Select_T1_Value</c> invocation. Querying after cancel
    /// gives zero, which is correct semantics ("stopped = no time
    /// remaining") but unhelpful for that formula.
    /// </remarks>
    TimeSpan TimeRemaining(string name);

    /// <summary>
    /// Atomically re-arm <paramref name="name"/> with a fresh
    /// <paramref name="duration"/> and its <em>existing</em> expiry callback —
    /// but only if it is currently running. Returns <c>false</c> (touching
    /// nothing) when the timer isn't armed.
    /// </summary>
    /// <remarks>
    /// The TX-complete→T1 seam: when a TNC's ACKMODE echo reports that an
    /// I-frame / enquiry actually cleared the air, the listener pushes a
    /// <em>running</em> T1's deadline out to (now + T1V) — the SDL armed T1 at
    /// enqueue, before the frame had even keyed up. The
    /// check-and-re-arm must be atomic against the SDL stopping T1 on the
    /// dispatch thread (an ack racing the echo): re-arming a timer the SDL
    /// just stopped would resurrect a watchdog the figures believe is off.
    /// Default implementation is a conservative no-op so virtual-time test
    /// schedulers don't have to care.
    /// </remarks>
    bool RearmIfRunning(string name, TimeSpan duration) => false;

    /// <summary>
    /// Capture the currently-armed timers (name, time remaining, expiry
    /// callback) so the set can be restored with <see cref="RestoreState"/>.
    /// </summary>
    IReadOnlyList<ArmedTimer> CaptureState();

    /// <summary>
    /// Restore the armed-timer set to a previously <see cref="CaptureState"/>d
    /// snapshot: cancel every currently-armed timer and re-arm exactly those in
    /// <paramref name="state"/> with their captured remaining time. The session
    /// uses this to undo a transition that threw part-way through, so a
    /// half-applied transition can't leave the link watchdog (T1) cancelled —
    /// which would wedge the session silently. (packet-net/packet.net#225)
    /// </summary>
    void RestoreState(IReadOnlyList<ArmedTimer> state);
}
