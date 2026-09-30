using System.Net;
using System.Net.Http.Headers;
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
/// exchange - plus the security guards (PKCE S256, single-use codes, redirect/credential
/// checks). All on the in-memory TestServer. See PdnOauthApi + docs/mcp-oauth-design.md.
/// </summary>
[Trait("Category", "Node")]
public sealed class OauthApiTests : IDisposable
{
    private readonly string dir;
    private readonly string configPath;
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    public OauthApiTests()
    {
        dir = TestPaths.NewPath("packetnet-oauth");
        Directory.CreateDirectory(dir);
        configPath = Path.Combine(dir, "node.yaml");
        WriteConfig("");
        Environment.SetEnvironmentVariable("PACKETNET_CONFIG", configPath);
        Environment.SetEnvironmentVariable("PACKETNET_DB", Path.Combine(dir, "pdn.db"));
    }

    // The node's config, read once at first boot; `extraOauth` is appended under mcp.oauth
    // (two-space indented lines, e.g. an issuer), so a test can boot with a variant.
    private void WriteConfig(string extraOauth) =>
        File.WriteAllText(configPath, $"""
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
            {extraOauth}
            """);

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
    public async Task By_default_the_issuer_follows_the_request_host()
    {
        // The documented default (#427): with no pinned issuer, discovery is built on the
        // request's scheme and host. Behind the loopback-trusted forwarded headers that is
        // the sidecar's public address; from anyone else it is whatever Host they sent.
        await using var factory = new NodeAppFactory();
        using var client = factory.CreateClient();

        using var req = new HttpRequestMessage(HttpMethod.Get, "/.well-known/oauth-authorization-server");
        req.Headers.Host = "node.lan:8080";
        using var doc = JsonDocument.Parse(await (await client.SendAsync(req)).Content.ReadAsStringAsync());
        doc.RootElement.GetProperty("issuer").GetString().Should().Be("http://node.lan:8080");
        doc.RootElement.GetProperty("token_endpoint").GetString().Should().Be("http://node.lan:8080/oauth/token");
    }

    [Fact]
    public async Task A_pinned_issuer_is_what_discovery_and_iss_carry_whatever_the_host_header_says()
    {
        // mcp.oauth.issuer (#427): the operator's canonical address, for a node behind a
        // TLS proxy that is not the loopback sidecar, or wherever the issuer must not
        // follow the client's Host header. A trailing slash is ignored.
        WriteConfig("    issuer: https://pdn.example:8443/");
        await using var factory = new NodeAppFactory();
        SeedUser(factory, "op", "correct horse battery staple", AuthScopes.Operate);
        using var client = NoRedirect(factory);

        using var req = new HttpRequestMessage(HttpMethod.Get, "/.well-known/oauth-authorization-server");
        req.Headers.Host = "evil.example:9999";
        using var asDoc = JsonDocument.Parse(await (await client.SendAsync(req)).Content.ReadAsStringAsync());
        asDoc.RootElement.GetProperty("issuer").GetString().Should().Be("https://pdn.example:8443");
        asDoc.RootElement.GetProperty("authorization_endpoint").GetString().Should().Be("https://pdn.example:8443/oauth/authorize");
        asDoc.RootElement.GetProperty("registration_endpoint").GetString().Should().Be("https://pdn.example:8443/oauth/register");

        using var prReq = new HttpRequestMessage(HttpMethod.Get, "/.well-known/oauth-protected-resource");
        prReq.Headers.Host = "evil.example:9999";
        using var prDoc = JsonDocument.Parse(await (await client.SendAsync(prReq)).Content.ReadAsStringAsync());
        prDoc.RootElement.GetProperty("resource").GetString().Should().Be("https://pdn.example:8443/mcp");
        prDoc.RootElement.GetProperty("authorization_servers")[0].GetString().Should().Be("https://pdn.example:8443");

        // The RFC 9207 iss on the authorization response says the same.
        var clientId = await RegisterClientAsync(client);
        var challenge = OauthPkce.ChallengeFor("the-quick-brown-fox-jumps-over-the-lazy-dog-pkce-verifier");
        var resp = await PostApproveAsync(client, clientId, challenge, "op", "correct horse battery staple", "mcp:operate");
        resp.StatusCode.Should().Be(HttpStatusCode.Redirect);
        resp.Headers.Location!.ToString().Should().Contain("iss=" + Uri.EscapeDataString("https://pdn.example:8443"));
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
    public async Task Registration_has_a_per_address_budget()
    {
        // Open registration is what the MCP connector flow needs, so the guard is a budget,
        // not a gate (#426): the login throttle's count and window, under a key of its own.
        await using var factory = new NodeAppFactory();
        using var client = factory.CreateClient();

        for (int i = 0; i < LoginThrottle.DefaultMaxFailures; i++)
        {
            await RegisterClientAsync(client);
        }

        var over = await client.PostAsJsonAsync("/oauth/register", new { client_name = "one more", redirect_uris = new[] { RedirectUri } });
        over.StatusCode.Should().Be(HttpStatusCode.TooManyRequests);
        using var doc = JsonDocument.Parse(await over.Content.ReadAsStringAsync());
        doc.RootElement.GetProperty("error").GetString().Should().Be("temporarily_unavailable");

        // A registrant's budget and a login's never touch: the login throttle is not tripped.
        var login = await client.PostAsJsonAsync("/api/v1/auth/login", new { username = "nobody", password = "x" }, Web);
        login.StatusCode.Should().NotBe(HttpStatusCode.TooManyRequests);
    }

    [Fact]
    public async Task The_operator_can_close_registration_and_discovery_stops_advertising_it()
    {
        WriteConfig("    allowDynamicRegistration: false");
        await using var factory = new NodeAppFactory();
        using var client = factory.CreateClient();

        var resp = await client.PostAsJsonAsync("/oauth/register", new { client_name = "Claude", redirect_uris = new[] { RedirectUri } });
        resp.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        using var err = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        err.RootElement.GetProperty("error").GetString().Should().Be("access_denied");

        using var asDoc = JsonDocument.Parse(await client.GetStringAsync("/.well-known/oauth-authorization-server"));
        asDoc.RootElement.TryGetProperty("registration_endpoint", out _).Should().BeFalse(
            "RFC 8414 makes registration_endpoint optional, and a closed one is not advertised");
        asDoc.RootElement.GetProperty("token_endpoint").GetString().Should().EndWith("/oauth/token");
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
        // The name is the registrant's own claim (#426): the page says so and shows the
        // registered redirect target, which is what the owner can actually judge.
        html.Should().Contain("has not been verified").And.Contain(RedirectUri);

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
    public async Task The_token_response_carries_a_refresh_token_that_rotates_the_grant()
    {
        // #428, second slice: the code exchange mints a refresh token in a family recorded
        // against the client and consented scope; grant_type=refresh_token rotates it and
        // re-issues that grant, and the successor works where the presented one did.
        await using var factory = new NodeAppFactory();
        SeedUser(factory, "op", "correct horse battery staple", AuthScopes.Operate);
        using var client = NoRedirect(factory);
        var clientId = await RegisterClientAsync(client);

        using var first = JsonDocument.Parse(await ExchangeCodeAsync(client, clientId, "mcp:operate"));
        var refreshToken = first.RootElement.GetProperty("refresh_token").GetString()!;
        refreshToken.Should().NotBeNullOrEmpty();

        using var rotated = JsonDocument.Parse(await RefreshAsync(client, clientId, refreshToken, HttpStatusCode.OK));
        rotated.RootElement.GetProperty("scope").GetString().Should().Be("mcp:operate", "the grant's scope, not the user's whole scope");
        AudienceOf(rotated.RootElement.GetProperty("access_token").GetString()!).Should().Be(JwtTokenService.McpAudience);
        var next = rotated.RootElement.GetProperty("refresh_token").GetString()!;
        next.Should().NotBe(refreshToken, "a refresh token is one-time use");
        rotated.RootElement.GetProperty("access_token").GetString().Should().NotBe(first.RootElement.GetProperty("access_token").GetString());

        using var again = JsonDocument.Parse(await RefreshAsync(client, clientId, next, HttpStatusCode.OK));
        again.RootElement.GetProperty("refresh_token").GetString().Should().NotBe(next);

        using var asDoc = JsonDocument.Parse(await client.GetStringAsync("/.well-known/oauth-authorization-server"));
        asDoc.RootElement.GetProperty("grant_types_supported").EnumerateArray().Select(e => e.GetString()).Should().Contain("refresh_token");
    }

    [Fact]
    public async Task A_refresh_token_is_bound_to_its_client_and_a_revoked_one_ends_its_family()
    {
        await using var factory = new NodeAppFactory();
        SeedUser(factory, "op", "correct horse battery staple", AuthScopes.Operate);
        using var client = NoRedirect(factory);
        var clientId = await RegisterClientAsync(client);
        var otherClient = await RegisterClientAsync(client);

        using var issued = JsonDocument.Parse(await ExchangeCodeAsync(client, clientId, "mcp:read"));
        var refreshToken = issued.RootElement.GetProperty("refresh_token").GetString()!;

        // Presented by another registered client: refused, and the family is ended, so the
        // rightful client cannot use it either.
        var wrong = await RefreshAsync(client, otherClient, refreshToken, HttpStatusCode.BadRequest);
        JsonDocument.Parse(wrong).RootElement.GetProperty("error").GetString().Should().Be("invalid_grant");
        await RefreshAsync(client, clientId, refreshToken, HttpStatusCode.BadRequest);

        // A fresh grant, revoked through /oauth/revoke with the refresh token: over from then on.
        using var second = JsonDocument.Parse(await ExchangeCodeAsync(client, clientId, "mcp:read"));
        var live = second.RootElement.GetProperty("refresh_token").GetString()!;
        (await client.PostAsync("/oauth/revoke", new FormUrlEncodedContent(new Dictionary<string, string> { ["token"] = live })))
            .StatusCode.Should().Be(HttpStatusCode.OK);
        await RefreshAsync(client, clientId, live, HttpStatusCode.BadRequest);
    }

    // Run the whole authorize flow for `scope` and return the token endpoint's JSON body.
    private static async Task<string> ExchangeCodeAsync(HttpClient client, string clientId, string scope)
    {
        const string verifier = "the-quick-brown-fox-jumps-over-the-lazy-dog-pkce-verifier";
        var code = await ApproveAsync(client, clientId, OauthPkce.ChallengeFor(verifier), "op", "correct horse battery staple", scope);
        var resp = await client.PostAsync("/oauth/token", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["code"] = code!,
            ["client_id"] = clientId,
            ["redirect_uri"] = RedirectUri,
            ["code_verifier"] = verifier,
        }));
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        return await resp.Content.ReadAsStringAsync();
    }

    private static async Task<string> RefreshAsync(HttpClient client, string clientId, string refreshToken, HttpStatusCode expected)
    {
        var resp = await client.PostAsync("/oauth/token", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "refresh_token",
            ["refresh_token"] = refreshToken,
            ["client_id"] = clientId,
        }));
        resp.StatusCode.Should().Be(expected);
        return await resp.Content.ReadAsStringAsync();
    }

    [Fact]
    public async Task Revoke_refuses_the_presented_token_from_the_next_request()
    {
        // #428: every token carries a jti, /oauth/revoke lists a presented token's id, and the
        // bearer path refuses a listed id. The denylist sits below the audiences, so a panel
        // token and a connector token are revoked the same way; a panel token is the one a
        // test can present to a gated route here.
        await using var factory = new NodeAppFactory();
        SeedUser(factory, "op", "correct horse battery staple", AuthScopes.Operate);
        using var client = factory.CreateClient();

        var login = await client.PostAsJsonAsync("/api/v1/auth/login", new { username = "op", password = "correct horse battery staple" }, Web);
        login.StatusCode.Should().Be(HttpStatusCode.OK);
        var token = (await login.Content.ReadFromJsonAsync<JsonElement>(Web)).GetProperty("token").GetString()!;

        (await GetWith(client, token, "/api/v1/status")).Should().Be(HttpStatusCode.OK);

        var revoke = await client.PostAsync("/oauth/revoke", new FormUrlEncodedContent(new Dictionary<string, string> { ["token"] = token }));
        revoke.StatusCode.Should().Be(HttpStatusCode.OK);
        (await GetWith(client, token, "/api/v1/status")).Should().Be(HttpStatusCode.Unauthorized, "the id is listed until the token's own expiry");

        // RFC 7009: an invalid token is 200 as well, and lists nothing.
        (await client.PostAsync("/oauth/revoke", new FormUrlEncodedContent(new Dictionary<string, string> { ["token"] = "not-a-token" })))
            .StatusCode.Should().Be(HttpStatusCode.OK);
    }

    private static async Task<HttpStatusCode> GetWith(HttpClient client, string bearer, string path)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, path);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
        return (await client.SendAsync(req)).StatusCode;
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
