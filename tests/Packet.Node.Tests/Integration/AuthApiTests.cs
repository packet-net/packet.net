using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Packet.Node.Tests.Integration;

/// <summary>
/// Boots the real <c>Packet.Node</c> composition root and exercises the auth
/// foundation: login, the setup probe + bootstrap, and (in a second WAF with the
/// flag on) the scope gates. Mirrors <see cref="ReadApiTests"/>/<see cref="ConfigWriteApiTests"/> —
/// a temp YAML config with telnet disabled (no fixed TCP port under the WAF) and
/// the db pointed at the same temp dir.
/// </summary>
/// <remarks>
/// Each test owns its own temp dir + config + db (no env-var bleed across the
/// parallel classes) and selects auth on/off through the config file.
/// </remarks>
[Trait("Category", "Node")]
public sealed class AuthApiTests : IDisposable
{
    private readonly string dir;
    private readonly string configPath;
    private readonly string dbPath;

    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    public AuthApiTests()
    {
        dir = Path.Combine(Path.GetTempPath(), "packetnet-authapi-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        configPath = Path.Combine(dir, "node.yaml");
        dbPath = Path.Combine(dir, "pdn.db");
    }

    private static string ConfigYaml(bool authEnabled) => $"""
        schemaVersion: 1
        identity:
          callsign: M0LTE-1
          alias: LONDON
        ports:
          - id: vhf
            enabled: false
            transport:
              kind: kiss-tcp
              host: 127.0.0.1
              port: 8101
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

    // Config now lives in pdn.db (config-in-DB, #473), so flipping auth on between boots is
    // no longer a YAML-file rewrite (the file is read once on first boot, then vestigial). To
    // turn auth ON before a re-boot, persist the auth-on config through the live write seam
    // (PUT /config/raw is ungated while auth is still off) — the DB then carries it and the
    // next boot loads it. Replaces the old "rewrite the file + reboot" idiom.
    private static async Task FlipAuthOnViaApi(HttpClient client)
    {
        var resp = await client.PutAsync("/api/v1/config/raw",
            new StringContent(ConfigYaml(authEnabled: true)));
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    // A factory bound to THIS test's temp config/db via per-factory env vars set
    // inside ConfigureWebHost (so two factories in one process don't clash).
    private sealed class NodeAppFactory(string configPath, string dbPath) : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(Microsoft.AspNetCore.Hosting.IWebHostBuilder builder)
        {
            Environment.SetEnvironmentVariable("PACKETNET_CONFIG", configPath);
            Environment.SetEnvironmentVariable("PACKETNET_DB", dbPath);
        }
    }

    private NodeAppFactory Factory() => new(configPath, dbPath);

    // --- Setup flow ----------------------------------------------------------

    [Fact]
    public async Task Setup_state_is_needed_on_an_empty_store_then_not_after_setup()
    {
        WriteConfig(authEnabled: false);
        await using var factory = Factory();
        using var client = factory.CreateClient();

        var before = await client.GetFromJsonAsync<JsonElement>("/api/v1/setup/state", Web);
        before.GetProperty("needsSetup").GetBoolean().Should().BeTrue();

        var setup = await client.PostAsJsonAsync("/api/v1/setup", new
        {
            identity = new { callsign = "G0ABC-1", alias = "TESTND" },
            admin = new { username = "sysop", password = "hunter2hunter2" },
        }, Web);
        setup.StatusCode.Should().Be(HttpStatusCode.OK);

        var after = await client.GetFromJsonAsync<JsonElement>("/api/v1/setup/state", Web);
        after.GetProperty("needsSetup").GetBoolean().Should().BeFalse();

        // The identity was applied through the config write seam.
        var cfg = await client.GetFromJsonAsync<JsonElement>("/api/v1/config", Web);
        cfg.GetProperty("identity").GetProperty("callsign").GetString().Should().Be("G0ABC-1");

        // A second setup is rejected (one-shot).
        var second = await client.PostAsJsonAsync("/api/v1/setup", new
        {
            identity = new { callsign = "G9ZZZ-9" },
            admin = new { username = "other", password = "password1234" },
        }, Web);
        second.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Setup_rejects_a_short_password()
    {
        WriteConfig(authEnabled: false);
        await using var factory = Factory();
        using var client = factory.CreateClient();

        var resp = await client.PostAsJsonAsync("/api/v1/setup", new
        {
            identity = new { callsign = "G0ABC-1" },
            admin = new { username = "sysop", password = "short" },
        }, Web);
        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    // --- Degraded boot (pdn.db unwritable) ------------------------------------

    [Fact]
    public async Task Node_boots_and_degrades_to_503_when_the_db_is_unwritable()
    {
        // pdn.db under a directory that does not exist → SQLite cannot open it, so
        // BOTH the routing store and the user store degrade (warn + disable). The
        // host must still BOOT and serve — not abort at startup. Regression guard:
        // with no signing key the token service is unregistered, and the login
        // handler's [FromServices] JwtTokenService? must resolve to null (→ 503)
        // rather than failing minimal-API parameter inference and crashing the host.
        WriteConfig(authEnabled: false);
        var unopenableDb = Path.Combine(dir, "no-such-dir", "pdn.db");
        await using var factory = new NodeAppFactory(configPath, unopenableDb);
        using var client = factory.CreateClient(); // throws here if the host aborted at startup

        // Booted + serving: liveness and the (auth-off) read API both answer.
        (await client.GetAsync("/healthz")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await client.GetAsync("/api/v1/status")).StatusCode.Should().Be(HttpStatusCode.OK);

        // Auth can't initialise (no key) → login degrades to 503, not a 500/crash.
        var login = await client.PostAsJsonAsync(
            "/api/v1/auth/login", new { username = "x", password = "yyyyyyyy" }, Web);
        login.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
    }

    // --- Login ----------------------------------------------------------------

    [Fact]
    public async Task Login_succeeds_with_a_token_for_good_creds_and_fails_generically_otherwise()
    {
        WriteConfig(authEnabled: false);
        await using var factory = Factory();
        using var client = factory.CreateClient();

        await BootstrapAdmin(client, "sysop", "hunter2hunter2");

        // Good creds → 200 + token + scopes.
        var ok = await client.PostAsJsonAsync("/api/v1/auth/login",
            new { username = "sysop", password = "hunter2hunter2" }, Web);
        ok.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await ok.Content.ReadFromJsonAsync<JsonElement>(Web);
        body.GetProperty("token").GetString().Should().NotBeNullOrEmpty();
        body.GetProperty("scopes").GetString().Should().Be("admin");
        // The response carries the authenticated username so the client need not derive it
        // (a passwordless passkey sign-in has no typed username to fall back on).
        body.GetProperty("username").GetString().Should().Be("sysop");

        // Bad password → 401, generic message.
        var badPw = await client.PostAsJsonAsync("/api/v1/auth/login",
            new { username = "sysop", password = "wrong-password" }, Web);
        badPw.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        var badPwBody = await badPw.Content.ReadAsStringAsync();

        // Unknown user → 401, the SAME generic message (no user-existence oracle).
        var unknown = await client.PostAsJsonAsync("/api/v1/auth/login",
            new { username = "ghost", password = "wrong-password" }, Web);
        unknown.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        var unknownBody = await unknown.Content.ReadAsStringAsync();

        unknownBody.Should().Be(badPwBody);
    }

    // --- Refresh-token rotation + reuse detection ----------------------------

    [Fact]
    public async Task Login_returns_a_refresh_token_that_rotates_and_tolerates_a_concurrent_replay()
    {
        WriteConfig(authEnabled: false);
        await using var factory = Factory();
        using var client = factory.CreateClient();

        await BootstrapAdmin(client, "sysop", "hunter2hunter2");

        // Login returns both an access token AND an opaque refresh token.
        var login = await client.PostAsJsonAsync("/api/v1/auth/login",
            new { username = "sysop", password = "hunter2hunter2" }, Web);
        login.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await login.Content.ReadFromJsonAsync<JsonElement>(Web);
        var refreshToken = body.GetProperty("refreshToken").GetString();
        refreshToken.Should().NotBeNullOrEmpty();

        // Refresh rotates: a new access token + a new (different) refresh token.
        var refresh = await client.PostAsJsonAsync("/api/v1/auth/refresh",
            new { refreshToken }, Web);
        refresh.StatusCode.Should().Be(HttpStatusCode.OK);
        var refreshBody = await refresh.Content.ReadFromJsonAsync<JsonElement>(Web);
        refreshBody.GetProperty("token").GetString().Should().NotBeNullOrEmpty();
        var rotated = refreshBody.GetProperty("refreshToken").GetString();
        rotated.Should().NotBeNullOrEmpty();
        rotated.Should().NotBe(refreshToken);
        refreshBody.GetProperty("scopes").GetString().Should().Be("admin");
        refreshBody.GetProperty("username").GetString().Should().Be("sysop");

        // A near-immediate replay of the just-consumed token is the legitimate client
        // racing itself (two tabs / a retried silent refresh). Within the reuse-leeway
        // window it is TOLERATED — it rotates again rather than burning the family and
        // logging the user out. (This was the "logged out every ~hour" REUSE-DETECTED
        // bug; the strict-after-leeway theft response is unit-tested in
        // RefreshTokenServiceTests on a controllable clock.)
        var replay = await client.PostAsJsonAsync("/api/v1/auth/refresh",
            new { refreshToken }, Web);
        replay.StatusCode.Should().Be(HttpStatusCode.OK);

        // The earlier successor was NOT burned by that replay — the session survives and
        // it still rotates.
        var successorStillWorks = await client.PostAsJsonAsync("/api/v1/auth/refresh",
            new { refreshToken = rotated }, Web);
        successorStillWorks.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task An_invalid_or_blank_refresh_token_is_401()
    {
        WriteConfig(authEnabled: false);
        await using var factory = Factory();
        using var client = factory.CreateClient();

        await BootstrapAdmin(client, "sysop", "hunter2hunter2");

        (await client.PostAsJsonAsync("/api/v1/auth/refresh", new { refreshToken = "garbage" }, Web))
            .StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await client.PostAsJsonAsync("/api/v1/auth/refresh", new { refreshToken = "" }, Web))
            .StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Logout_revokes_the_token_family()
    {
        WriteConfig(authEnabled: false);
        await using var factory = Factory();
        using var client = factory.CreateClient();

        await BootstrapAdmin(client, "sysop", "hunter2hunter2");
        var login = await client.PostAsJsonAsync("/api/v1/auth/login",
            new { username = "sysop", password = "hunter2hunter2" }, Web);
        var refreshToken = (await login.Content.ReadFromJsonAsync<JsonElement>(Web))
            .GetProperty("refreshToken").GetString();

        // Logout is a 204 and revokes the family.
        var logout = await client.PostAsJsonAsync("/api/v1/auth/logout", new { refreshToken }, Web);
        logout.StatusCode.Should().Be(HttpStatusCode.NoContent);

        // The token no longer rotates.
        (await client.PostAsJsonAsync("/api/v1/auth/refresh", new { refreshToken }, Web))
            .StatusCode.Should().Be(HttpStatusCode.Unauthorized);

        // Logout is idempotent / safe on an unknown token (still 204, nothing leaked).
        (await client.PostAsJsonAsync("/api/v1/auth/logout", new { refreshToken = "nope" }, Web))
            .StatusCode.Should().Be(HttpStatusCode.NoContent);
    }

    // --- Login hardening: lockout --------------------------------------------

    [Fact]
    public async Task Repeated_bad_logins_lock_out_with_429()
    {
        WriteConfig(authEnabled: false);
        await using var factory = Factory();
        using var client = factory.CreateClient();

        await BootstrapAdmin(client, "sysop", "hunter2hunter2");

        // The default threshold is 5 failures within the window. Five bad attempts...
        for (int i = 0; i < 5; i++)
        {
            var fail = await client.PostAsJsonAsync("/api/v1/auth/login",
                new { username = "sysop", password = "definitely-wrong" }, Web);
            fail.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        }

        // ...the next attempt is refused with 429 — even with the CORRECT password,
        // because the username (and source IP) are now locked out.
        var lockedOut = await client.PostAsJsonAsync("/api/v1/auth/login",
            new { username = "sysop", password = "hunter2hunter2" }, Web);
        lockedOut.StatusCode.Should().Be(HttpStatusCode.TooManyRequests);
    }

    // --- No regression: refresh/logout exist + gated APIs serve tokenless ----

    [Fact]
    public async Task With_auth_off_refresh_and_logout_endpoints_exist_and_gated_apis_serve_tokenless()
    {
        WriteConfig(authEnabled: false);
        await using var factory = Factory();
        using var client = factory.CreateClient();

        await BootstrapAdmin(client, "sysop", "hunter2hunter2");

        // The new endpoints are reachable (a bad refresh is a 401, NOT a 404 — proving
        // they're mapped even with auth off).
        (await client.PostAsJsonAsync("/api/v1/auth/refresh", new { refreshToken = "x" }, Web))
            .StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await client.PostAsJsonAsync("/api/v1/auth/logout", new { refreshToken = "x" }, Web))
            .StatusCode.Should().Be(HttpStatusCode.NoContent);

        // And the gated read API still serves tokenless (the no-regression contract).
        (await client.GetAsync("/api/v1/status")).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    // --- No regression: auth disabled (DEFAULT) ------------------------------

    [Fact]
    public async Task With_auth_disabled_read_and_write_endpoints_work_without_a_token()
    {
        WriteConfig(authEnabled: false);
        await using var factory = Factory();
        using var client = factory.CreateClient();

        // Read endpoint — no token, 200 (exactly as before auth existed).
        (await client.GetAsync("/api/v1/status")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await client.GetAsync("/api/v1/ports")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await client.GetAsync("/api/v1/config")).StatusCode.Should().Be(HttpStatusCode.OK);

        // Write endpoint — no token, the request REACHES the handler (proof the gate is
        // inert, not just that GETs are open). POST /ports with a blank id hits the
        // validator → 422 (NOT 401/403).
        var resp = await client.PostAsJsonAsync("/api/v1/ports",
            new { id = "", enabled = true, transport = new { kind = "kiss-tcp", host = "127.0.0.1", port = 8102 } }, Web);
        resp.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);

        // The admin-gated /users is also open when auth is off.
        (await client.GetAsync("/api/v1/users")).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    // --- Enforced: auth enabled ----------------------------------------------

    [Fact]
    public async Task With_auth_enabled_a_gated_endpoint_is_401_then_403_then_200_by_scope()
    {
        // Bootstrap a read-scope user + an operate-scope user while auth is OFF (so the
        // bootstrap calls themselves aren't gated), then flip auth ON and re-create the
        // client so the gate enforces.
        WriteConfig(authEnabled: false);
        string readToken, operateToken;
        await using (var setupFactory = Factory())
        using (var setupClient = setupFactory.CreateClient())
        {
            await BootstrapAdmin(setupClient, "admin", "adminpassword");
            var adminToken = await Login(setupClient, "admin", "adminpassword");

            // Create a read user + an operate user via the (open) /users endpoint.
            await CreateUser(setupClient, adminToken, "reader", "readerpassword", "read");
            await CreateUser(setupClient, adminToken, "operator", "operatorpassword", "operate");

            readToken = await Login(setupClient, "reader", "readerpassword");
            operateToken = await Login(setupClient, "operator", "operatorpassword");

            // Turn auth ON via the live write seam (persists to pdn.db) while still ungated.
            await FlipAuthOnViaApi(setupClient);
        }

        // Now boot a fresh host over the SAME db (users + key + auth-on config persist).
        await using var factory = Factory();
        using var client = factory.CreateClient();

        // An OPERATE endpoint (PUT /config) ...
        var candidate = new
        {
            schemaVersion = 1,
            identity = new { callsign = "M0LTE-1", alias = "LONDON" },
            ports = Array.Empty<object>(),
            management = new { telnet = new { enabled = false }, http = new { bind = "127.0.0.1", port = 8080 }, auth = new { enabled = true } },
        };

        // No token → 401.
        var noToken = await client.PutAsJsonAsync("/api/v1/config", candidate, Web);
        noToken.StatusCode.Should().Be(HttpStatusCode.Unauthorized);

        // Read token on an operate endpoint → 403 (authenticated, insufficient scope).
        var withRead = await PutWithToken(client, readToken, "/api/v1/config", candidate);
        withRead.StatusCode.Should().Be(HttpStatusCode.Forbidden);

        // Operate token → reaches the handler. (Either 200 applied or 422 validation,
        // but NOT 401/403 — the gate let it through.)
        var withOperate = await PutWithToken(client, operateToken, "/api/v1/config", candidate);
        withOperate.StatusCode.Should().NotBe(HttpStatusCode.Unauthorized);
        withOperate.StatusCode.Should().NotBe(HttpStatusCode.Forbidden);

        // A READ endpoint with the read token → 200 (read satisfies read).
        var statusWithRead = await GetWithToken(client, readToken, "/api/v1/status");
        statusWithRead.StatusCode.Should().Be(HttpStatusCode.OK);

        // A read endpoint with NO token → 401 when auth is on.
        (await client.GetAsync("/api/v1/status")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);

        // The admin-only /users with an operate token → 403.
        var usersWithOperate = await GetWithToken(client, operateToken, "/api/v1/users");
        usersWithOperate.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task With_auth_enabled_the_login_and_setup_state_endpoints_stay_open()
    {
        // Bootstrap with auth off, then flip on via the live write seam (persists to pdn.db).
        WriteConfig(authEnabled: false);
        await using (var setupFactory = Factory())
        using (var setupClient = setupFactory.CreateClient())
        {
            await BootstrapAdmin(setupClient, "admin", "adminpassword");
            await FlipAuthOnViaApi(setupClient);
        }

        await using var factory = Factory();
        using var client = factory.CreateClient();

        // /setup/state and /auth/login + /healthz are reachable without a token.
        (await client.GetAsync("/api/v1/setup/state")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await client.GetAsync("/healthz")).StatusCode.Should().Be(HttpStatusCode.OK);
        var login = await client.PostAsJsonAsync("/api/v1/auth/login",
            new { username = "admin", password = "adminpassword" }, Web);
        login.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    // --- Helpers --------------------------------------------------------------

    private static async Task BootstrapAdmin(HttpClient client, string username, string password)
    {
        var resp = await client.PostAsJsonAsync("/api/v1/setup", new
        {
            identity = new { callsign = "M0LTE-1", alias = "LONDON" },
            admin = new { username, password },
        }, Web);
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    private static async Task<string> Login(HttpClient client, string username, string password)
    {
        var resp = await client.PostAsJsonAsync("/api/v1/auth/login", new { username, password }, Web);
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>(Web);
        return body.GetProperty("token").GetString()!;
    }

    private static async Task CreateUser(HttpClient client, string adminToken, string username, string password, string scope)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, "/api/v1/users")
        {
            Content = JsonContent.Create(new { username, password, scope }, options: Web),
        };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", adminToken);
        var resp = await client.SendAsync(req);
        resp.StatusCode.Should().Be(HttpStatusCode.Created);
    }

    private static async Task<HttpResponseMessage> PutWithToken(HttpClient client, string token, string url, object body)
    {
        using var req = new HttpRequestMessage(HttpMethod.Put, url)
        {
            Content = JsonContent.Create(body, options: Web),
        };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return await client.SendAsync(req);
    }

    private static async Task<HttpResponseMessage> GetWithToken(HttpClient client, string token, string url)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return await client.SendAsync(req);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("PACKETNET_CONFIG", null);
        Environment.SetEnvironmentVariable("PACKETNET_DB", null);
        try { Directory.Delete(dir, recursive: true); } catch { /* best effort */ }
    }
}
