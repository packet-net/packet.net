using Microsoft.Extensions.Time.Testing;
using Packet.Ax25.Session;

namespace Packet.Ax25.Tests.Session;

public class TimerSchedulerTests
{
    [Fact]
    public void Arm_Then_Advance_Past_Due_Fires_Once()
    {
        var time = new FakeTimeProvider();
        using var sched = new SystemTimerScheduler(time);
        int fired = 0;
        sched.Arm("T1", TimeSpan.FromMilliseconds(3000), () => fired++);

        time.Advance(TimeSpan.FromMilliseconds(2999));
        fired.Should().Be(0);

        time.Advance(TimeSpan.FromMilliseconds(1));
        fired.Should().Be(1);
        sched.IsRunning("T1").Should().BeFalse("timer should clean itself up after firing");
    }

    [Fact]
    public void Cancel_Prevents_Expiry()
    {
        var time = new FakeTimeProvider();
        using var sched = new SystemTimerScheduler(time);
        int fired = 0;
        sched.Arm("T1", TimeSpan.FromMilliseconds(3000), () => fired++);
        sched.Cancel("T1");

        time.Advance(TimeSpan.FromMilliseconds(5000));
        fired.Should().Be(0);
        sched.IsRunning("T1").Should().BeFalse();
    }

    [Fact]
    public void Arming_Same_Name_Replaces_The_Previous()
    {
        var time = new FakeTimeProvider();
        using var sched = new SystemTimerScheduler(time);
        int firstFired = 0, secondFired = 0;
        sched.Arm("T1", TimeSpan.FromMilliseconds(1000), () => firstFired++);
        sched.Arm("T1", TimeSpan.FromMilliseconds(5000), () => secondFired++);

        time.Advance(TimeSpan.FromMilliseconds(1500));
        firstFired.Should().Be(0, "the replaced timer should never fire");
        secondFired.Should().Be(0, "we haven't reached the new due-time yet");

        time.Advance(TimeSpan.FromMilliseconds(4000));
        secondFired.Should().Be(1);
    }

    [Fact]
    public void Multiple_Timers_Fire_In_Due_Order_Within_One_Advance()
    {
        var time = new FakeTimeProvider();
        using var sched = new SystemTimerScheduler(time);
        var fireOrder = new List<string>();
        sched.Arm("T1", TimeSpan.FromMilliseconds(2000), () => fireOrder.Add("T1"));
        sched.Arm("T2", TimeSpan.FromMilliseconds(500),  () => fireOrder.Add("T2"));
        sched.Arm("T3", TimeSpan.FromMilliseconds(1500), () => fireOrder.Add("T3"));

        time.Advance(TimeSpan.FromMilliseconds(3000));

        fireOrder.Should().Equal(new[] { "T2", "T3", "T1" });
    }

    [Fact]
    public void Timer_Can_Be_Re_Armed_After_It_Has_Fired()
    {
        // AX.25's timer model is "expiry → event → dispatcher decides".
        // The dispatcher might re-arm a timer in response to an expiry,
        // but the re-arm happens *outside* the original callback. This
        // test exercises that contract — we don't depend on re-arming
        // mid-callback being supported.
        var time = new FakeTimeProvider();
        using var sched = new SystemTimerScheduler(time);
        int fired = 0;
        sched.Arm("T1", TimeSpan.FromMilliseconds(1000), () => fired++);

        time.Advance(TimeSpan.FromMilliseconds(1000));
        fired.Should().Be(1);
        sched.IsRunning("T1").Should().BeFalse();

        // The dispatcher decides to re-arm after the expiry.
        sched.Arm("T1", TimeSpan.FromMilliseconds(1000), () => fired++);
        sched.IsRunning("T1").Should().BeTrue();

        time.Advance(TimeSpan.FromMilliseconds(1000));
        fired.Should().Be(2);
    }

    [Fact]
    public void IsRunning_Returns_False_For_Unknown_Timer()
    {
        var time = new FakeTimeProvider();
        using var sched = new SystemTimerScheduler(time);
        sched.IsRunning("never_armed").Should().BeFalse();
    }

    [Fact]
    public void TimeRemaining_Tracks_Elapsed_Then_Zero_After_Expiry()
    {
        var time = new FakeTimeProvider();
        using var sched = new SystemTimerScheduler(time);
        sched.Arm("T1", TimeSpan.FromMilliseconds(3000), () => { });

        sched.TimeRemaining("T1").Should().Be(TimeSpan.FromMilliseconds(3000));

        time.Advance(TimeSpan.FromMilliseconds(1200));
        sched.TimeRemaining("T1").Should().Be(TimeSpan.FromMilliseconds(1800));

        time.Advance(TimeSpan.FromMilliseconds(1800));
        // Timer has fired → callback ran → entry removed → query is zero.
        sched.TimeRemaining("T1").Should().Be(TimeSpan.Zero);
    }

    [Fact]
    public void TimeRemaining_Returns_Zero_For_Unknown_Timer()
    {
        var time = new FakeTimeProvider();
        using var sched = new SystemTimerScheduler(time);
        sched.TimeRemaining("never_armed").Should().Be(TimeSpan.Zero);
    }

    [Fact]
    public void TimeRemaining_Returns_Zero_After_Cancel()
    {
        var time = new FakeTimeProvider();
        using var sched = new SystemTimerScheduler(time);
        sched.Arm("T1", TimeSpan.FromMilliseconds(3000), () => { });
        time.Advance(TimeSpan.FromMilliseconds(1000));

        // Caller must read TimeRemaining BEFORE cancelling to get the
        // actual round-trip sample — this is the contract that
        // ActionDispatcher's stop_T1 case relies on.
        var sampled = sched.TimeRemaining("T1");
        sched.Cancel("T1");

        sampled.Should().Be(TimeSpan.FromMilliseconds(2000));
        sched.TimeRemaining("T1").Should().Be(TimeSpan.Zero);
    }
}
