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

    // We keep both the underlying ITimer (so Cancel can dispose it) and
    // the deadline so TimeRemaining can answer without poking at the
    // ITimer. Deadlines are absolute on TimeProvider's clock so they
    // tick correctly under FakeTimeProvider too.
    private readonly Dictionary<string, (ITimer Timer, DateTimeOffset Deadline, Action OnExpiry)> timers =
        new(StringComparer.Ordinal);

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
            ArmLocked(name, duration, onExpiry);
        }
    }

    /// <inheritdoc/>
    public void Cancel(string name)
    {
        lock (gate)
        {
            if (timers.TryGetValue(name, out var entry))
            {
                entry.Timer.Dispose();
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
    public TimeSpan TimeRemaining(string name)
    {
        lock (gate)
        {
            if (!timers.TryGetValue(name, out var entry)) return TimeSpan.Zero;
            var remaining = entry.Deadline - time.GetUtcNow();
            return remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero;
        }
    }

    /// <inheritdoc/>
    public IReadOnlyList<ArmedTimer> CaptureState()
    {
        lock (gate)
        {
            var now = time.GetUtcNow();
            return timers.Select(kv =>
            {
                var remaining = kv.Value.Deadline - now;
                return new ArmedTimer(
                    kv.Key,
                    remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero,
                    kv.Value.OnExpiry);
            }).ToList();
        }
    }

    /// <inheritdoc/>
    public void RestoreState(IReadOnlyList<ArmedTimer> state)
    {
        ArgumentNullException.ThrowIfNull(state);
        lock (gate)
        {
            foreach (var entry in timers.Values)
            {
                entry.Timer.Dispose();
            }
            timers.Clear();
            foreach (var t in state)
            {
                // Clamp to a minimal positive duration so a timer captured at
                // (or past) its deadline still re-arms and fires promptly,
                // rather than being silently dropped.
                var duration = t.Remaining > TimeSpan.Zero ? t.Remaining : TimeSpan.FromTicks(1);
                ArmLocked(t.Name, duration, t.OnExpiry);
            }
        }
    }

    // Arm body without taking the gate — caller already holds it.
    private void ArmLocked(string name, TimeSpan duration, Action onExpiry)
    {
        if (timers.TryGetValue(name, out var existing))
        {
            existing.Timer.Dispose();
            timers.Remove(name);
        }
        var deadline = time.GetUtcNow() + duration;
        var timer = time.CreateTimer(_ =>
        {
            lock (gate)
            {
                timers.Remove(name);
            }
            onExpiry();
        }, state: null, dueTime: duration, period: Timeout.InfiniteTimeSpan);
        timers[name] = (timer, deadline, onExpiry);
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        lock (gate)
        {
            foreach (var entry in timers.Values)
            {
                entry.Timer.Dispose();
            }
            timers.Clear();
        }
    }
}
