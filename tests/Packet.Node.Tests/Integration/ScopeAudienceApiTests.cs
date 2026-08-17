using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Packet.Node.Core.Auth;
using Packet.Node.Tests.Support;

namespace Packet.Node.Tests.Integration;

/// <summary>
/// The audience wall between the MCP surface and the control API, exercised with real minted
/// tokens against a real route table (review items C057 + C108).
/// </summary>
/// <remarks>
/// <c>JwtTokenService.ValidationParameters</c> accepts BOTH audiences, so an MCP token
/// authenticates perfectly well on <c>/api/v1</c>; the only thing that stops it is the
/// audience pinned on each policy. Nothing tested that end to end - and nothing tested
/// <c>POST /api/v1/mcp/token</c> at all, guard branches included.
/// </remarks>
[Trait("Category", "Node")]
public sealed class ScopeAudienceApiTests : IDisposable
{
    // The MCP transport only mounts when both flags are on; without it /mcp is not a route
    // and the "a panel token can't reach /mcp" half would prove nothing.
    private const string McpBlock = """
        mcp:
          enabled: true
          sse:
            enabled: true
            path: /mcp
        """;

    private readonly AuthNode node = new("scopeaud");

    [Fact]
    public async Task An_mcp_token_is_minted_on_the_mcp_audience_and_bounces_off_the_control_api()
    {
        using var client = await BootAuthOn();
        var adminToken = await AuthNode.Login(client, "admin", "adminpassword");

        var mint = await AuthNode.PostJson(client, adminToken, "/api/v1/mcp/token", new { scope = "operate" });
        mint.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await mint.Content.ReadFromJsonAsync<JsonElement>(AuthNode.Web);
        body.GetProperty("scope").GetString().Should().Be("operate");
        body.GetProperty("tokenType").GetString().Should().Be("Bearer");

        var mcpToken = body.GetProperty("token").GetString()!;
        var claims = AuthNode.DecodeJwtPayload(mcpToken);
        claims.GetProperty("aud").GetString().Should().Be(JwtTokenService.McpAudience);
        claims.GetProperty("scope").GetString().Should().Be(AuthScopes.Operate);

        // The wall: an operate-scoped MCP token on a READ control-API endpoint. It
        // authenticates (same key, same issuer, valid audience list) - the policy's audience
        // pin is what makes it a 403.
        var status = await AuthNode.Get(client, mcpToken, "/api/v1/status");
        status.StatusCode.Should().Be(HttpStatusCode.Forbidden);

        // ... and it cannot mint itself a fresh one either.
        var remint = await AuthNode.PostJson(client, mcpToken, "/api/v1/mcp/token", new { scope = "operate" });
        remint.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task A_control_api_token_cannot_reach_the_mcp_endpoint()
    {
        using var client = await BootAuthOn();
        var adminToken = await AuthNode.Login(client, "admin", "adminpassword");

        // An ADMIN panel token - the most privileged control-API credential there is - is
        // still the wrong audience for /mcp.
        var mcp = await AuthNode.Send(client, HttpMethod.Post, "/mcp", adminToken,
            JsonContent.Create(new { jsonrpc = "2.0", id = 1, method = "tools/list" }, options: AuthNode.Web));
        mcp.StatusCode.Should().Be(HttpStatusCode.Forbidden);

        // No token at all is a 401, so the 403 above is a scope/audience decision, not a
        // missing route.
        var anonymous = await AuthNode.Send(client, HttpMethod.Post, "/mcp", token: null,
            JsonContent.Create(new { jsonrpc = "2.0", id = 1, method = "tools/list" }, options: AuthNode.Web));
        anonymous.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task The_mint_endpoint_is_admin_gated_and_validates_its_scope()
    {
        using var client = await BootAuthOn();
        var adminToken = await AuthNode.Login(client, "admin", "adminpassword");
        var operateToken = await AuthNode.Login(client, "operator", "operatorpassword");

        // Minting a durable credential is an admin action.
        (await AuthNode.PostJson(client, operateToken, "/api/v1/mcp/token", new { scope = "read" }))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);

        // read is the default and the floor; admin-over-MCP is not offered at all.
        var defaulted = await AuthNode.PostJson(client, adminToken, "/api/v1/mcp/token", new { });
        defaulted.StatusCode.Should().Be(HttpStatusCode.OK);
        (await defaulted.Content.ReadFromJsonAsync<JsonElement>(AuthNode.Web))
            .GetProperty("scope").GetString().Should().Be(AuthScopes.Read);

        (await AuthNode.PostJson(client, adminToken, "/api/v1/mcp/token", new { scope = "admin" }))
            .StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await AuthNode.PostJson(client, adminToken, "/api/v1/mcp/token", new { scope = "nonsense" }))
            .StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    // Claim the node, add an operate user, turn auth on, and hand back a client on a fresh
    // host booted over the same db. The factory is kept alive by the returned client's
    // handler, which is all these tests need.
    private async Task<HttpClient> BootAuthOn()
    {
        node.WriteConfig(authEnabled: false, McpBlock);

        await using (var setupFactory = node.Factory())
        using (var setupClient = setupFactory.CreateClient())
        {
            await AuthNode.Setup(setupClient, "admin", "adminpassword");
            var adminToken = await AuthNode.Login(setupClient, "admin", "adminpassword");
            await AuthNode.CreateUser(setupClient, adminToken, "operator", "operatorpassword", "operate");
            await AuthNode.FlipAuthOn(setupClient, McpBlock);
        }

        factory = node.Factory();
        return factory.CreateClient();
    }

    private AuthNode.NodeAppFactory? factory;

    public void Dispose()
    {
        factory?.Dispose();
        node.Dispose();
    }
}
