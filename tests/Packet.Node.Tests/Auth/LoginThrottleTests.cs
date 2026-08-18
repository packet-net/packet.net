using Microsoft.Extensions.Time.Testing;
using Packet.Node.Core.Auth;

namespace Packet.Node.Tests.Auth;

/// <summary>
/// The login lockout: N failures within the window lock a key out; the window
/// sliding past resets it; a success resets it; and per-username vs per-IP keys are
/// independent. All driven by <see cref="FakeTimeProvider"/> - no wall-clock.
/// </summary>
[Trait("Category", "Node")]
public sealed class LoginThrottleTests
{
    private static (LoginThrottle Throttle, FakeTimeProvider Clock) Make(int max = 3, int windowMinutes = 5)
    {
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 6, 9, 0, 0, 0, TimeSpan.Zero));
        return (new LoginThrottle(clock, max, TimeSpan.FromMinutes(windowMinutes)), clock);
    }

    [Fact]
    public void N_failures_within_the_window_lock_the_key_out()
    {
        var (throttle, _) = Make(max: 3);

        throttle.IsLocked("user:bob").Should().BeFalse();
        throttle.RecordFailure("user:bob").Should().BeFalse();   // 1
        throttle.RecordFailure("user:bob").Should().BeFalse();   // 2
        throttle.RecordFailure("user:bob").Should().BeTrue();    // 3 → locked
        throttle.IsLocked("user:bob").Should().BeTrue();
    }

    [Fact]
    public void The_window_sliding_past_resets_the_lockout()
    {
        var (throttle, clock) = Make(max: 3, windowMinutes: 5);

        throttle.RecordFailure("user:bob");
        throttle.RecordFailure("user:bob");
        throttle.RecordFailure("user:bob");
        throttle.IsLocked("user:bob").Should().BeTrue();

        // After the whole window passes, the failures age out → unlocked again.
        clock.Advance(TimeSpan.FromMinutes(5) + TimeSpan.FromSeconds(1));
        throttle.IsLocked("user:bob").Should().BeFalse();
    }

    [Fact]
    public void A_failure_just_outside_the_window_does_not_count_toward_the_threshold()
    {
        var (throttle, clock) = Make(max: 3, windowMinutes: 5);

        throttle.RecordFailure("user:bob");                      // t=0
        clock.Advance(TimeSpan.FromMinutes(6));                  // first failure ages out
        throttle.RecordFailure("user:bob");                      // 1 in window
        throttle.RecordFailure("user:bob");                      // 2 in window
        throttle.IsLocked("user:bob").Should().BeFalse();        // only 2 within window
    }

    [Fact]
    public void A_successful_login_resets_the_counter()
    {
        var (throttle, _) = Make(max: 3);

        throttle.RecordFailure("user:bob");
        throttle.RecordFailure("user:bob");
        throttle.Reset("user:bob");                              // success
        throttle.IsLocked("user:bob").Should().BeFalse();

        // Back to a clean slate - it takes the full N again to lock.
        throttle.RecordFailure("user:bob").Should().BeFalse();
        throttle.RecordFailure("user:bob").Should().BeFalse();
        throttle.RecordFailure("user:bob").Should().BeTrue();
    }

    [Fact]
    public void Per_username_and_per_ip_keys_are_independent()
    {
        var (throttle, _) = Make(max: 3);

        // Three failures for bob lock bob, but NOT an unrelated IP key.
        throttle.RecordFailure("user:bob");
        throttle.RecordFailure("user:bob");
        throttle.RecordFailure("user:bob");
        throttle.IsLocked("user:bob").Should().BeTrue();
        throttle.IsLocked("ip:10.0.0.1").Should().BeFalse();

        // And an IP hammered across many usernames locks the IP key independently.
        throttle.RecordFailure("ip:10.0.0.9");
        throttle.RecordFailure("ip:10.0.0.9");
        throttle.RecordFailure("ip:10.0.0.9");
        throttle.IsLocked("ip:10.0.0.9").Should().BeTrue();
        throttle.IsLocked("user:alice").Should().BeFalse();
    }

    [Fact]
    public void Defaults_are_five_failures_over_five_minutes()
    {
        var clock = new FakeTimeProvider();
        var throttle = new LoginThrottle(clock);
        throttle.MaxFailures.Should().Be(5);
        throttle.Window.Should().Be(TimeSpan.FromMinutes(5));

        for (int i = 0; i < 4; i++)
        {
            throttle.RecordFailure("user:x").Should().BeFalse();
        }
        throttle.RecordFailure("user:x").Should().BeTrue();      // the 5th locks
    }
}
