namespace Packet.Ax25.Session;

/// <summary>
/// <see cref="ITimerScheduler"/> backed by a <see cref="TimeProvider"/>.
/// </summary>
/// <remarks>
/// <para>
/// Pass <see cref="TimeProvider.System"/> for production — timers fire off
/// real wall-clock time. Pass a <c>FakeTimeProvider</c> (from
/// <c>Microsoft.Extensions.TimeProvider.Testing</c>) for tests — virtual
/// time advances only when the test harness calls <c>Advance</c>, making
/// timer-expiry transitions deterministic without sleeping.
/// </para>
/// <para>
/// One implementation covers both cases — the <see cref="TimeProvider"/>
/// abstraction handles the real-vs-fake distinction without us needing
/// parallel schedulers.
/// </para>
/// </remarks>
public sealed class SystemTimerScheduler : ITimerScheduler, IDisposable
{
    private readonly TimeProvider time;
    private readonly object gate = new();
    private readonly Dictionary<string, ITimer> timers = new(StringComparer.Ordinal);

    /// <summary>
    /// Create a scheduler. <paramref name="time"/> controls how durations are
    /// interpreted — pass <see cref="TimeProvider.System"/> for production,
    /// a fake for tests.
    /// </summary>
    public SystemTimerScheduler(TimeProvider time)
    {
        this.time = time ?? throw new ArgumentNullException(nameof(time));
    }

    /// <inheritdoc/>
    public void Arm(string name, TimeSpan duration, Action onExpiry)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(onExpiry);

        lock (gate)
        {
            if (timers.TryGetValue(name, out var existing))
            {
                existing.Dispose();
                timers.Remove(name);
            }
            // Capture `name` so the callback can self-remove on fire.
            var timer = time.CreateTimer(_ =>
            {
                lock (gate)
                {
                    timers.Remove(name);
                }
                onExpiry();
            }, state: null, dueTime: duration, period: Timeout.InfiniteTimeSpan);
            timers[name] = timer;
        }
    }

    /// <inheritdoc/>
    public void Cancel(string name)
    {
        lock (gate)
        {
            if (timers.TryGetValue(name, out var timer))
            {
                timer.Dispose();
                timers.Remove(name);
            }
        }
    }

    /// <inheritdoc/>
    public bool IsRunning(string name)
    {
        lock (gate)
        {
            return timers.ContainsKey(name);
        }
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        lock (gate)
        {
            foreach (var timer in timers.Values)
            {
                timer.Dispose();
            }
            timers.Clear();
        }
    }
}
