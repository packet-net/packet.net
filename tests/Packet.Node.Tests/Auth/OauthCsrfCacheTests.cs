using Microsoft.Extensions.Time.Testing;
using Packet.Node.Core.Auth.Oauth;

namespace Packet.Node.Tests.Auth;

/// <summary>
/// The in-memory OAuth consent anti-forgery (CSRF) token cache. Covers mint/consume
/// single-use and expiry, and - the 2026-08-17 hardening - the hard cap that bounds
/// memory under an unauthenticated GET /oauth/authorize flood. Tokens only expire, never
/// evict, so without a cap a burst would grow the map without bound for a whole TTL window.
/// </summary>
[Trait("Category", "Node")]
public sealed class OauthCsrfCacheTests
{
    private static readonly DateTimeOffset T0 = new(2026, 6, 1, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void A_minted_token_consumes_exactly_once()
    {
        var cache = new OauthCsrfCache(new FakeTimeProvider(T0));
        var t = cache.Mint();
        cache.Consume(t).Should().BeTrue();   // first use accepted
        cache.Consume(t).Should().BeFalse();  // replay rejected (single-use)
    }

    [Fact]
    public void An_unknown_or_blank_token_is_rejected()
    {
        var cache = new OauthCsrfCache(new FakeTimeProvider(T0));
        cache.Consume(null).Should().BeFalse();
        cache.Consume("").Should().BeFalse();
        cache.Consume("deadbeef").Should().BeFalse();
    }

    [Fact]
    public void A_token_past_its_ttl_is_rejected()
    {
        var clock = new FakeTimeProvider(T0);
        var cache = new OauthCsrfCache(clock, TimeSpan.FromMinutes(15));
        var t = cache.Mint();
        clock.Advance(TimeSpan.FromMinutes(15) + TimeSpan.FromSeconds(1));
        cache.Consume(t).Should().BeFalse();
    }

    [Fact]
    public void An_unauthenticated_flood_cannot_grow_the_cache_without_bound()
    {
        // Nothing expires during the burst (all fresh, 15-min TTL), so only the hard cap
        // keeps memory bounded. Mint far past the cap and assert the live count never
        // exceeds it - the pre-hardening cache would hold every one of these.
        var cache = new OauthCsrfCache(new FakeTimeProvider(T0), TimeSpan.FromMinutes(15), maxEntries: 64);
        for (int i = 0; i < 64 * 20; i++)
        {
            cache.Mint();
        }
        cache.Count.Should().BeLessThanOrEqualTo(64);
    }
}
