using System.Net;
using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Packet.Mcp;
using Packet.Node.Core.Auth;
using Packet.Node.Core.Configuration;
using Packet.Node.Mcp;
using Packet.Node.Tests.Support;

namespace Packet.Node.Tests.Mcp;

/// <summary>
/// The SSE transport's caller resolution (RM-6): who the MCP request is, and which
/// tools it may reach. Two halves, both of which have bitten before - the scope
/// expansion (the hierarchical admin ⊃ operate ⊃ read model the REST gate uses) and
/// the actor name, which the WP6 fix (<c>PrincipalName</c>, review item C011) made
/// resolve identically however the bearer handler spelled the subject claim.
/// </summary>
public sealed class HttpContextMcpCallerAccessorTests
{
    // The node's JWTs carry the username in `sub`; the validation parameters set
    // NameClaimType = sub, and MapInboundClaims = false keeps it there. Spelt out
    // rather than referenced so this test pins the wire name, not our constant.
    private const string SubClaim = "sub";

    private sealed class StubHttpContextAccessor(HttpContext? context) : IHttpContextAccessor
    {
        public HttpContext? HttpContext { get; set; } = context;
    }

    private static NodeConfig Config(bool authEnabled) => new()
    {
        Identity = new Identity { Callsign = "NODE-1" },
        Management = new ManagementConfig { Auth = new AuthConfig { Enabled = authEnabled } },
    };

    private static McpCaller Caller(ClaimsPrincipal? user, bool authEnabled, IPAddress? remoteIp = null)
    {
        HttpContext? context = null;
        if (user is not null || remoteIp is not null)
        {
            var ctx = new DefaultHttpContext();
            if (user is not null)
            {
                ctx.User = user;
            }
            ctx.Connection.RemoteIpAddress = remoteIp;
            context = ctx;
        }

        var accessor = new HttpContextMcpCallerAccessor(
            new StubHttpContextAccessor(context), new TestConfigProvider(Config(authEnabled)));
        return accessor.Current;
    }

    /// <summary>A principal the way the bearer handler builds one: authenticated, with the
    /// username in whichever claim the caller names, and (optionally) a scope claim.</summary>
    private static ClaimsPrincipal Principal(string? scope, params Claim[] identityClaims)
    {
        var claims = new List<Claim>(identityClaims);
        if (scope is not null)
        {
            claims.Add(new Claim(AuthScopes.ScopeClaim, scope));
        }
        // nameType `sub` mirrors the node's TokenValidationParameters.
        return new ClaimsPrincipal(new ClaimsIdentity(claims, "Bearer", SubClaim, ClaimTypes.Role));
    }

    // ---- scopes ---------------------------------------------------------------------

    [Fact]
    public void Auth_disabled_is_a_pass_through_that_grants_every_scope()
    {
        var caller = Caller(user: null, authEnabled: false);

        caller.Scopes.Should().BeEquivalentTo(new[] { McpScopes.Read, McpScopes.Operate, McpScopes.Admin },
            "auth off means the REST gate lets everything through, and MCP mirrors it");
        caller.Transport.Should().Be("mcp:sse");
        caller.Actor.Should().Be("anonymous", "there is no authenticated subject to name");
    }

    [Fact]
    public void A_read_scoped_principal_gets_read_only_so_the_write_tools_stay_shut()
    {
        var caller = Caller(Principal(AuthScopes.Read, new Claim(SubClaim, "tom")), authEnabled: true);

        caller.HasScope(McpScopes.Read).Should().BeTrue();
        caller.HasScope(McpScopes.Operate).Should().BeFalse("read does not imply operate - the model only widens upwards");
        caller.HasScope(McpScopes.Admin).Should().BeFalse();
    }

    [Fact]
    public void An_operate_scoped_principal_gets_read_and_operate_but_not_admin()
    {
        var caller = Caller(Principal(AuthScopes.Operate, new Claim(SubClaim, "tom")), authEnabled: true);

        caller.Scopes.Should().BeEquivalentTo(new[] { McpScopes.Read, McpScopes.Operate });
    }

    [Fact]
    public void An_admin_scoped_principal_gets_every_scope_including_operate()
    {
        var caller = Caller(Principal(AuthScopes.Admin, new Claim(SubClaim, "tom")), authEnabled: true);

        caller.Scopes.Should().BeEquivalentTo(new[] { McpScopes.Read, McpScopes.Operate, McpScopes.Admin },
            "admin implies operate implies read, expanded here rather than carried as three claims on the token");
    }

    [Fact]
    public void A_principal_with_no_scope_claim_gets_nothing_at_all()
    {
        var caller = Caller(Principal(scope: null, new Claim(SubClaim, "tom")), authEnabled: true);

        caller.Scopes.Should().BeEmpty("an unknown or absent scope satisfies no tool");
        caller.Actor.Should().Be("tom", "we still know who was refused");
    }

    [Fact]
    public void An_unknown_scope_value_grants_nothing()
    {
        var caller = Caller(Principal("superuser", new Claim(SubClaim, "tom")), authEnabled: true);

        caller.Scopes.Should().BeEmpty();
    }

    [Fact]
    public void With_auth_on_and_no_request_principal_the_caller_holds_no_scope()
    {
        var caller = Caller(user: null, authEnabled: true);

        caller.Scopes.Should().BeEmpty();
        caller.Actor.Should().Be("anonymous");
    }

    // ---- the actor name (WP6 / C011) ------------------------------------------------

    [Fact]
    public void The_actor_is_the_token_subject_when_the_handler_leaves_sub_alone()
    {
        // MapInboundClaims = false + NameClaimType = sub: Identity.Name IS the username.
        var principal = Principal(AuthScopes.Operate, new Claim(SubClaim, "tom"));
        principal.Identity!.Name.Should().Be("tom", "the arrangement this test is pinning");

        Caller(principal, authEnabled: true).Actor.Should().Be("tom");
    }

    [Fact]
    public void The_actor_is_still_the_subject_when_the_handler_renamed_sub_to_nameidentifier()
    {
        // The C011 failure mode: JwtBearerOptions.MapInboundClaims defaults to true and
        // rewrites `sub` to ClaimTypes.NameIdentifier, leaving Name AND the sub lookup
        // null - which is how every audited action came out as "owner".
        var principal = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(ClaimTypes.NameIdentifier, "tom"), new Claim(AuthScopes.ScopeClaim, AuthScopes.Admin)],
            "Bearer"));
        principal.Identity!.Name.Should().BeNull("the rename is exactly what broke the naive read");

        Caller(principal, authEnabled: true).Actor.Should().Be("tom");
    }

    [Fact]
    public void An_unauthenticated_principal_is_anonymous_even_carrying_a_subject_claim()
    {
        // No authentication type ⇒ IsAuthenticated false ⇒ nothing is asserted about who
        // this is, so its claims must not name the actor in the audit trail.
        var principal = new ClaimsPrincipal(new ClaimsIdentity([new Claim(SubClaim, "mallory")]));
        principal.Identity!.IsAuthenticated.Should().BeFalse();

        Caller(principal, authEnabled: false).Actor.Should().Be("anonymous");
    }

    // ---- the client ip --------------------------------------------------------------

    [Fact]
    public void The_caller_carries_the_requests_remote_ip_for_the_audit_trail()
    {
        var caller = Caller(
            Principal(AuthScopes.Admin, new Claim(SubClaim, "tom")),
            authEnabled: true,
            remoteIp: IPAddress.Parse("192.0.2.17"));

        caller.ClientIp.Should().Be("192.0.2.17");
    }

    [Fact]
    public void No_http_context_at_all_yields_an_anonymous_caller_with_no_ip()
    {
        var caller = Caller(user: null, authEnabled: false);

        caller.ClientIp.Should().BeNull();
        caller.Actor.Should().Be("anonymous");
    }
}
