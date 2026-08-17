using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Packet.Node.Core.Auth;
using Packet.Node.Core.Auth.Oauth;
using Packet.Node.Tests.Support;

namespace Packet.Node.Tests.Integration;

/// <summary>
/// Boots the real <c>Packet.Node</c> composition root with <c>mcp.oauth.enabled</c> and
/// exercises the MCP OAuth 2.1 flow (the hosted-claude.ai connector path): discovery,
/// dynamic client registration, the interactive authorize/consent, and the code→token
/// exchange — plus the security guards (PKCE S256, single-use codes, redirect/credential
/// checks). All on the in-memory TestServer. See PdnOauthApi + docs/mcp-oauth-design.md.
/// </summary>
[Trait("Category", "Node")]
public sealed class OauthApiTests : IDisposable
{
    private readonly string dir;
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    public OauthApiTests()
    {
        dir = TestPaths.NewPath("packetnet-oauth");
        Directory.CreateDirectory(dir);
        var configPath = Path.Combine(dir, "node.yaml");
        File.WriteAllText(configPath, """
            schemaVersion: 1
            identity:
              callsign: M0LTE-1
              alias: LONDON
            ports: []
            management:
              telnet:
                enabled: false
              http:
                bind: 127.0.0.1
                port: 8080
              auth:
                enabled: true
            mcp:
              oauth:
                enabled: true
            """);
        Environment.SetEnvironmentVariable("PACKETNET_CONFIG", configPath);
        Environment.SetEnvironmentVariable("PACKETNET_DB", Path.Combine(dir, "pdn.db"));
    }

    private sealed class NodeAppFactory : WebApplicationFactory<Program> { }

    private static HttpClient NoRedirect(NodeAppFactory f) =>
        f.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

    private const string RedirectUri = "https://claude.ai/api/mcp/callback";

    private static async Task<string> RegisterClientAsync(HttpClient client)
    {
        var resp = await client.PostAsJsonAsync("/oauth/register", new
        {
            client_name = "Claude",
            redirect_uris = new[] { RedirectUri },
        });
        resp.StatusCode.Should().Be(HttpStatusCode.Created);
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        return doc.RootElement.GetProperty("client_id").GetString()!;
    }

    private static void SeedUser(NodeAppFactory f, string username, string password, string scope)
    {
        var users = f.Services.GetRequiredService<IUserStore>();
        users.Create(new UserRecord(username, PasswordHasher.Hash(password), scope, DateTimeOffset.UtcNow, null));
    }

    [Fact]
    public async Task Discovery_describes_the_resource_and_authorization_server()
    {
        await using var factory = new NodeAppFactory();
        using var client = factory.CreateClient();

        using var prDoc = JsonDocument.Parse(await client.GetStringAsync("/.well-known/oauth-protected-resource"));
        prDoc.RootElement.GetProperty("resource").GetString().Should().EndWith("/mcp");
        prDoc.RootElement.GetProperty("authorization_servers").EnumerateArray().Should().NotBeEmpty();

        using var asDoc = JsonDocument.Parse(await client.GetStringAsync("/.well-known/oauth-authorization-server"));
        asDoc.RootElement.GetProperty("authorization_endpoint").GetString().Should().EndWith("/oauth/authorize");
        asDoc.RootElement.GetProperty("token_endpoint").GetString().Should().EndWith("/oauth/token");
        asDoc.RootElement.GetProperty("registration_endpoint").GetString().Should().EndWith("/oauth/register");
        asDoc.RootElement.GetProperty("code_challenge_methods_supported").EnumerateArray()
            .Select(e => e.GetString()).Should().Contain("S256");
    }

    [Fact]
    public async Task Register_issues_a_client_id_and_rejects_a_missing_redirect_uri()
    {
        await using var factory = new NodeAppFactory();
        using var client = factory.CreateClient();

        var clientId = await RegisterClientAsync(client);
        clientId.Should().StartWith("pdn-");

        var bad = await client.PostAsJsonAsync("/oauth/register", new { client_name = "x", redirect_uris = Array.Empty<string>() });
        bad.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Authorize_get_shows_consent_for_a_registered_client_and_rejects_an_unknown_one()
    {
        await using var factory = new NodeAppFactory();
        using var client = factory.CreateClient();
        var clientId = await RegisterClientAsync(client);

        var challenge = OauthPkce.ChallengeFor("a-verifier-that-is-long-enough-to-be-valid-1234567890");
        var url = $"/oauth/authorize?response_type=code&client_id={clientId}&redirect_uri={Uri.EscapeDataString(RedirectUri)}"
            + $"&code_challenge={challenge}&code_challenge_method=S256&scope=mcp:read&state=xyz";
        var html = await client.GetStringAsync(url);
        html.Should().Contain("Approve").And.Contain("Claude");

        var unknown = await client.GetAsync($"/oauth/authorize?response_type=code&client_id=nope&redirect_uri={Uri.EscapeDataString(RedirectUri)}&code_challenge={challenge}&code_challenge_method=S256");
        unknown.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Full_flow_login_to_code_to_access_token()
    {
        await using var factory = new NodeAppFactory();
        SeedUser(factory, "op", "correct horse battery staple", AuthScopes.Operate);
        using var client = NoRedirect(factory);
        var clientId = await RegisterClientAsync(client);

        const string verifier = "the-quick-brown-fox-jumps-over-the-lazy-dog-pkce-verifier";
        var challenge = OauthPkce.ChallengeFor(verifier);

        // Owner logs in + approves → 302 back to the client with a single-use code.
        var code = await ApproveAsync(client, clientId, challenge, "op", "correct horse battery staple", "mcp:operate");
        code.Should().NotBeNullOrEmpty();

        // Exchange code + verifier for an access token.
        var tokenResp = await client.PostAsync("/oauth/token", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["code"] = code!,
            ["client_id"] = clientId,
            ["redirect_uri"] = RedirectUri,
            ["code_verifier"] = verifier,
        }));
        tokenResp.StatusCode.Should().Be(HttpStatusCode.OK);
        using var doc = JsonDocument.Parse(await tokenResp.Content.ReadAsStringAsync());
        doc.RootElement.GetProperty("token_type").GetString().Should().Be("Bearer");
        doc.RootElement.GetProperty("scope").GetString().Should().Be("mcp:operate");
        // A well-formed JWT (header.payload.signature)...
        var accessToken = doc.RootElement.GetProperty("access_token").GetString()!;
        accessToken.Split('.').Should().HaveCount(3);
        // ...carrying the MCP audience (so it reaches /mcp only, never the control API).
        AudienceOf(accessToken).Should().Be(JwtTokenService.McpAudience);
    }

    // Decode the unverified JWT payload and read its `aud` claim (a string or an array).
    private static string? AudienceOf(string jwt)
    {
        var payload = jwt.Split('.')[1];
        payload = payload.Replace('-', '+').Replace('_', '/');
        payload = payload.PadRight(payload.Length + (4 - payload.Length % 4) % 4, '=');
        using var doc = JsonDocument.Parse(Convert.FromBase64String(payload));
        var aud = doc.RootElement.GetProperty("aud");
        return aud.ValueKind == JsonValueKind.Array ? aud[0].GetString() : aud.GetString();
    }

    [Fact]
    public async Task Token_rejects_a_bad_pkce_verifier()
    {
        await using var factory = new NodeAppFactory();
        SeedUser(factory, "op", "pw-pw-pw-pw-pw-pw", AuthScopes.Operate);
        using var client = NoRedirect(factory);
        var clientId = await RegisterClientAsync(client);
        var challenge = OauthPkce.ChallengeFor("the-real-verifier-aaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");
        var code = await ApproveAsync(client, clientId, challenge, "op", "pw-pw-pw-pw-pw-pw", "mcp:read");

        var resp = await client.PostAsync("/oauth/token", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["code"] = code!,
            ["client_id"] = clientId,
            ["redirect_uri"] = RedirectUri,
            ["code_verifier"] = "a-completely-different-verifier-bbbbbbbbbbbbbbbbbbbb",
        }));
        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        doc.RootElement.GetProperty("error").GetString().Should().Be("invalid_grant");
    }

    [Fact]
    public async Task An_authorization_code_is_single_use()
    {
        await using var factory = new NodeAppFactory();
        SeedUser(factory, "op", "pw-pw-pw-pw-pw-pw", AuthScopes.Read);
        using var client = NoRedirect(factory);
        var clientId = await RegisterClientAsync(client);
        const string verifier = "verifier-for-single-use-test-cccccccccccccccccccc";
        var code = await ApproveAsync(client, clientId, OauthPkce.ChallengeFor(verifier), "op", "pw-pw-pw-pw-pw-pw", "mcp:read");

        var form = () => new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["code"] = code!,
            ["client_id"] = clientId,
            ["redirect_uri"] = RedirectUri,
            ["code_verifier"] = verifier,
        });

        (await client.PostAsync("/oauth/token", form())).StatusCode.Should().Be(HttpStatusCode.OK);
        // Replay the same code → rejected.
        (await client.PostAsync("/oauth/token", form())).StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Bad_credentials_are_reprompted_not_redirected()
    {
        await using var factory = new NodeAppFactory();
        SeedUser(factory, "op", "the-right-password", AuthScopes.Operate);
        using var client = NoRedirect(factory);
        var clientId = await RegisterClientAsync(client);

        var resp = await PostApproveAsync(client, clientId, OauthPkce.ChallengeFor("verifier-dddddddddddddddddddddddddddddddddd"), "op", "WRONG", "mcp:read");
        resp.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task The_consent_post_rejects_a_missing_wrong_or_replayed_csrf_token()
    {
        await using var factory = new NodeAppFactory();
        SeedUser(factory, "op", "pw-pw-pw-pw-pw-pw", AuthScopes.Operate);
        using var client = NoRedirect(factory);
        var clientId = await RegisterClientAsync(client);
        var challenge = OauthPkce.ChallengeFor("verifier-csrf-ffffffffffffffffffffffffffffff");

        Dictionary<string, string> Form(string? csrf)
        {
            var d = new Dictionary<string, string>
            {
                ["response_type"] = "code",
                ["client_id"] = clientId,
                ["redirect_uri"] = RedirectUri,
                ["scope"] = "mcp:read",
                ["state"] = "st",
                ["code_challenge"] = challenge,
                ["code_challenge_method"] = "S256",
                ["action"] = "approve",
                ["username"] = "op",
                ["password"] = "pw-pw-pw-pw-pw-pw",
            };
            if (csrf is not null)
            {
                d["csrf"] = csrf;
            }
            return d;
        }

        // No token at all (a cross-site forge has none) → 400, no redirect to the client.
        (await client.PostAsync("/oauth/authorize", new FormUrlEncodedContent(Form(null))))
            .StatusCode.Should().Be(HttpStatusCode.BadRequest);

        // A token the server never minted → 400.
        (await client.PostAsync("/oauth/authorize", new FormUrlEncodedContent(Form("deadbeefdeadbeefdeadbeefdeadbeef"))))
            .StatusCode.Should().Be(HttpStatusCode.BadRequest);

        // The real token works exactly once (single-use)...
        var csrf = await FetchCsrfAsync(client, clientId, challenge, "mcp:read");
        (await client.PostAsync("/oauth/authorize", new FormUrlEncodedContent(Form(csrf))))
            .StatusCode.Should().Be(HttpStatusCode.Redirect);

        // ...and its replay is rejected (no second consent off one rendered page).
        (await client.PostAsync("/oauth/authorize", new FormUrlEncodedContent(Form(csrf))))
            .StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task The_consent_page_is_frame_protected()
    {
        await using var factory = new NodeAppFactory();
        using var client = factory.CreateClient();
        var clientId = await RegisterClientAsync(client);
        var challenge = OauthPkce.ChallengeFor("verifier-frame-00000000000000000000000000");

        var resp = await client.GetAsync(AuthorizeUrl(clientId, challenge, "mcp:operate"));
        resp.StatusCode.Should().Be(HttpStatusCode.OK);

        // The one-click operate-scope consent must never be iframed (clickjacking).
        Header(resp, "Content-Security-Policy").Should().Be("frame-ancestors 'none'");
        Header(resp, "X-Frame-Options").Should().Be("DENY");

        static string? Header(HttpResponseMessage r, string name) =>
            r.Headers.TryGetValues(name, out var v) ? v.First()
            : r.Content.Headers.TryGetValues(name, out var c) ? c.First() : null;
    }

    [Fact]
    public async Task The_credential_guess_budget_is_shared_with_auth_login()
    {
        // Splitting guesses across /auth/login and the OAuth consent POST must not
        // double the per-window password budget: both endpoints key the SAME throttle
        // namespace per username and per source IP.
        await using var factory = new NodeAppFactory();
        SeedUser(factory, "op", "the-right-password", AuthScopes.Operate);
        using var client = NoRedirect(factory);
        var clientId = await RegisterClientAsync(client);
        var challenge = OauthPkce.ChallengeFor("verifier-budget-1111111111111111111111111");

        // Three failures on /auth/login...
        for (int i = 0; i < 3; i++)
        {
            (await client.PostAsJsonAsync("/api/v1/auth/login", new { username = "op", password = "wrong" }, Web))
                .StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        }
        // ...plus two on the OAuth consent POST reach the shared 5-failure threshold...
        for (int i = 0; i < 2; i++)
        {
            (await PostApproveAsync(client, clientId, challenge, "op", "wrong", "mcp:read"))
                .StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        }
        // ...and the next attempt is locked out on BOTH endpoints, even with the right
        // password.
        (await client.PostAsJsonAsync("/api/v1/auth/login", new { username = "op", password = "the-right-password" }, Web))
            .StatusCode.Should().Be(HttpStatusCode.TooManyRequests);
        (await PostApproveAsync(client, clientId, challenge, "op", "the-right-password", "mcp:read"))
            .StatusCode.Should().Be(HttpStatusCode.TooManyRequests);
    }

    [Fact]
    public async Task Endpoints_are_404_when_oauth_is_disabled()
    {
        // A second config with OAuth off → the routes must not be exposed.
        var off = Path.Combine(dir, "off.yaml");
        File.WriteAllText(off, """
            schemaVersion: 1
            identity: { callsign: M0LTE-2, alias: L }
            ports: []
            management: { telnet: { enabled: false }, http: { bind: 127.0.0.1, port: 8080 } }
            """);
        Environment.SetEnvironmentVariable("PACKETNET_CONFIG", off);
        await using var factory = new NodeAppFactory();
        using var client = factory.CreateClient();

        (await client.GetAsync("/.well-known/oauth-authorization-server")).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await client.PostAsJsonAsync("/oauth/register", new { redirect_uris = new[] { RedirectUri } }))
            .StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    // Drive the consent POST and return the issued code from the 302 Location.
    private static async Task<string?> ApproveAsync(HttpClient client, string clientId, string challenge, string user, string pw, string scope)
    {
        var resp = await PostApproveAsync(client, clientId, challenge, user, pw, scope);
        resp.StatusCode.Should().Be(HttpStatusCode.Redirect);
        var query = new Uri(resp.Headers.Location!.ToString()).Query.TrimStart('?');
        foreach (var pair in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var kv = pair.Split('=', 2);
            if (kv[0] == "code")
            {
                return Uri.UnescapeDataString(kv.Length > 1 ? kv[1] : "");
            }
        }
        return null;
    }

    private static string AuthorizeUrl(string clientId, string challenge, string scope) =>
        $"/oauth/authorize?response_type=code&client_id={clientId}&redirect_uri={Uri.EscapeDataString(RedirectUri)}"
        + $"&code_challenge={challenge}&code_challenge_method=S256&scope={Uri.EscapeDataString(scope)}&state=st";

    // The consent POST is CSRF-protected: it only accepts the single-use token the GET
    // minted into the rendered consent page. Extract it from the hidden field.
    private static async Task<string> FetchCsrfAsync(HttpClient client, string clientId, string challenge, string scope)
    {
        var html = await client.GetStringAsync(AuthorizeUrl(clientId, challenge, scope));
        var match = System.Text.RegularExpressions.Regex.Match(html, "name=\"csrf\" value=\"([^\"]+)\"");
        match.Success.Should().BeTrue("the consent page must embed its single-use CSRF token");
        return match.Groups[1].Value;
    }

    private static async Task<HttpResponseMessage> PostApproveAsync(HttpClient client, string clientId, string challenge, string user, string pw, string scope)
    {
        var csrf = await FetchCsrfAsync(client, clientId, challenge, scope);
        return await client.PostAsync("/oauth/authorize", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["csrf"] = csrf,
            ["response_type"] = "code",
            ["client_id"] = clientId,
            ["redirect_uri"] = RedirectUri,
            ["scope"] = scope,
            ["state"] = "st",
            ["code_challenge"] = challenge,
            ["code_challenge_method"] = "S256",
            ["action"] = "approve",
            ["username"] = user,
            ["password"] = pw,
        }));
    }

    public void Dispose()
    {
        try { Directory.Delete(dir, recursive: true); } catch { /* best-effort temp cleanup */ }
    }
}
