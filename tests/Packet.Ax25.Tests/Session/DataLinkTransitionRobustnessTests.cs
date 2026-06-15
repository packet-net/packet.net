using AwesomeAssertions;
using Microsoft.Extensions.Time.Testing;
using Packet.Ax25.Sdl;
using Packet.Ax25.Session;
using Packet.Core;

namespace Packet.Ax25.Tests.Session;

/// <summary>
/// Robustness (packet-net/packet.net#225): a transition whose action sequence throws
/// part-way through must not leave the session silently wedged. A path that ran
/// "Stop T1" but threw before "Start T1" used to leave T1 cancelled forever, so
/// the session sat in TimerRecovery doing nothing until the peer's N2 timeout
/// killed the link. (Surfaced by the figc4.5 SREJ erratum, ax25sdl#55, where
/// "Push Frame Onto Queue" on an SREJ trigger throws mid-transition.)
///
/// The session now captures the armed-timer set before running a transition's
/// actions and restores it if they throw, so the link watchdog survives and
/// T1 / N2 drive recovery or a clean disconnect.
/// </summary>
public class DataLinkTransitionRobustnessTests
{
    [Fact]
    public void Scheduler_RestoreState_re_arms_a_timer_cancelled_after_capture()
    {
        var time = new FakeTimeProvider();
        var scheduler = new SystemTimerScheduler(time);
        var fired = 0;
        scheduler.Arm("T1", TimeSpan.FromSeconds(3), () => fired++);

        var snapshot = scheduler.CaptureState();
        scheduler.Cancel("T1");
        scheduler.IsRunning("T1").Should().BeFalse();

        scheduler.RestoreState(snapshot);

        scheduler.IsRunning("T1").Should().BeTrue("RestoreState re-arms timers captured before they were cancelled");
        time.Advance(TimeSpan.FromSeconds(3.1));
        fired.Should().Be(1, "the restored timer still fires its original callback");
    }

    [Fact]
    public void A_mid_transition_throw_restores_the_link_timer_and_leaves_state_unchanged()
    {
        var time = new FakeTimeProvider();
        var scheduler = new SystemTimerScheduler(time);
        // Session is in TimerRecovery with T1 running (the normal recovery posture).
        scheduler.Arm("T1", TimeSpan.FromSeconds(6), () => { });

        var ctx = new Ax25SessionContext
        {
            Local  = new Callsign("M0LTE", 0),
            Remote = new Callsign("G7XYZ", 7),
        };
        var guards = new GuardEvaluator(Ax25SessionBindings.CreateDefault(ctx, scheduler));

        // A transition whose actions run "Stop T1" and then throw before
        // "Start T1" — the figc4.5-SREJ crash shape (ax25sdl#55).
        var dispatcher = new StopT1ThenThrowDispatcher(scheduler);
        var transitions = new Dictionary<string, IReadOnlyList<TransitionSpec>>
        {
            ["TimerRecovery"] = new TransitionSpec[]
            {
                new(
                    Id: "t_boom",
                    From: "TimerRecovery",
                    On: Packet.Ax25.Sdl.Ax25Event.T1Expiry,
                    Guard: null,
                    // The verb is immaterial — StopT1ThenThrowDispatcher ignores it
                    // and throws after cancelling T1 (it never reaches the real switch).
                    Actions: new[] { new ActionStep(Ax25ActionVerb.StopT1, ActionKind.Processing) },
                    Next: "Connected",
                    Notes: null,
                    References: Array.Empty<ImplementationReference>(),
                    Loops: Array.Empty<LoopRange>()),
            },
        };
        var session = new Ax25Session(ctx, scheduler, dispatcher, guards, transitions, initialState: "TimerRecovery");

        var act = () => session.PostEvent(new T1Expiry());

        act.Should().Throw<InvalidOperationException>();
        session.CurrentState.Should().Be("TimerRecovery", "the transition did not complete, so state is unchanged");
        scheduler.IsRunning("T1").Should().BeTrue(
            "a mid-transition throw must not leave T1 cancelled — otherwise the session wedges silently (#225)");
    }

    // Mimics "Stop T1" followed by a crash before "Start T1".
    private sealed class StopT1ThenThrowDispatcher : IActionDispatcher
    {
        private readonly ITimerScheduler scheduler;
        public StopT1ThenThrowDispatcher(ITimerScheduler scheduler) => this.scheduler = scheduler;

        public void Execute(IEnumerable<ActionStep> actions, TransitionContext tx)
        {
            scheduler.Cancel("T1");
            throw new InvalidOperationException("unexpected verb for this trigger");
        }
    }
}
