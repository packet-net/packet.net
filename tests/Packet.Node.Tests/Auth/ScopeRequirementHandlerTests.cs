using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Packet.Node.Api;
using Packet.Node.Core.Auth;
using Packet.Node.Core.Configuration;
using Packet.Node.Tests.Support;

namespace Packet.Node.Tests.Auth;

/// <summary>
/// Unit tests for <see cref="ScopeRequirementHandler"/> - the single gate behind every
/// control-API and MCP policy. Two independent things are enforced here and nothing else
/// enforces either of them: the scope rank (admin ⊃ operate ⊃ read) and the token
/// <b>audience</b>.
/// </summary>
/// <remarks>
/// The audience half was completely untested (review item C057): <c>JwtTokenService</c>
/// authenticates BOTH audiences (a minted MCP token is a perfectly valid bearer), so the only
/// thing keeping an MCP credential off <c>/api/v1</c> - and a panel token off <c>/mcp</c> - is
/// the per-policy audience pin compared here. The WAF leg (a real minted MCP token bounced off
/// a real control-API route) lives in <c>ScopeAudienceApiTests</c>.
/// </remarks>
[Trait("Category", "Node")]
public sealed class ScopeRequirementHandlerTests
{
    private static readonly string ControlAudience = JwtTokenService.Audience;
    private static readonly string McpAudience = JwtTokenService.McpAudience;

    [Fact]
    public async Task An_mcp_token_does_not_satisfy_a_control_api_gate()
    {
        // Right scope, wrong audience: an operate-scoped MCP token on an operate control-API
        // gate. Scope alone would pass; the audience pin is what stops it.
        var context = Context(
            Principal(scope: AuthScopes.Operate, audience: McpAudience),
            new ScopeRequirement(AuthScopes.Operate, ControlAudience));

        await Handler(authEnabled: true).HandleAsync(context);

        context.HasSucceeded.Should().BeFalse();
    }

    [Fact]
    public async Task A_control_api_token_does_not_satisfy_the_mcp_gate()
    {
        var context = Context(
            Principal(scope: AuthScopes.Admin, audience: ControlAudience),
            new ScopeRequirement(AuthScopes.Read, McpAudience));

        await Handler(authEnabled: true).HandleAsync(context);

        context.HasSucceeded.Should().BeFalse();
    }

    [Theory]
    [InlineData(AuthScopes.Read, AuthScopes.Read, true)]
    [InlineData(AuthScopes.Operate, AuthScopes.Read, true)]     // implication: operate ⊃ read
    [InlineData(AuthScopes.Admin, AuthScopes.Operate, true)]    // implication: admin ⊃ operate
    [InlineData(AuthScopes.Read, AuthScopes.Operate, false)]
    [InlineData(AuthScopes.Operate, AuthScopes.Admin, false)]
    public async Task The_right_audience_then_decides_on_scope_rank(string granted, string required, bool expected)
    {
        var context = Context(
            Principal(scope: granted, audience: ControlAudience),
            new ScopeRequirement(required, ControlAudience));

        await Handler(authEnabled: true).HandleAsync(context);

        context.HasSucceeded.Should().Be(expected);
    }

    [Fact]
    public async Task An_mcp_token_satisfies_the_mcp_gate()
    {
        var context = Context(
            Principal(scope: AuthScopes.Read, audience: McpAudience),
            new ScopeRequirement(AuthScopes.Read, McpAudience));

        await Handler(authEnabled: true).HandleAsync(context);

        context.HasSucceeded.Should().BeTrue();
    }

    [Fact]
    public async Task An_unauthenticated_principal_never_satisfies_a_gate_when_auth_is_on()
    {
        var context = Context(
            new ClaimsPrincipal(new ClaimsIdentity()),   // no authentication type ⇒ not authenticated
            new ScopeRequirement(AuthScopes.Read, ControlAudience));

        await Handler(authEnabled: true).HandleAsync(context);

        context.HasSucceeded.Should().BeFalse();
    }

    [Fact]
    public async Task With_auth_off_every_requirement_passes_through()
    {
        // The default-off, no-regression contract: no principal, no audience, no scope.
        var context = Context(
            new ClaimsPrincipal(new ClaimsIdentity()),
            new ScopeRequirement(AuthScopes.Admin, ControlAudience));

        await Handler(authEnabled: false).HandleAsync(context);

        context.HasSucceeded.Should().BeTrue();
    }

    private static ScopeRequirementHandler Handler(bool authEnabled)
    {
        var config = new NodeConfig
        {
            Identity = new Identity { Callsign = "M0LTE-1" },
            Management = new ManagementConfig { Auth = new AuthConfig { Enabled = authEnabled } },
        };
        return new ScopeRequirementHandler(new TestConfigProvider(config));
    }

    private static ClaimsPrincipal Principal(string scope, string audience) =>
        new(new ClaimsIdentity(
            [
                new Claim("sub", "sysop"),
                new Claim(AuthScopes.ScopeClaim, scope),
                new Claim("aud", audience),
            ],
            authenticationType: "test"));

    private static AuthorizationHandlerContext Context(ClaimsPrincipal user, ScopeRequirement requirement) =>
        new([requirement], user, resource: null);
}
