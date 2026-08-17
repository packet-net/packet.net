using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Packet.Node.Api;
using Packet.Node.Core.Auth;
using Packet.Node.Mcp;
using Packet.Node.Tests.Support;

namespace Packet.Node.Tests.Integration;

/// <summary>
/// <c>POST /api/v1/auth/service-token</c>: the long-lived CONTROL-API credential a headless
/// caller needs, and the reason it had to exist (#727 item 2).
/// </summary>
/// <remarks>
/// <para>
/// The <c>pdn mcp</c> stdio bridge drives the node's REST control API, and #694's C061 fix told
/// the operator to mint its token with <c>POST /api/v1/mcp/token</c>. That endpoint mints the
/// <c>packet.net-mcp</c> audience; every route the bridge calls is pinned to
/// <c>packet.net-control-api</c>, so following the instruction produced a permanent 403 whose
/// wording blamed the SCOPE. No endpoint minted a long-lived control-API token at all, which
/// left <c>pdn mcp</c> unusable with auth on beyond a 60-minute login token.
/// </para>
/// <para>
/// The last test here drives the REAL <see cref="RestNodeMcpBackend"/> against the REAL booted
/// node with auth on: that is the assertion the regression would have failed.
/// </para>
/// </remarks>
[Trait("Category", "Node")]
public sealed class ServiceTokenApiTests : IDisposable
{
    private readonly AuthNode node = new("servicetoken");

    [Fact]
    public async Task It_mints_a_control_api_token_subjected_to_the_service_name()
    {
        using var client = await BootAuthOn();
        var adminToken = await AuthNode.Login(client, "admin", "adminpassword");

        var mint = await AuthNode.PostJson(client, adminToken, "/api/v1/auth/service-token",
            new { name = "mcp-bridge", scope = "operate", days = 30 });

        mint.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await mint.Content.ReadFromJsonAsync<JsonElement>(AuthNode.Web);
        body.GetProperty("scope").GetString().Should().Be(AuthScopes.Operate);
        body.GetProperty("subject").GetString().Should().Be("service:mcp-bridge");
        body.GetProperty("tokenType").GetString().Should().Be("Bearer");

        var claims = AuthNode.DecodeJwtPayload(body.GetProperty("token").GetString()!);
        claims.GetProperty("aud").GetString().Should().Be(JwtTokenService.Audience,
            "this is the whole point: the control API is what the token has to reach");
        claims.GetProperty("sub").GetString().Should().Be("service:mcp-bridge",
            "a service credential must be distinguishable from a human account in `sub` and the audit log");
        claims.GetProperty("scope").GetString().Should().Be(AuthScopes.Operate);
    }

    [Fact]
    public async Task The_minted_token_actually_drives_the_control_api()
    {
        using var client = await BootAuthOn();
        var adminToken = await AuthNode.Login(client, "admin", "adminpassword");
        var serviceToken = await MintAsync(client, adminToken, "scripts", "read");

        var ports = await AuthNode.Get(client, serviceToken, "/api/v1/ports");
        ports.StatusCode.Should().Be(HttpStatusCode.OK);

        // read is read: a write is still refused.
        var write = await AuthNode.PostJson(client, serviceToken, "/api/v1/sessions", new { target = "GB7RDG-1" });
        write.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task The_mcp_stdio_bridge_can_read_the_node_with_one_and_not_with_an_mcp_token()
    {
        using var client = await BootAuthOn();
        var adminToken = await AuthNode.Login(client, "admin", "adminpassword");

        // The bridge, wired exactly as McpStdioEntry wires it, but pointed at the test server.
        var serviceToken = await MintAsync(client, adminToken, "mcp-bridge", "operate");
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", serviceToken);
        var backend = new RestNodeMcpBackend(client, TimeProvider.System);

        var ports = await backend.ListPortsAsync();
        ports.Should().ContainSingle().Which.Id.Should().Be("vhf",
            "the bridge's first tool call must succeed on an auth-on node");

        // And the credential the old message told the operator to mint still does not work
        // here, which is exactly why the message had to change.
        var mcpMint = await AuthNode.PostJson(client, adminToken, "/api/v1/mcp/token", new { scope = "operate" });
        mcpMint.StatusCode.Should().Be(HttpStatusCode.OK);
        var mcpToken = (await mcpMint.Content.ReadFromJsonAsync<JsonElement>(AuthNode.Web))
            .GetProperty("token").GetString()!;
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", mcpToken);

        var act = async () => await backend.ListPortsAsync();
        (await act.Should().ThrowAsync<InvalidOperationException>())
            .Which.Message.Should().Contain("auth/service-token",
                "the message must name the mint that actually works");
    }

    [Fact]
    public async Task It_is_admin_gated_bounded_and_picky_about_its_inputs()
    {
        using var client = await BootAuthOn();
        var adminToken = await AuthNode.Login(client, "admin", "adminpassword");
        var operateToken = await AuthNode.Login(client, "operator", "operatorpassword");

        // Minting a durable credential is an admin action.
        (await AuthNode.PostJson(client, operateToken, "/api/v1/auth/service-token", new { name = "sneaky" }))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);

        // A name is required, and it has to be a name (it becomes `sub`).
        (await AuthNode.PostJson(client, adminToken, "/api/v1/auth/service-token", new { name = "  " }))
            .StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await AuthNode.PostJson(client, adminToken, "/api/v1/auth/service-token", new { name = "bad:name" }))
            .StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await AuthNode.PostJson(client, adminToken, "/api/v1/auth/service-token", new { name = "ok", scope = "nonsense" }))
            .StatusCode.Should().Be(HttpStatusCode.BadRequest);

        // Scope defaults to read.
        var defaulted = await AuthNode.PostJson(client, adminToken, "/api/v1/auth/service-token", new { name = "defaults" });
        defaulted.StatusCode.Should().Be(HttpStatusCode.OK);
        var defaultedBody = await defaulted.Content.ReadFromJsonAsync<JsonElement>(AuthNode.Web);
        defaultedBody.GetProperty("scope").GetString().Should().Be(AuthScopes.Read);

        // Lifetime is bounded: long-lived is the point, forever is not (a stateless JWT can
        // only be revoked by rotating the signing key).
        var greedy = await AuthNode.PostJson(client, adminToken, "/api/v1/auth/service-token",
            new { name = "greedy", days = 3650 });
        greedy.StatusCode.Should().Be(HttpStatusCode.OK);
        var expires = (await greedy.Content.ReadFromJsonAsync<JsonElement>(AuthNode.Web))
            .GetProperty("expiresAt").GetDateTimeOffset();
        expires.Should().BeBefore(DateTimeOffset.UtcNow.AddDays(PdnAuthApi.MaxServiceTokenDays + 1));
    }

    [Fact]
    public async Task The_mint_is_audited_under_the_service_name()
    {
        using var client = await BootAuthOn();
        var adminToken = await AuthNode.Login(client, "admin", "adminpassword");
        await MintAsync(client, adminToken, "audited-bridge", "read");

        var audit = await AuthNode.Get(client, adminToken, "/api/v1/audit?limit=50");
        audit.StatusCode.Should().Be(HttpStatusCode.OK);
        var rows = await audit.Content.ReadFromJsonAsync<JsonElement>(AuthNode.Web);

        rows.EnumerateArray().Should().Contain(r =>
            r.GetProperty("action").GetString() == "service_token"
            && r.GetProperty("target").GetString() == "audited-bridge");
    }

    private static async Task<string> MintAsync(HttpClient client, string adminToken, string name, string scope)
    {
        var resp = await AuthNode.PostJson(client, adminToken, "/api/v1/auth/service-token", new { name, scope });
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        return (await resp.Content.ReadFromJsonAsync<JsonElement>(AuthNode.Web)).GetProperty("token").GetString()!;
    }

    // Claim the node, add an operate user, turn auth on, and hand back a client on a fresh host
    // booted over the same db.
    private async Task<HttpClient> BootAuthOn()
    {
        node.WriteConfig(authEnabled: false);

        await using (var setupFactory = node.Factory())
        using (var setupClient = setupFactory.CreateClient())
        {
            await AuthNode.Setup(setupClient, "admin", "adminpassword");
            var adminToken = await AuthNode.Login(setupClient, "admin", "adminpassword");
            await AuthNode.CreateUser(setupClient, adminToken, "operator", "operatorpassword", "operate");
            await AuthNode.FlipAuthOn(setupClient);
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
