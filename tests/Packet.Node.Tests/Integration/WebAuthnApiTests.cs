using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Packet.Node.Tests.Integration;

/// <summary>
/// Boots the real <c>Packet.Node</c> composition root and exercises the WebAuthn /
/// passkey endpoint plumbing: the always-open assert endpoints + the gated register /
/// credential-management group, that <c>begin</c> returns well-formed options with a
/// server-issued challenge, that a malformed / replayed assert is rejected, and the
/// auth-off no-regression contract. The full register→passwordless-sign-in ceremony
/// (which exercises the real signature + sign-count clone detection) is the
/// CDP-virtual-authenticator E2E (scripts/passkey-e2e.mjs) — a WAF can't mint a real
/// authenticator signature.
/// </summary>
[Trait("Category", "Node")]
public sealed class WebAuthnApiTests : IDisposable
{
    private readonly string dir;
    private readonly string configPath;
    private readonly string dbPath;

    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    public WebAuthnApiTests()
    {
        dir = Path.Combine(Path.GetTempPath(), "packetnet-waapi-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        configPath = Path.Combine(dir, "node.yaml");
        dbPath = Path.Combine(dir, "pdn.db");
    }

    private static string ConfigYaml(bool authEnabled) => $"""
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
            enabled: {(authEnabled ? "true" : "false")}
            webAuthn:
              relyingPartyId: localhost
              relyingPartyName: pdn test node
        """;

    private void WriteConfig(bool authEnabled) => File.WriteAllText(configPath, ConfigYaml(authEnabled));

    // Config now lives in pdn.db (config-in-DB, #473): flipping auth on between boots is a
    // write through the live seam (PUT /config/raw, ungated while auth is off), not a YAML
    // rewrite (the file is read once on first boot, then vestigial). Persists to the DB so the
    // next boot loads it.
    private static async Task FlipAuthOnViaApi(HttpClient client) =>
        (await client.PutAsync("/api/v1/config/raw",
            new StringContent(ConfigYaml(authEnabled: true)))).StatusCode.Should().Be(HttpStatusCode.OK);

    private sealed class NodeAppFactory(string configPath, string dbPath) : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(Microsoft.AspNetCore.Hosting.IWebHostBuilder builder)
        {
            Environment.SetEnvironmentVariable("PACKETNET_CONFIG", configPath);
            Environment.SetEnvironmentVariable("PACKETNET_DB", dbPath);
        }
    }

    private NodeAppFactory Factory() => new(configPath, dbPath);

    // --- assert/begin returns well-formed, server-issued options --------------

    [Fact]
    public async Task Assert_begin_returns_a_session_id_and_options_with_a_challenge()
    {
        WriteConfig(authEnabled: false);
        await using var factory = Factory();
        using var client = factory.CreateClient();

        var resp = await client.PostAsJsonAsync("/api/v1/auth/webauthn/assert/begin", new { }, Web);
        resp.StatusCode.Should().Be(HttpStatusCode.OK);

        var body = await resp.Content.ReadFromJsonAsync<JsonElement>(Web);
        // A per-attempt session id (echoed back at complete) ...
        body.GetProperty("sessionId").GetString().Should().NotBeNullOrEmpty();
        // ... and the assertion options carrying a server-generated challenge + the RP id.
        var options = body.GetProperty("options");
        options.GetProperty("challenge").GetString().Should().NotBeNullOrEmpty();
        options.GetProperty("rpId").GetString().Should().Be("localhost");
    }

    [Fact]
    public async Task Two_assert_begins_issue_distinct_challenges_and_sessions()
    {
        WriteConfig(authEnabled: false);
        await using var factory = Factory();
        using var client = factory.CreateClient();

        var a = await (await client.PostAsJsonAsync("/api/v1/auth/webauthn/assert/begin", new { }, Web))
            .Content.ReadFromJsonAsync<JsonElement>(Web);
        var b = await (await client.PostAsJsonAsync("/api/v1/auth/webauthn/assert/begin", new { }, Web))
            .Content.ReadFromJsonAsync<JsonElement>(Web);

        a.GetProperty("sessionId").GetString().Should().NotBe(b.GetProperty("sessionId").GetString());
        a.GetProperty("options").GetProperty("challenge").GetString()
            .Should().NotBe(b.GetProperty("options").GetProperty("challenge").GetString());
    }

    // --- assert/complete rejects bad input (single-use / no-oracle) -----------

    [Fact]
    public async Task Assert_complete_rejects_an_unknown_session_with_a_generic_401()
    {
        WriteConfig(authEnabled: false);
        await using var factory = Factory();
        using var client = factory.CreateClient();

        // No begin was issued for this session → the challenge cache has nothing → 401.
        var resp = await client.PostAsJsonAsync("/api/v1/auth/webauthn/assert/complete",
            new { sessionId = "never-issued", response = new { } }, Web);
        resp.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Assert_complete_is_single_use_a_consumed_session_cannot_be_reused()
    {
        WriteConfig(authEnabled: false);
        await using var factory = Factory();
        using var client = factory.CreateClient();

        var begin = await (await client.PostAsJsonAsync("/api/v1/auth/webauthn/assert/begin", new { }, Web))
            .Content.ReadFromJsonAsync<JsonElement>(Web);
        var sessionId = begin.GetProperty("sessionId").GetString();

        // First complete (with a malformed/empty response) consumes the pending challenge → 401.
        (await client.PostAsJsonAsync("/api/v1/auth/webauthn/assert/complete",
            new { sessionId, response = new { } }, Web)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);

        // A SECOND complete for the same session now finds no pending challenge (consumed) → 401.
        (await client.PostAsJsonAsync("/api/v1/auth/webauthn/assert/complete",
            new { sessionId, response = new { } }, Web)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    // --- registration is gated to the signed-in user -------------------------

    [Fact]
    public async Task Register_begin_requires_auth_when_enabled_then_works_for_the_logged_in_user()
    {
        // Bootstrap an admin with auth off, then flip auth on via the live write seam (it
        // persists to pdn.db; config-in-DB makes the YAML rewrite inert between boots).
        WriteConfig(authEnabled: false);
        await using (var setupFactory = Factory())
        using (var setupClient = setupFactory.CreateClient())
        {
            (await setupClient.PostAsJsonAsync("/api/v1/setup", new
            {
                identity = new { callsign = "M0LTE-1", alias = "LONDON" },
                admin = new { username = "sysop", password = "hunter2hunter2" },
            }, Web)).StatusCode.Should().Be(HttpStatusCode.OK);
            await FlipAuthOnViaApi(setupClient);
        }

        await using var factory = Factory();
        using var client = factory.CreateClient();

        // No token → the gate is 401 (the register group is read-gated when auth is on).
        (await client.PostAsJsonAsync("/api/v1/auth/webauthn/register/begin", new { }, Web))
            .StatusCode.Should().Be(HttpStatusCode.Unauthorized);

        // With a token → reaches the handler and returns well-formed creation options
        // whose user.name is the AUTHENTICATED principal (never a body value).
        var token = await Login(client, "sysop", "hunter2hunter2");
        using var req = new HttpRequestMessage(HttpMethod.Post, "/api/v1/auth/webauthn/register/begin");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var resp = await client.SendAsync(req);
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var options = await resp.Content.ReadFromJsonAsync<JsonElement>(Web);
        options.GetProperty("challenge").GetString().Should().NotBeNullOrEmpty();
        options.GetProperty("rp").GetProperty("id").GetString().Should().Be("localhost");
        options.GetProperty("user").GetProperty("name").GetString().Should().Be("sysop");
    }

    [Fact]
    public async Task Register_begin_409s_when_auth_is_off_no_self_to_enrol_for()
    {
        // Auth off ⇒ no authenticated principal ⇒ no "self" to bind a passkey to. The
        // endpoint is mapped (not 404) but returns 409, proving the default-off contract:
        // a node that never turns auth on is never usable for passkeys.
        WriteConfig(authEnabled: false);
        await using var factory = Factory();
        using var client = factory.CreateClient();

        (await client.PostAsJsonAsync("/api/v1/auth/webauthn/register/begin", new { }, Web))
            .StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    // --- credentials list/delete ----------------------------------------------

    [Fact]
    public async Task Credentials_list_is_empty_for_a_user_with_no_passkeys()
    {
        WriteConfig(authEnabled: false);
        await using (var setupFactory = Factory())
        using (var setupClient = setupFactory.CreateClient())
        {
            (await setupClient.PostAsJsonAsync("/api/v1/setup", new
            {
                identity = new { callsign = "M0LTE-1" },
                admin = new { username = "sysop", password = "hunter2hunter2" },
            }, Web)).StatusCode.Should().Be(HttpStatusCode.OK);
            await FlipAuthOnViaApi(setupClient);
        }

        await using var factory = Factory();
        using var client = factory.CreateClient();
        var token = await Login(client, "sysop", "hunter2hunter2");

        using var req = new HttpRequestMessage(HttpMethod.Get, "/api/v1/auth/webauthn/credentials");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var resp = await client.SendAsync(req);
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        (await resp.Content.ReadFromJsonAsync<JsonElement>(Web)).GetArrayLength().Should().Be(0);
    }

    // --- no regression: endpoints exist + node behaves as before with auth off

    [Fact]
    public async Task With_auth_off_the_webauthn_endpoints_are_mapped_and_the_node_is_unchanged()
    {
        WriteConfig(authEnabled: false);
        await using var factory = Factory();
        using var client = factory.CreateClient();

        // assert/begin is reachable (200, not 404) even with auth off — it's always open.
        (await client.PostAsJsonAsync("/api/v1/auth/webauthn/assert/begin", new { }, Web))
            .StatusCode.Should().Be(HttpStatusCode.OK);

        // And the rest of the node still serves tokenless exactly as before.
        (await client.GetAsync("/api/v1/status")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await client.GetAsync("/healthz")).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    private static async Task<string> Login(HttpClient client, string username, string password)
    {
        var resp = await client.PostAsJsonAsync("/api/v1/auth/login", new { username, password }, Web);
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>(Web);
        return body.GetProperty("token").GetString()!;
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("PACKETNET_CONFIG", null);
        Environment.SetEnvironmentVariable("PACKETNET_DB", null);
        try { Directory.Delete(dir, recursive: true); } catch { /* best effort */ }
    }
}
