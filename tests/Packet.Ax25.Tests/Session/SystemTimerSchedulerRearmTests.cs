using AwesomeAssertions;
using Microsoft.Extensions.Time.Testing;
using Packet.Ax25.Session;

namespace Packet.Ax25.Tests.Session;

/// <summary>
/// <see cref="SystemTimerScheduler.RearmIfRunning"/> - the TX-complete→T1
/// primitive. Re-arms a RUNNING timer with a fresh duration and its existing
/// callback, atomically; touches nothing when the timer isn't armed (so an
/// ACKMODE echo racing the SDL's Stop-T1 can never resurrect a stopped watchdog).
/// </summary>
public class SystemTimerSchedulerRearmTests
{
    [Fact]
    public void Rearm_extends_a_running_timer_and_keeps_its_callback()
    {
        var time = new FakeTimeProvider();
        using var scheduler = new SystemTimerScheduler(time);
        var fired = 0;
        scheduler.Arm("T1", TimeSpan.FromSeconds(2), () => fired++);

        time.Advance(TimeSpan.FromSeconds(1.5));
        scheduler.RearmIfRunning("T1", TimeSpan.FromSeconds(2)).Should().BeTrue();

        // The original deadline (t0+2s) passes without firing - it moved.
        time.Advance(TimeSpan.FromSeconds(1));   // t = 2.5s
        fired.Should().Be(0, "the deadline moved to (re-arm + 2 s) = t0+3.5 s");
        scheduler.IsRunning("T1").Should().BeTrue();

        // The moved deadline passes - the ORIGINAL callback fires exactly once.
        time.Advance(TimeSpan.FromSeconds(1.2)); // t = 3.7s
        fired.Should().Be(1);
        scheduler.IsRunning("T1").Should().BeFalse();
    }

    [Fact]
    public void Rearm_of_a_stopped_timer_is_a_no_op_and_returns_false()
    {
        var time = new FakeTimeProvider();
        using var scheduler = new SystemTimerScheduler(time);
        var fired = 0;
        scheduler.Arm("T1", TimeSpan.FromSeconds(2), () => fired++);
        scheduler.Cancel("T1");

        scheduler.RearmIfRunning("T1", TimeSpan.FromSeconds(2)).Should().BeFalse(
            "re-arming a timer the SDL just stopped would resurrect a watchdog the figures believe is off");
        scheduler.IsRunning("T1").Should().BeFalse();
        time.Advance(TimeSpan.FromSeconds(5));
        fired.Should().Be(0);
    }
}
