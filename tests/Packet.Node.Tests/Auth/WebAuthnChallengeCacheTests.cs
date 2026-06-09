using Microsoft.Extensions.Time.Testing;
using Packet.Node.Core.Auth;

namespace Packet.Node.Tests.Auth;

/// <summary>
/// The security properties of the WebAuthn challenge cache: single-use (a take consumes
/// the entry), expiry on the injected clock (a stale ceremony can't be completed), and
/// the user/session key binding (a registration for one user can't be taken as another;
/// an assertion is bound to its per-attempt session handle). All driven by
/// <see cref="FakeTimeProvider"/> — no wall-clock.
/// </summary>
[Trait("Category", "Node")]
public sealed class WebAuthnChallengeCacheTests
{
    // A stand-in "options" object — the cache is type-agnostic (it stashes object), so a
    // simple sentinel proves the take/expiry/binding semantics without a real Fido2 type.
    private sealed record Options(string Marker);

    private static (WebAuthnChallengeCache cache, FakeTimeProvider clock) New(TimeSpan? ttl = null)
    {
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 6, 9, 0, 0, 0, TimeSpan.Zero));
        return (new WebAuthnChallengeCache(clock, ttl), clock);
    }

    [Fact]
    public void Take_is_single_use()
    {
        var (cache, _) = New();
        var key = WebAuthnChallengeCache.RegistrationKey("alice");
        cache.Put(key, new Options("reg-alice"));

        cache.Take<Options>(key).Should().Be(new Options("reg-alice"));
        // Consumed — a second take (a replay) finds nothing.
        cache.Take<Options>(key).Should().BeNull();
    }

    [Fact]
    public void An_expired_entry_is_not_returned()
    {
        var (cache, clock) = New(TimeSpan.FromMinutes(5));
        var key = WebAuthnChallengeCache.AssertionKey("sess-1");
        cache.Put(key, new Options("assert"));

        // Just before expiry — still good.
        clock.Advance(TimeSpan.FromMinutes(5) - TimeSpan.FromSeconds(1));
        // (don't take yet — re-put so we can test the past-expiry case on a fresh entry)
        var stillGood = new WebAuthnChallengeCache(clock, TimeSpan.FromMinutes(5));
        stillGood.Put(key, new Options("assert2"));
        clock.Advance(TimeSpan.FromMinutes(5));   // now strictly at/after expiry
        stillGood.Take<Options>(key).Should().BeNull();

        // And the ORIGINAL cache's entry is also past expiry now.
        cache.Take<Options>(key).Should().BeNull();
    }

    [Fact]
    public void An_entry_just_inside_its_ttl_is_returned()
    {
        var (cache, clock) = New(TimeSpan.FromMinutes(5));
        var key = WebAuthnChallengeCache.AssertionKey("sess-1");
        cache.Put(key, new Options("assert"));

        clock.Advance(TimeSpan.FromMinutes(5) - TimeSpan.FromMilliseconds(1));
        cache.Take<Options>(key).Should().Be(new Options("assert"));
    }

    [Fact]
    public void Registration_is_bound_to_the_user_key()
    {
        var (cache, _) = New();
        cache.Put(WebAuthnChallengeCache.RegistrationKey("alice"), new Options("alice-reg"));

        // A take under a DIFFERENT user's key finds nothing (you can't complete Alice's
        // enrolment as Bob).
        cache.Take<Options>(WebAuthnChallengeCache.RegistrationKey("bob")).Should().BeNull();
        // Alice's own key resolves it.
        cache.Take<Options>(WebAuthnChallengeCache.RegistrationKey("alice")).Should().Be(new Options("alice-reg"));
    }

    [Fact]
    public void Assertion_is_bound_to_the_session_key()
    {
        var (cache, _) = New();
        cache.Put(WebAuthnChallengeCache.AssertionKey("sess-A"), new Options("A"));
        cache.Put(WebAuthnChallengeCache.AssertionKey("sess-B"), new Options("B"));

        // A wrong/unknown session id finds nothing; each session id resolves only its own.
        cache.Take<Options>(WebAuthnChallengeCache.AssertionKey("sess-X")).Should().BeNull();
        cache.Take<Options>(WebAuthnChallengeCache.AssertionKey("sess-A")).Should().Be(new Options("A"));
        cache.Take<Options>(WebAuthnChallengeCache.AssertionKey("sess-B")).Should().Be(new Options("B"));
    }

    [Fact]
    public void A_fresh_put_supersedes_a_prior_pending_ceremony_for_the_same_key()
    {
        var (cache, _) = New();
        var key = WebAuthnChallengeCache.RegistrationKey("alice");
        cache.Put(key, new Options("first"));
        cache.Put(key, new Options("second"));   // a new begin supersedes the abandoned one
        cache.Take<Options>(key).Should().Be(new Options("second"));
    }

    [Fact]
    public void Prune_expired_drops_only_stale_entries()
    {
        var (cache, clock) = New(TimeSpan.FromMinutes(5));
        cache.Put(WebAuthnChallengeCache.AssertionKey("old"), new Options("old"));
        clock.Advance(TimeSpan.FromMinutes(3));
        cache.Put(WebAuthnChallengeCache.AssertionKey("new"), new Options("new"));

        // Advance past the old entry's expiry but not the new one's.
        clock.Advance(TimeSpan.FromMinutes(3));   // old is 6 min, new is 3 min
        cache.PruneExpired();

        cache.Take<Options>(WebAuthnChallengeCache.AssertionKey("old")).Should().BeNull();
        cache.Take<Options>(WebAuthnChallengeCache.AssertionKey("new")).Should().Be(new Options("new"));
    }

    [Fact]
    public void A_take_of_the_wrong_type_returns_null_without_throwing()
    {
        var (cache, _) = New();
        var key = WebAuthnChallengeCache.RegistrationKey("alice");
        cache.Put(key, new Options("x"));
        // Asking for a different type consumes the entry but resolves null (no cast throw).
        cache.Take<string>(key).Should().BeNull();
        cache.Take<Options>(key).Should().BeNull();   // and it was consumed
    }
}
