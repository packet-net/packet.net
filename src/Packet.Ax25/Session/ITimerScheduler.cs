namespace Packet.Ax25.Session;

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
}
