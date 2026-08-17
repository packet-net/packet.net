using AwesomeAssertions;
using Microsoft.Extensions.Time.Testing;
using Packet.Ax25.Session;

namespace Packet.Ax25.Tests.Session;

/// <summary>
/// A timer callback that was already in flight when its timer was cancelled and
/// the same name re-armed must not touch the new entry. Disposing an
/// <see cref="ITimer"/> suppresses a merely-queued callback, but one that has
/// already passed that check and is waiting on the scheduler's gate still runs -
/// and the SDL does Stop-T1 + Start-T1 back to back, so the window is real. It
/// used to remove whatever entry was under the name and post its own expiry,
/// which meant a spurious T1 expiry into the state machine and an orphaned new
/// timer that later fired while the figures believed T1 was stopped
/// (packet-net/packet.net#696). Each armed timer now carries a generation token
/// and only acts when the stored entry is still its own.
/// </summary>
public class SystemTimerSchedulerStaleCallbackTests
{
    [Fact]
    public void A_stale_callback_neither_fires_nor_removes_the_rearmed_timer()
    {
        var time = new ManualTimerProvider();
        using var scheduler = new SystemTimerScheduler(time);

        var aFired = 0;
        var bFired = 0;
        scheduler.Arm("T1", TimeSpan.FromMilliseconds(20), () => aFired++);
        var timerA = time.Timers.Single();

        // The SDL's Stop T1 / Start T1 pair, run while A's callback is in flight.
        scheduler.Cancel("T1");
        scheduler.Arm("T1", TimeSpan.FromMilliseconds(400), () => bFired++);
        var timerB = time.Timers.Last();
        timerB.Should().NotBeSameAs(timerA);

        // A's callback finally gets the gate.
        timerA.Fire();

        aFired.Should().Be(0, "the timer that raised this callback was cancelled before it ran");
        bFired.Should().Be(0, "B's deadline has not passed");
        scheduler.IsRunning("T1").Should().BeTrue("the re-armed timer must still be armed");
        time.Advance(TimeSpan.FromMilliseconds(200));
        scheduler.TimeRemaining("T1").Should().BeGreaterThan(TimeSpan.Zero);

        // ...and B still owns the name: it fires, once, and clears itself.
        timerB.Fire();
        bFired.Should().Be(1);
        aFired.Should().Be(0);
        scheduler.IsRunning("T1").Should().BeFalse();
    }

    [Fact]
    public void A_stale_callback_after_a_rearm_in_running_is_also_suppressed()
    {
        // RearmIfRunning re-arms through the same path, so it takes a new
        // generation and the superseded callback is silent.
        var time = new ManualTimerProvider();
        using var scheduler = new SystemTimerScheduler(time);

        var fired = 0;
        scheduler.Arm("T1", TimeSpan.FromMilliseconds(20), () => fired++);
        var timerA = time.Timers.Single();
        scheduler.RearmIfRunning("T1", TimeSpan.FromMilliseconds(400)).Should().BeTrue();

        timerA.Fire();

        fired.Should().Be(0, "the pre-rearm callback must not deliver an expiry");
        scheduler.IsRunning("T1").Should().BeTrue();

        time.Timers.Last().Fire();
        fired.Should().Be(1, "the re-armed timer keeps the original callback and fires exactly once");
    }

    [Fact]
    public void The_ordinary_expiry_path_is_unaffected()
    {
        // The guard must not break the normal case, which the rest of the suite
        // drives through FakeTimeProvider.
        var time = new FakeTimeProvider();
        using var scheduler = new SystemTimerScheduler(time);
        var fired = 0;
        scheduler.Arm("T3", TimeSpan.FromSeconds(2), () => fired++);

        time.Advance(TimeSpan.FromSeconds(2));

        fired.Should().Be(1);
        scheduler.IsRunning("T3").Should().BeFalse();
    }

    /// <summary>
    /// A <see cref="TimeProvider"/> whose timers never fire on their own: the test
    /// invokes each captured callback by hand, which is what makes the
    /// cancelled-then-re-armed ordering deterministic. The clock itself is a
    /// <see cref="FakeTimeProvider"/> so deadlines and <c>TimeRemaining</c> behave
    /// exactly as they do in the rest of the scheduler tests.
    /// </summary>
    private sealed class ManualTimerProvider : TimeProvider
    {
        private readonly FakeTimeProvider clock = new();

        public List<ManualTimer> Timers { get; } = [];

        public override DateTimeOffset GetUtcNow() => clock.GetUtcNow();

        public void Advance(TimeSpan delta) => clock.Advance(delta);

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = new ManualTimer(callback, state);
            Timers.Add(timer);
            return timer;
        }
    }

    private sealed class ManualTimer(TimerCallback callback, object? state) : ITimer
    {
        public bool IsDisposed { get; private set; }

        /// <summary>Run the callback as the timer queue would.</summary>
        public void Fire() => callback(state);

        public bool Change(TimeSpan dueTime, TimeSpan period) => true;

        public void Dispose() => IsDisposed = true;

        public ValueTask DisposeAsync()
        {
            IsDisposed = true;
            return ValueTask.CompletedTask;
        }
    }
}
