using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Packet.Node.Tests.Integration;

/// <summary>
/// The app-gateway human-plane 401 recovery (<c>Program.cs</c> <c>OnChallenge</c> +
/// <see cref="Packet.Node.Api.AppGatewayChallenge"/>). With auth ON, an unauthenticated request
/// to a gated <c>/apps/{id}/*</c> path must:
/// <list type="bullet">
/// <item>a top-level browser navigation → <c>302</c> to the SPA login, carrying a same-site
/// <c>next</c> of the originally-requested path;</item>
/// <item>a sub-frame (iframe/frame) → a renderable <c>200 text/html</c> whose script breaks the
/// slot out to the top-level login (a frame can't 302 its parent);</item>
/// <item>an XHR / API fetch (Accept: application/json, no navigation hint) → the bare <c>401</c>
/// EXACTLY as before — the SPA's on401 handler owns those.</item>
/// </list>
/// The redirect never weakens auth — it still rejects the request and requires re-login; it only
/// swaps the unrenderable bare 401 for a renderable re-auth on human-plane navigations.
/// </summary>
[Trait("Category", "Node")]
public sealed class AppGatewayChallengeTests : IDisposable
{
    private readonly string dir;
    private readonly string configPath;
    private readonly string dbPath;

    public AppGatewayChallengeTests()
    {
        dir = Path.Combine(Path.GetTempPath(), "pdn-gw-chal-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        configPath = Path.Combine(dir, "node.yaml");
        dbPath = Path.Combine(dir, "pdn.db");
    }

    // The node config with one web app and auth on/off. The app's upstream is never reached in
    // these tests (auth rejects before the proxy runs), so a bogus loopback upstream is fine.
    private static string ConfigYaml(bool authEnabled) => $"""
        schemaVersion: 1
        identity:
          callsign: M0LTE-1
        ports: []
        management:
          telnet:
            enabled: false
          http:
            bind: 127.0.0.1
            port: 8080
          auth:
            enabled: {(authEnabled ? "true" : "false")}
        applications:
          - id: bbs
            command: BBS
            executable: /bin/cat
            ui:
              upstream: http://127.0.0.1:9
              name: BBS
              icon: mail
        """;

    private void WriteConfig(bool authEnabled) => File.WriteAllText(configPath, ConfigYaml(authEnabled));

    private sealed class NodeAppFactory(string configPath, string dbPath) : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(Microsoft.AspNetCore.Hosting.IWebHostBuilder builder)
        {
            Environment.SetEnvironmentVariable("PACKETNET_CONFIG", configPath);
            Environment.SetEnvironmentVariable("PACKETNET_DB", dbPath);
        }
    }

    // Bootstrap an admin (so the user store is non-empty + the signing key exists) with auth OFF,
    // then return a fresh auth-ON factory over the SAME db so the gate enforces. Config now lives
    // in pdn.db (config-in-DB, #473), so auth is flipped on through the live write seam
    // (PUT /config/raw is ungated while auth is off) — not a YAML rewrite, which the next boot
    // would ignore since the DB row already exists.
    private async Task<NodeAppFactory> AuthOnFactoryAsync()
    {
        WriteConfig(authEnabled: false);
        await using (var setup = new NodeAppFactory(configPath, dbPath))
        using (var client = setup.CreateClient())
        {
            var resp = await client.PostAsJsonAsync("/api/v1/setup", new
            {
                identity = new { callsign = "M0LTE-1" },
                admin = new { username = "sysop", password = "hunter2hunter2" },
            });
            resp.StatusCode.Should().Be(HttpStatusCode.OK);
            (await client.PutAsync("/api/v1/config/raw",
                new StringContent(ConfigYaml(authEnabled: true)))).StatusCode.Should().Be(HttpStatusCode.OK);
        }
        return new NodeAppFactory(configPath, dbPath);
    }

    // A client that does NOT auto-follow redirects, so we can assert the 302 + Location.
    private static HttpClient NoRedirectClient(WebApplicationFactory<Program> factory) =>
        factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

    [Fact]
    public async Task A_top_level_browser_navigation_to_a_gated_app_redirects_to_login_with_a_safe_next()
    {
        await using var factory = await AuthOnFactoryAsync();
        using var client = NoRedirectClient(factory);

        using var req = new HttpRequestMessage(HttpMethod.Get, "/apps/bbs/inbox?folder=1");
        // A real top-level browser navigation (Chrome/Safari send these on a document load).
        req.Headers.TryAddWithoutValidation("Sec-Fetch-Mode", "navigate");
        req.Headers.TryAddWithoutValidation("Sec-Fetch-Dest", "document");
        req.Headers.TryAddWithoutValidation("Accept", "text/html,application/xhtml+xml");
        var resp = await client.SendAsync(req);

        resp.StatusCode.Should().Be(HttpStatusCode.Redirect);   // 302
        var location = resp.Headers.Location!.ToString();
        // Same-site relative login URL, with the originally-requested path as an encoded next.
        location.Should().StartWith("/login?next=");
        location.Should().NotContain("//");                     // no protocol-relative open-redirect
        // next returns to the SPA app route (/apps/bbs), NOT the raw deep gateway path — so login
        // lands back in the panel with the slot, not on the headless app full-page.
        Uri.UnescapeDataString(location).Should().Be("/login?next=/apps/bbs");
        // It is NOT the bare 401 challenge.
        resp.Headers.Contains("WWW-Authenticate").Should().BeFalse();
    }

    [Fact]
    public async Task A_sub_frame_navigation_to_a_gated_app_breaks_the_slot_out_to_login()
    {
        await using var factory = await AuthOnFactoryAsync();
        using var client = NoRedirectClient(factory);

        using var req = new HttpRequestMessage(HttpMethod.Get, "/apps/bbs/");
        req.Headers.TryAddWithoutValidation("Sec-Fetch-Mode", "navigate");
        req.Headers.TryAddWithoutValidation("Sec-Fetch-Dest", "iframe");
        req.Headers.TryAddWithoutValidation("Accept", "text/html");
        var resp = await client.SendAsync(req);

        // A renderable 200 HTML (NOT a 401, NOT a 302 the frame couldn't apply to its parent).
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        resp.Content.Headers.ContentType!.MediaType.Should().Be("text/html");
        var body = await resp.Content.ReadAsStringAsync();
        // The break-out script targets window.top with the JSON-encoded login URL.
        body.Should().Contain("window.top.location.href");
        body.Should().Contain("/login?next=");
        body.Should().Contain("%2Fapps%2Fbbs");                // encoded next = the SPA app route /apps/bbs
        resp.Headers.Contains("WWW-Authenticate").Should().BeFalse();
    }

    [Fact]
    public async Task An_xhr_api_request_to_a_gated_app_still_gets_the_bare_401()
    {
        await using var factory = await AuthOnFactoryAsync();
        using var client = NoRedirectClient(factory);

        using var req = new HttpRequestMessage(HttpMethod.Get, "/apps/bbs/data");
        // An XHR/fetch: empty fetch-dest, cors mode, JSON accept — the SPA's on401 owns this.
        req.Headers.TryAddWithoutValidation("Sec-Fetch-Mode", "cors");
        req.Headers.TryAddWithoutValidation("Sec-Fetch-Dest", "empty");
        req.Headers.TryAddWithoutValidation("Accept", "application/json");
        var resp = await client.SendAsync(req);

        resp.StatusCode.Should().Be(HttpStatusCode.Unauthorized);   // unchanged bare 401
        resp.Headers.Contains("WWW-Authenticate").Should().BeTrue();
    }

    [Fact]
    public async Task A_request_with_no_fetch_metadata_falls_back_to_accept_html_for_the_redirect()
    {
        // Older clients / contexts that don't send Sec-Fetch-* still get the renderable redirect
        // when they ask for HTML (a document load), and the bare 401 when they ask for JSON.
        await using var factory = await AuthOnFactoryAsync();
        using var client = NoRedirectClient(factory);

        using var htmlReq = new HttpRequestMessage(HttpMethod.Get, "/apps/bbs/");
        htmlReq.Headers.TryAddWithoutValidation("Accept", "text/html");
        (await client.SendAsync(htmlReq)).StatusCode.Should().Be(HttpStatusCode.Redirect);

        using var jsonReq = new HttpRequestMessage(HttpMethod.Get, "/apps/bbs/");
        jsonReq.Headers.TryAddWithoutValidation("Accept", "application/json");
        (await client.SendAsync(jsonReq)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task A_non_apps_path_navigation_is_untouched_by_the_recovery()
    {
        // The recovery is scoped to /apps/* only — a gated API path keeps the bare 401 even for a
        // browser navigation (it's not a human-plane app frame; the SPA handles its own routes).
        await using var factory = await AuthOnFactoryAsync();
        using var client = NoRedirectClient(factory);

        using var req = new HttpRequestMessage(HttpMethod.Get, "/api/v1/status");
        req.Headers.TryAddWithoutValidation("Sec-Fetch-Mode", "navigate");
        req.Headers.TryAddWithoutValidation("Sec-Fetch-Dest", "document");
        req.Headers.TryAddWithoutValidation("Accept", "text/html");
        var resp = await client.SendAsync(req);

        resp.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("PACKETNET_CONFIG", null);
        Environment.SetEnvironmentVariable("PACKETNET_DB", null);
        try { Directory.Delete(dir, recursive: true); } catch { /* best effort */ }
    }
}
