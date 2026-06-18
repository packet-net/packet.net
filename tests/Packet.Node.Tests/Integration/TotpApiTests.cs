using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using Packet.Node.Core.Auth;

namespace Packet.Node.Tests.Integration;

/// <summary>
/// Boots the real <c>Packet.Node</c> composition root and exercises the over-RF sysop-code
/// (TOTP) ENROLMENT endpoint plumbing: enroll/begin returns a server-minted secret + an
/// otpauth URI; enroll/complete with the CORRECT generated code persists + GET reflects the
/// enrolled state; a WRONG code 400s and does NOT persist; the gated endpoints 401 without a
/// token; and the auth-off no-regression contract. The host's <see cref="TimeProvider"/> is
/// replaced with a <see cref="FakeTimeProvider"/> so the confirming code is deterministic
/// (computed with <see cref="TotpService.ComputeCode"/> at the same instant).
/// </summary>
[Trait("Category", "Node")]
public sealed class TotpApiTests : IDisposable
{
    private readonly string dir;
    private readonly string configPath;
    private readonly string dbPath;

    // A fixed instant the host's clock is pinned to, so the TOTP code the test computes
    // matches what the endpoint computes when verifying.
    private static readonly DateTimeOffset Now = new(2026, 6, 9, 12, 0, 0, TimeSpan.Zero);

    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    public TotpApiTests()
    {
        dir = Path.Combine(Path.GetTempPath(), "packetnet-totpapi-" + Guid.NewGuid().ToString("N"));
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
        """;

    private void WriteConfig(bool authEnabled) => File.WriteAllText(configPath, ConfigYaml(authEnabled));

    private sealed class NodeAppFactory(string configPath, string dbPath) : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(Microsoft.AspNetCore.Hosting.IWebHostBuilder builder)
        {
            Environment.SetEnvironmentVariable("PACKETNET_CONFIG", configPath);
            Environment.SetEnvironmentVariable("PACKETNET_DB", dbPath);

            // Pin the clock the TOTP service rides so the confirming code is deterministic.
            // The TOTP enrolment cache is reconstructed over the fake clock too (its TTL also
            // rides the injected clock); the JWT token service keeps real-time validation
            // (it's built directly in Program.cs), which is fine — a freshly-issued token is
            // well inside its lifetime for the duration of the test.
            builder.ConfigureTestServices(services =>
            {
                var fake = new FakeTimeProvider(Now);
                services.AddSingleton<TimeProvider>(fake);
                services.AddSingleton(new TotpEnrollmentCache(fake));
            });
        }
    }

    private NodeAppFactory Factory() => new(configPath, dbPath);

    // The current 6-digit code for a base32 secret at the pinned instant — exactly what the
    // endpoint computes when it verifies an enroll/complete.
    private static string CodeFor(string secret) =>
        TotpService.ComputeCode(secret, TotpService.CounterAt(Now));

    // --- enrol begin → complete (happy path) → GET reflects it ----------------

    [Fact]
    public async Task Enroll_begin_returns_a_secret_and_otpauth_uri()
    {
        await BootstrapAdmin();
        WriteConfig(authEnabled: true);
        await using var factory = Factory();
        using var client = factory.CreateClient();
        var token = await Login(client, "sysop", "hunter2hunter2");

        var begin = await BeginEnroll(client, token);
        var secret = begin.GetProperty("secret").GetString();
        secret.Should().NotBeNullOrEmpty();
        var uri = begin.GetProperty("otpauthUri").GetString();
        uri.Should().StartWith("otpauth://totp/");
        // The issuer is the node's own callsign, and the secret is carried in the URI.
        uri.Should().Contain("issuer=M0LTE-1");
        uri.Should().Contain($"secret={secret}");
    }

    [Fact]
    public async Task Enroll_complete_with_the_correct_code_persists_and_GET_reflects_enrolled()
    {
        await BootstrapAdmin();
        WriteConfig(authEnabled: true);
        await using var factory = Factory();
        using var client = factory.CreateClient();
        var token = await Login(client, "sysop", "hunter2hunter2");

        var begin = await BeginEnroll(client, token);
        var secret = begin.GetProperty("secret").GetString()!;

        // Complete with the correct code computed at the pinned instant.
        var complete = await Send(client, token, HttpMethod.Post, "/api/v1/auth/totp/enroll/complete",
            new { code = CodeFor(secret), callsign = "G7XYZ" });
        complete.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await complete.Content.ReadFromJsonAsync<JsonElement>(Web);
        body.GetProperty("enrolled").GetBoolean().Should().BeTrue();
        body.GetProperty("callsign").GetString().Should().Be("G7XYZ");

        // GET reflects the enrolled state (callsign visible, never the secret).
        var get = await Send(client, token, HttpMethod.Get, "/api/v1/auth/totp/enroll", null);
        get.StatusCode.Should().Be(HttpStatusCode.OK);
        var state = await get.Content.ReadFromJsonAsync<JsonElement>(Web);
        state.GetProperty("enrolled").GetBoolean().Should().BeTrue();
        state.GetProperty("callsign").GetString().Should().Be("G7XYZ");
        state.TryGetProperty("secret", out _).Should().BeFalse();
    }

    [Fact]
    public async Task Enroll_complete_with_a_wrong_code_400s_and_does_not_persist()
    {
        await BootstrapAdmin();
        WriteConfig(authEnabled: true);
        await using var factory = Factory();
        using var client = factory.CreateClient();
        var token = await Login(client, "sysop", "hunter2hunter2");

        await BeginEnroll(client, token);

        // A wrong code is rejected with a generic 400 ...
        var complete = await Send(client, token, HttpMethod.Post, "/api/v1/auth/totp/enroll/complete",
            new { code = "000000", callsign = "G7XYZ" });
        complete.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        // ... and nothing was persisted (still not enrolled).
        var get = await Send(client, token, HttpMethod.Get, "/api/v1/auth/totp/enroll", null);
        (await get.Content.ReadFromJsonAsync<JsonElement>(Web))
            .GetProperty("enrolled").GetBoolean().Should().BeFalse();
    }

    [Fact]
    public async Task Enroll_complete_is_single_use_a_consumed_pending_secret_cannot_be_reused()
    {
        await BootstrapAdmin();
        WriteConfig(authEnabled: true);
        await using var factory = Factory();
        using var client = factory.CreateClient();
        var token = await Login(client, "sysop", "hunter2hunter2");

        await BeginEnroll(client, token);

        // First complete (wrong code) consumes the pending secret → 400.
        (await Send(client, token, HttpMethod.Post, "/api/v1/auth/totp/enroll/complete",
            new { code = "000000", callsign = "G7XYZ" })).StatusCode.Should().Be(HttpStatusCode.BadRequest);

        // A SECOND complete now finds no pending enrolment (consumed) → 400 "no pending".
        (await Send(client, token, HttpMethod.Post, "/api/v1/auth/totp/enroll/complete",
            new { code = "000000", callsign = "G7XYZ" })).StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Delete_enroll_clears_the_credential()
    {
        await BootstrapAdmin();
        WriteConfig(authEnabled: true);
        await using var factory = Factory();
        using var client = factory.CreateClient();
        var token = await Login(client, "sysop", "hunter2hunter2");

        var begin = await BeginEnroll(client, token);
        var secret = begin.GetProperty("secret").GetString()!;
        (await Send(client, token, HttpMethod.Post, "/api/v1/auth/totp/enroll/complete",
            new { code = CodeFor(secret), callsign = "G7XYZ" })).StatusCode.Should().Be(HttpStatusCode.OK);

        // DELETE clears it (204), and GET then reports not-enrolled.
        (await Send(client, token, HttpMethod.Delete, "/api/v1/auth/totp/enroll", null))
            .StatusCode.Should().Be(HttpStatusCode.NoContent);
        var get = await Send(client, token, HttpMethod.Get, "/api/v1/auth/totp/enroll", null);
        (await get.Content.ReadFromJsonAsync<JsonElement>(Web))
            .GetProperty("enrolled").GetBoolean().Should().BeFalse();
    }

    // --- gating + no regression ------------------------------------------------

    [Fact]
    public async Task Gated_endpoints_401_without_a_token_when_auth_is_on()
    {
        await BootstrapAdmin();
        WriteConfig(authEnabled: true);
        await using var factory = Factory();
        using var client = factory.CreateClient();

        (await client.PostAsJsonAsync("/api/v1/auth/totp/enroll/begin", new { }, Web))
            .StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await client.GetAsync("/api/v1/auth/totp/enroll"))
            .StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task With_auth_off_the_totp_endpoints_are_mapped_and_the_node_is_unchanged()
    {
        WriteConfig(authEnabled: false);
        await using var factory = Factory();
        using var client = factory.CreateClient();

        // Mapped (not 404) but, with no authenticated "self", begin 409s — the default-off
        // contract: a node that never turns auth on is never usable for TOTP enrolment.
        (await client.PostAsJsonAsync("/api/v1/auth/totp/enroll/begin", new { }, Web))
            .StatusCode.Should().Be(HttpStatusCode.Conflict);
        // GET is reachable and reports not-enrolled (no self) — never 404.
        var get = await client.GetAsync("/api/v1/auth/totp/enroll");
        get.StatusCode.Should().Be(HttpStatusCode.OK);
        (await get.Content.ReadFromJsonAsync<JsonElement>(Web))
            .GetProperty("enrolled").GetBoolean().Should().BeFalse();

        // And the rest of the node still serves tokenless exactly as before.
        (await client.GetAsync("/api/v1/status")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await client.GetAsync("/healthz")).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    // --- helpers ---------------------------------------------------------------

    private async Task BootstrapAdmin()
    {
        // Bootstrap an admin with auth off, then persist auth-ON through the live write seam
        // (config-in-DB, #473: the DB row, not a YAML rewrite, carries config between boots —
        // PUT /config/raw is ungated while auth is still off). The caller's reboot loads it.
        WriteConfig(authEnabled: false);
        await using var setupFactory = Factory();
        using var setupClient = setupFactory.CreateClient();
        (await setupClient.PostAsJsonAsync("/api/v1/setup", new
        {
            identity = new { callsign = "M0LTE-1", alias = "LONDON" },
            admin = new { username = "sysop", password = "hunter2hunter2" },
        }, Web)).StatusCode.Should().Be(HttpStatusCode.OK);
        (await setupClient.PutAsync("/api/v1/config/raw",
            new StringContent(ConfigYaml(authEnabled: true)))).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    private static async Task<JsonElement> BeginEnroll(HttpClient client, string token)
    {
        var resp = await Send(client, token, HttpMethod.Post, "/api/v1/auth/totp/enroll/begin", new { });
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        return await resp.Content.ReadFromJsonAsync<JsonElement>(Web);
    }

    private static async Task<HttpResponseMessage> Send(
        HttpClient client, string token, HttpMethod method, string path, object? body)
    {
        using var req = new HttpRequestMessage(method, path);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (body is not null)
        {
            req.Content = JsonContent.Create(body, options: Web);
        }
        return await client.SendAsync(req);
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
