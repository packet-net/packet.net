using System.Security.Cryptography;
using Microsoft.Extensions.Time.Testing;
using Packet.Node.Core.Auth;

namespace Packet.Node.Tests.Auth;

/// <summary>
/// JWT issue → validate: a fresh token validates and carries sub + scope; a tampered
/// token is rejected; an expired token is rejected (driven by the fake clock - no
/// wall-clock).
/// </summary>
[Trait("Category", "Node")]
public class JwtTokenServiceTests
{
    private static readonly byte[] Key = RandomNumberGenerator.GetBytes(32);

    private static JwtTokenService Make(FakeTimeProvider clock, TimeSpan? lifetime = null) =>
        new(Key, lifetime ?? TimeSpan.FromHours(1), clock);

    [Fact]
    public async Task A_valid_token_validates_and_carries_sub_and_scope()
    {
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 6, 9, 0, 0, 0, TimeSpan.Zero));
        var svc = Make(clock);

        var (token, expiresAt) = svc.Issue("m0lte", AuthScopes.Admin);
        expiresAt.Should().Be(clock.GetUtcNow() + TimeSpan.FromHours(1));

        var principal = await svc.ValidateAsync(token);
        principal.Should().NotBeNull();
        principal!.FindFirst("sub")!.Value.Should().Be("m0lte");
        principal.FindFirst(AuthScopes.ScopeClaim)!.Value.Should().Be(AuthScopes.Admin);
    }

    [Fact]
    public async Task A_long_lived_token_honours_the_explicit_lifetime_and_still_validates()
    {
        // The MCP bearer overload: a 90-day token still validates the same way (issuer/
        // audience/key/scope), only the expiry differs from the service default.
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 6, 13, 0, 0, 0, TimeSpan.Zero));
        var svc = Make(clock, TimeSpan.FromHours(1));

        var (token, expiresAt) = svc.Issue("mcp:m0lte", AuthScopes.Read, TimeSpan.FromDays(90));

        expiresAt.Should().Be(clock.GetUtcNow() + TimeSpan.FromDays(90), "the explicit lifetime wins over the 1h default");
        var principal = await svc.ValidateAsync(token);
        principal.Should().NotBeNull();
        principal!.FindFirst("sub")!.Value.Should().Be("mcp:m0lte");
        principal.FindFirst(AuthScopes.ScopeClaim)!.Value.Should().Be(AuthScopes.Read);
    }

    [Fact]
    public async Task The_default_overloads_stamp_the_control_api_audience()
    {
        var svc = Make(new FakeTimeProvider());
        var principal = await svc.ValidateAsync(svc.Issue("m0lte", AuthScopes.Read).Token);
        principal!.FindFirst("aud")!.Value.Should().Be(JwtTokenService.Audience);
    }

    [Fact]
    public async Task The_audience_overload_stamps_the_mcp_audience_and_still_validates()
    {
        // An MCP-audience token validates through the same parameters (both audiences
        // are accepted at the middleware) - the segregation is enforced at the policy,
        // not by rejecting the token outright.
        var svc = Make(new FakeTimeProvider());
        var (token, _) = svc.Issue("mcp:m0lte", AuthScopes.Read, TimeSpan.FromDays(90), JwtTokenService.McpAudience);

        var principal = await svc.ValidateAsync(token);
        principal.Should().NotBeNull();
        principal!.FindFirst("aud")!.Value.Should().Be(JwtTokenService.McpAudience);
    }

    [Fact]
    public void Issue_rejects_a_non_positive_lifetime()
    {
        var svc = Make(new FakeTimeProvider());
        var act = () => svc.Issue("m0lte", AuthScopes.Read, TimeSpan.Zero);
        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public async Task A_tampered_token_is_rejected()
    {
        var clock = new FakeTimeProvider();
        var svc = Make(clock);
        var (token, _) = svc.Issue("m0lte", AuthScopes.Read);

        // Flip a character in the signature segment.
        var parts = token.Split('.');
        parts[2] = parts[2].Length > 0 && parts[2][0] != 'A' ? "A" + parts[2][1..] : "B" + parts[2][1..];
        var tampered = string.Join('.', parts);

        (await svc.ValidateAsync(tampered)).Should().BeNull();
    }

    [Fact]
    public async Task An_expired_token_is_rejected()
    {
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 6, 9, 0, 0, 0, TimeSpan.Zero));
        var svc = Make(clock, TimeSpan.FromMinutes(30));
        var (token, _) = svc.Issue("m0lte", AuthScopes.Read);

        // Still valid inside the window.
        (await svc.ValidateAsync(token)).Should().NotBeNull();

        // Advance past expiry → rejected (the LifetimeValidator reads the fake clock).
        clock.Advance(TimeSpan.FromMinutes(31));
        (await svc.ValidateAsync(token)).Should().BeNull();
    }

    [Fact]
    public async Task A_token_signed_by_a_different_key_is_rejected()
    {
        var clock = new FakeTimeProvider();
        var (token, _) = Make(clock).Issue("m0lte", AuthScopes.Read);

        var other = new JwtTokenService(RandomNumberGenerator.GetBytes(32), TimeSpan.FromHours(1), clock);
        (await other.ValidateAsync(token)).Should().BeNull();
    }
}
