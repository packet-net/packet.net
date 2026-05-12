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
        fired.ShouldBe(0);

        time.Advance(TimeSpan.FromMilliseconds(1));
        fired.ShouldBe(1);
        sched.IsRunning("T1").ShouldBeFalse("timer should clean itself up after firing");
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
        fired.ShouldBe(0);
        sched.IsRunning("T1").ShouldBeFalse();
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
        firstFired.ShouldBe(0, "the replaced timer should never fire");
        secondFired.ShouldBe(0, "we haven't reached the new due-time yet");

        time.Advance(TimeSpan.FromMilliseconds(4000));
        secondFired.ShouldBe(1);
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

        fireOrder.ShouldBe(new[] { "T2", "T3", "T1" });
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
        fired.ShouldBe(1);
        sched.IsRunning("T1").ShouldBeFalse();

        // The dispatcher decides to re-arm after the expiry.
        sched.Arm("T1", TimeSpan.FromMilliseconds(1000), () => fired++);
        sched.IsRunning("T1").ShouldBeTrue();

        time.Advance(TimeSpan.FromMilliseconds(1000));
        fired.ShouldBe(2);
    }

    [Fact]
    public void IsRunning_Returns_False_For_Unknown_Timer()
    {
        var time = new FakeTimeProvider();
        using var sched = new SystemTimerScheduler(time);
        sched.IsRunning("never_armed").ShouldBeFalse();
    }
}
