using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Packet.Node.Core.Applications.Packages;

namespace Packet.Node.Tests.Integration;

/// <summary>
/// End-to-end tests for the app-gateway's package union (<c>docs/app-packages.md</c>): the
/// launcher feed (<c>GET /api/v1/apps</c>) and the reverse proxy (<c>/apps/{id}/*</c>) must
/// also serve enabled, error-free discovered packages whose manifest declares a <c>ui:</c>
/// block — with identity-injection/anti-spoof identical to inline upstreams (the same
/// transformer serves both sources). Mirrors <see cref="AppGatewayApiTests"/>: the real node
/// over the in-memory TestServer, the package's <c>ui.upstream</c> pointing at a loopback
/// stub <see cref="HttpListener"/> that echoes back the rebased path and the injected
/// headers. None of the packages declare a <c>service:</c> block, so the real supervisor the
/// composition root registers stays idle. Auth is off (an idle node), so the read gate passes.
/// </summary>
[Trait("Category", "Node")]
public sealed class AppPackagesGatewayTests : IDisposable
{
    private readonly string dir;
    private readonly string packagesRoot;
    private readonly HttpListener upstream;
    private readonly Task upstreamLoop;

    public AppPackagesGatewayTests()
    {
        var port = FreeTcpPort();
        upstream = new HttpListener();
        upstream.Prefixes.Add($"http://127.0.0.1:{port}/");
        upstream.Start();
        upstreamLoop = Task.Run(EchoUpstreamAsync);

        dir = Path.Combine(Path.GetTempPath(), "pdn-pkggw-" + Guid.NewGuid().ToString("N"));
        packagesRoot = Path.Combine(dir, "apps");
        Directory.CreateDirectory(packagesRoot);

        // pkgui: enabled (apps: grants trust below) with a ui: → tile + proxied. Declares
        // mode: slot so the launcher feed surfaces uiMode for the panel's in-panel iframe path.
        WriteManifest("pkgui", $"""
            manifest: 1
            id: pkgui
            name: PKGUI
            icon: layout-grid
            ui:
              upstream: http://127.0.0.1:{port}
              name: Package UI
              icon: layout-grid
              mode: slot
            """);

        // offpkg: a perfectly valid package the owner has NOT enabled → no tile, no proxy.
        WriteManifest("offpkg", $"""
            manifest: 1
            id: offpkg
            ui:
              upstream: http://127.0.0.1:{port}
            """);

        // dead: broken (id mismatch) — the apps: entry below tries to enable it, but a broken
        // package never goes live: no tile, no proxy.
        WriteManifest("dead", $"""
            manifest: 1
            id: notdead
            ui:
              upstream: http://127.0.0.1:{port}
            """);

        // wall (package side): collides with the inline wall entry → the catalog errors it,
        // so the INLINE app keeps serving /apps/wall. Its upstream points at a port nothing
        // listens on — if the package ever won the route, the proxy would 502, not echo.
        WriteManifest("wall", """
            manifest: 1
            id: wall
            ui:
              upstream: http://127.0.0.1:1
            """);

        var configPath = Path.Combine(dir, "node.yaml");
        File.WriteAllText(configPath, $"""
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
            applications:
              - id: wall
                match: WALL
                command: /bin/cat
                ui:
                  upstream: http://127.0.0.1:{port}
                  name: WALL
                  icon: message-square
            apps:
              - id: pkgui
                enabled: true
              - id: dead
                enabled: true
              - id: wall
                enabled: true
            appPackageRoots:
              - {packagesRoot}
            """);
        Environment.SetEnvironmentVariable("PACKETNET_CONFIG", configPath);
        Environment.SetEnvironmentVariable("PACKETNET_DB", Path.Combine(dir, "pdn.db"));
    }

    private void WriteManifest(string id, string yaml)
    {
        var pkgDir = Path.Combine(packagesRoot, id);
        Directory.CreateDirectory(pkgDir);
        File.WriteAllText(Path.Combine(pkgDir, AppPackageCatalog.ManifestFileName), yaml);
    }

    // The stub app server: echo the request path + the X-Pdn-* headers it received, so the
    // test can assert what the node forwarded.
    private async Task EchoUpstreamAsync()
    {
        while (upstream.IsListening)
        {
            HttpListenerContext ctx;
            try { ctx = await upstream.GetContextAsync().ConfigureAwait(false); }
            catch { break; }   // listener stopped

            var body =
                $"path={ctx.Request.Url!.PathAndQuery}\n" +
                $"user=[{ctx.Request.Headers["X-Pdn-User"]}]\n" +
                $"scope=[{ctx.Request.Headers["X-Pdn-Scope"]}]\n" +
                $"gateway=[{ctx.Request.Headers["X-Pdn-Gateway"]}]\n";
            var bytes = Encoding.UTF8.GetBytes(body);
            ctx.Response.StatusCode = 200;
            ctx.Response.ContentType = "text/plain";
            try
            {
                await ctx.Response.OutputStream.WriteAsync(bytes).ConfigureAwait(false);
            }
            catch { /* client gone */ }
            finally { ctx.Response.Close(); }
        }
    }

    [Fact]
    public async Task Apps_feed_unions_in_enabled_packages_with_a_ui_and_skips_disabled_and_broken_ones()
    {
        await using var factory = new WebApplicationFactory<Program>();
        using var client = factory.CreateClient();

        var json = await client.GetStringAsync("/api/v1/apps");
        var tiles = JsonDocument.Parse(json).RootElement.EnumerateArray()
            .ToDictionary(t => t.GetProperty("id").GetString()!, t => t);

        // The inline app and the enabled package both tile.
        tiles.Should().ContainKey("wall");
        tiles.Should().ContainKey("pkgui");
        tiles["pkgui"].GetProperty("name").GetString().Should().Be("Package UI");   // ui.name wins
        tiles["pkgui"].GetProperty("icon").GetString().Should().Be("layout-grid");
        tiles["pkgui"].GetProperty("url").GetString().Should().Be("/apps/pkgui/");

        // Not enabled / broken packages never tile.
        tiles.Should().NotContainKey("offpkg");
        tiles.Should().NotContainKey("dead");

        // The wall id appears exactly once — the inline entry; the colliding package errored.
        tiles["wall"].GetProperty("name").GetString().Should().Be("WALL");
        json.Split("\"wall\"").Length.Should().Be(2, "the colliding package must not add a second wall tile");
    }

    [Fact]
    public async Task Apps_feed_carries_a_uiMode_field_per_app()
    {
        // The launcher feed surfaces uiMode so the panel's nav knows how to open each app — a full
        // navigation (standalone) vs an in-panel iframe (embedded/slot). The pkgui package declares
        // mode: slot; the inline wall entry declares no mode → standalone (the safe default).
        await using var factory = new WebApplicationFactory<Program>();
        using var client = factory.CreateClient();

        var json = await client.GetStringAsync("/api/v1/apps");
        var tiles = JsonDocument.Parse(json).RootElement.EnumerateArray()
            .ToDictionary(t => t.GetProperty("id").GetString()!, t => t);

        // The field is present on every tile…
        tiles["pkgui"].TryGetProperty("uiMode", out var pkguiMode).Should().BeTrue();
        tiles["wall"].TryGetProperty("uiMode", out var wallMode).Should().BeTrue();
        // …carrying the declared mode (lowercase contract spelling) and the standalone default.
        pkguiMode.GetString().Should().Be("slot");
        wallMode.GetString().Should().Be("standalone");
    }

    [Fact]
    public async Task Apps_feed_carries_a_state_field_null_for_service_less_apps()
    {
        // The launcher feed now carries the supervisor `state` so the panel's nav can render a
        // not-running warning from this one fetch. Neither the inline `wall` nor the `pkgui`
        // package declares a service: block, so both have nothing to run → state is null (the
        // managed/Faulted derivation mirrors PdnAppPackagesApi, covered there).
        await using var factory = new WebApplicationFactory<Program>();
        using var client = factory.CreateClient();

        var json = await client.GetStringAsync("/api/v1/apps");
        var tiles = JsonDocument.Parse(json).RootElement.EnumerateArray()
            .ToDictionary(t => t.GetProperty("id").GetString()!, t => t);

        // The field is present on every tile…
        tiles["pkgui"].TryGetProperty("state", out var pkguiState).Should().BeTrue();
        tiles["wall"].TryGetProperty("state", out var wallState).Should().BeTrue();
        // …and null for these service-less apps (no daemon → nothing to warn about).
        pkguiState.ValueKind.Should().Be(JsonValueKind.Null);
        wallState.ValueKind.Should().Be(JsonValueKind.Null);
    }

    [Fact]
    public async Task Proxies_a_package_upstream_rebasing_the_path_and_injecting_identity()
    {
        await using var factory = new WebApplicationFactory<Program>();
        using var client = factory.CreateClient();

        var resp = await client.GetAsync("/apps/pkgui/hello?x=1");
        var body = await resp.Content.ReadAsStringAsync();
        Assert.True(HttpStatusCode.OK == resp.StatusCode, $"status={resp.StatusCode} body=<<{body}>>");

        body.Should().Contain("path=/hello?x=1");   // /apps/pkgui prefix stripped
        body.Should().Contain("gateway=[1]");        // gateway marker injected
        body.Should().Contain("user=[]");            // anonymous (auth off)
    }

    [Fact]
    public async Task Strips_a_client_supplied_identity_header_on_a_package_route()
    {
        await using var factory = new WebApplicationFactory<Program>();
        using var client = factory.CreateClient();

        using var req = new HttpRequestMessage(HttpMethod.Get, "/apps/pkgui/x");
        req.Headers.TryAddWithoutValidation("X-Pdn-User", "attacker");
        var resp = await client.SendAsync(req);
        var body = await resp.Content.ReadAsStringAsync();

        // Identical anti-spoof discipline to inline upstreams: the spoofed identity is
        // dropped, the upstream sees the gateway's value (empty here), never the client's.
        body.Should().Contain("user=[]");
        body.Should().NotContain("attacker");
    }

    [Fact]
    public async Task A_disabled_or_broken_package_is_404_on_the_proxy()
    {
        await using var factory = new WebApplicationFactory<Program>();
        using var client = factory.CreateClient();

        (await client.GetAsync("/apps/offpkg/anything")).StatusCode
            .Should().Be(HttpStatusCode.NotFound);
        (await client.GetAsync("/apps/dead/anything")).StatusCode
            .Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task An_inline_entry_wins_the_proxy_route_on_an_id_collision()
    {
        await using var factory = new WebApplicationFactory<Program>();
        using var client = factory.CreateClient();

        // The colliding wall package's upstream is a dead port — reaching the echo stub
        // proves the inline entry served the route.
        var resp = await client.GetAsync("/apps/wall/check");
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        (await resp.Content.ReadAsStringAsync()).Should().Contain("path=/check");
    }

    private static int FreeTcpPort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }

    public void Dispose()
    {
        GC.SuppressFinalize(this);
        Environment.SetEnvironmentVariable("PACKETNET_CONFIG", null);
        Environment.SetEnvironmentVariable("PACKETNET_DB", null);
        try { upstream.Stop(); } catch { /* ignore */ }
        try { upstream.Close(); } catch { /* ignore */ }
        try { upstreamLoop.Wait(TimeSpan.FromSeconds(2)); } catch { /* ignore */ }
        try { Directory.Delete(dir, recursive: true); } catch { /* best effort */ }
    }
}
