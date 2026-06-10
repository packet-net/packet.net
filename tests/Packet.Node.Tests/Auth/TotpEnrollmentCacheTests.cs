using Microsoft.Extensions.Time.Testing;
using Packet.Node.Core.Auth;

namespace Packet.Node.Tests.Auth;

/// <summary>
/// The security properties of the pending-TOTP-enrolment cache: single-use (a take consumes
/// the entry), expiry on the injected clock (an abandoned begin can't be completed later),
/// and the per-user key binding (one user's pending secret can't be completed as another).
/// All driven by <see cref="FakeTimeProvider"/> — no wall-clock. Mirrors
/// <see cref="WebAuthnChallengeCacheTests"/>.
/// </summary>
[Trait("Category", "Node")]
public sealed class TotpEnrollmentCacheTests
{
    private static (TotpEnrollmentCache cache, FakeTimeProvider clock) New(TimeSpan? ttl = null)
    {
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 6, 9, 0, 0, 0, TimeSpan.Zero));
        return (new TotpEnrollmentCache(clock, ttl), clock);
    }

    [Fact]
    public void Take_is_single_use()
    {
        var (cache, _) = New();
        cache.Put("alice", "JBSWY3DPEHPK3PXP");

        cache.Take("alice").Should().Be("JBSWY3DPEHPK3PXP");
        // Consumed — a second take (a replay / double-submit) finds nothing.
        cache.Take("alice").Should().BeNull();
    }

    [Fact]
    public void An_expired_entry_is_not_returned()
    {
        var (cache, clock) = New(TimeSpan.FromMinutes(5));
        cache.Put("bob", "SECRETSECRET");

        clock.Advance(TimeSpan.FromMinutes(5));   // now strictly at/after expiry
        cache.Take("bob").Should().BeNull();
    }

    [Fact]
    public void An_entry_just_inside_its_ttl_is_returned()
    {
        var (cache, clock) = New(TimeSpan.FromMinutes(5));
        cache.Put("carol", "SECRETSECRET");

        clock.Advance(TimeSpan.FromMinutes(5) - TimeSpan.FromMilliseconds(1));
        cache.Take("carol").Should().Be("SECRETSECRET");
    }

    [Fact]
    public void Enrolment_is_bound_to_the_user_key()
    {
        var (cache, _) = New();
        cache.Put("alice", "ALICESECRET");

        // A take under a DIFFERENT user finds nothing (you can't complete Alice's enrolment
        // as Bob).
        cache.Take("bob").Should().BeNull();
        cache.Take("alice").Should().Be("ALICESECRET");
    }

    [Fact]
    public void A_fresh_put_supersedes_a_prior_pending_enrolment_for_the_same_user()
    {
        var (cache, _) = New();
        cache.Put("alice", "FIRST");
        cache.Put("alice", "SECOND");   // a new begin supersedes the abandoned one
        cache.Take("alice").Should().Be("SECOND");
    }

    [Fact]
    public void Prune_expired_drops_only_stale_entries()
    {
        var (cache, clock) = New(TimeSpan.FromMinutes(5));
        cache.Put("old", "OLD");
        clock.Advance(TimeSpan.FromMinutes(3));
        cache.Put("new", "NEW");

        // Advance past the old entry's expiry but not the new one's.
        clock.Advance(TimeSpan.FromMinutes(3));   // old is 6 min, new is 3 min
        cache.PruneExpired();

        cache.Take("old").Should().BeNull();
        cache.Take("new").Should().Be("NEW");
    }

    [Fact]
    public void A_take_for_an_unknown_user_returns_null_without_throwing()
    {
        var (cache, _) = New();
        cache.Take("nobody").Should().BeNull();
    }
}
