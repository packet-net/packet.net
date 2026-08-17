using System.Net.Http;
using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.IdentityModel.JsonWebTokens;
using Packet.Node.Api;

namespace Packet.Node.Tests.Applications;

/// <summary>
/// Unit tests for the app-gateway request transformer (the security-critical injection logic):
/// the path is rebased (the <c>/apps/{id}</c> prefix stripped), any client-supplied
/// <c>X-Pdn-*</c> is stripped, and the authenticated identity is injected — robust to the
/// inbound-claim mapping that can surface the username as the raw <c>sub</c> claim or the mapped
/// <c>NameIdentifier</c> rather than <c>Identity.Name</c> (the bug a lab live-verify caught: the
/// gateway injected an empty <c>X-Pdn-User</c> for a genuinely-authenticated user).
/// </summary>
[Trait("Category", "Node")]
public sealed class AppGatewayTransformerTests
{
    [Theory]
    [InlineData("name")]            // Identity.Name present
    [InlineData("sub")]             // mapped away from Name → raw sub claim
    [InlineData("nameidentifier")]  // mapped to the NameIdentifier URI (the production case)
    public void AuthenticatedUsername_reads_the_subject_however_claims_were_mapped(string where)
    {
        var claims = where switch
        {
            "name" => new[] { new Claim(ClaimTypes.Name, "tom") },
            "sub" => new[] { new Claim(JwtRegisteredClaimNames.Sub, "tom") },
            _ => new[] { new Claim(ClaimTypes.NameIdentifier, "tom") },
        };
        var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, authenticationType: "test"));

        Assert.Equal("tom", PdnAppGateway.AuthenticatedUsername(principal));
    }

    [Fact]
    public void AuthenticatedUsername_is_empty_for_an_unauthenticated_principal()
    {
        Assert.Equal(string.Empty, PdnAppGateway.AuthenticatedUsername(new ClaimsPrincipal(new ClaimsIdentity())));
        Assert.Equal(string.Empty, PdnAppGateway.AuthenticatedUsername(null));
    }

    [Fact]
    public async Task TransformRequest_rebases_the_path_strips_spoofed_identity_and_injects_the_real_one()
    {
        var transformer = new PdnAppGateway.AppGatewayTransformer();

        var ctx = new DefaultHttpContext();
        ctx.Request.Method = "GET";
        ctx.Request.Path = "/apps/wall/page";
        ctx.Request.QueryString = new QueryString("?x=1");
        ctx.Request.RouteValues["rest"] = "page";
        ctx.Request.Headers["X-Pdn-User"] = "attacker";   // a client trying to spoof identity
        // A genuinely-authenticated principal whose subject is only on the raw sub claim
        // (Identity.Name is null) — the case that produced an empty X-Pdn-User on the lab.
        ctx.User = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(JwtRegisteredClaimNames.Sub, "tom"), new Claim("scope", "admin")], "test"));

        using var proxyRequest = new HttpRequestMessage(HttpMethod.Get, "http://placeholder/");
        await transformer.TransformRequestAsync(ctx, proxyRequest, "http://127.0.0.1:9090", default);

        Assert.Equal("http://127.0.0.1:9090/page?x=1", proxyRequest.RequestUri!.ToString());   // prefix stripped
        Assert.Equal("tom", Single(proxyRequest, "X-Pdn-User"));                                // real identity, not "attacker"
        Assert.Equal("admin", Single(proxyRequest, "X-Pdn-Scope"));
        Assert.Equal("1", Single(proxyRequest, "X-Pdn-Gateway"));
    }

    [Fact]
    public async Task TransformRequest_strips_the_authorization_header_and_the_pdn_at_cookie()
    {
        // YARP's default copy forwards every non-hop-by-hop header, so an app upstream was
        // handed the viewer's bearer token AND the pdn_at cookie carrying the same access JWT -
        // replayable against /api/v1 with the viewer's scope (C054). The app is told who is
        // viewing; it is never given the credential.
        var transformer = new PdnAppGateway.AppGatewayTransformer();

        var ctx = new DefaultHttpContext();
        ctx.Request.Method = "GET";
        ctx.Request.Path = "/apps/wall/page";
        ctx.Request.RouteValues["rest"] = "page";
        ctx.Request.RouteValues["id"] = "wall";
        ctx.Request.Headers.Authorization = "Bearer header.payload.signature";
        ctx.Request.Headers.Cookie = "pdn_at=header.payload.signature; wall_session=keep-me";
        ctx.User = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(JwtRegisteredClaimNames.Sub, "tom"), new Claim("scope", "read")], "test"));

        using var proxyRequest = new HttpRequestMessage(HttpMethod.Get, "http://placeholder/");
        await transformer.TransformRequestAsync(ctx, proxyRequest, "http://127.0.0.1:9090", default);

        Single(proxyRequest, "Authorization").Should().BeNull();
        // The app keeps its OWN cookies - only pdn's session cookie is dropped.
        Single(proxyRequest, "Cookie").Should().Be("wall_session=keep-me");
        Single(proxyRequest, "X-Pdn-User").Should().Be("tom");   // identity still injected
    }

    [Fact]
    public async Task TransformRequest_drops_the_cookie_header_entirely_when_pdn_at_was_the_only_cookie()
    {
        var transformer = new PdnAppGateway.AppGatewayTransformer();

        var ctx = new DefaultHttpContext();
        ctx.Request.Method = "GET";
        ctx.Request.Path = "/apps/wall/";
        ctx.Request.RouteValues["rest"] = "";
        ctx.Request.RouteValues["id"] = "wall";
        ctx.Request.Headers.Cookie = "pdn_at=header.payload.signature";

        using var proxyRequest = new HttpRequestMessage(HttpMethod.Get, "http://placeholder/");
        await transformer.TransformRequestAsync(ctx, proxyRequest, "http://127.0.0.1:9090", default);

        Single(proxyRequest, "Cookie").Should().BeNull();
    }

    private static string? Single(HttpRequestMessage req, string header) =>
        req.Headers.TryGetValues(header, out var values) ? string.Concat(values) : null;
}
