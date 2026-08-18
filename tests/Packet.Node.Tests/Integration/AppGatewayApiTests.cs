using System.Net;
using System.Net.Sockets;
using System.Text;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Packet.Node.Tests.Support;

namespace Packet.Node.Tests.Integration;

/// <summary>
/// End-to-end tests for the app-gateway (the human plane, Slice 3): the launcher feed
/// (<c>GET /api/v1/apps</c>) and the reverse proxy (<c>/apps/{id}/*</c>). Boots the real node
/// over the in-memory TestServer with one app whose <c>ui.upstream</c> points at a stub
/// <see cref="HttpListener"/> running on loopback; the forwarder's outbound call is real, so a
/// request through the node reaches the stub, which echoes back the rebased path and the
/// injected identity headers. Auth is off here (an idle node), so the read gate passes - the
/// auth path itself is covered by the auth suites.
/// </summary>
[Trait("Category", "Node")]
public sealed class AppGatewayApiTests : IDisposable
{
    private readonly string dir;
    private readonly string configPath;
    private readonly HttpListener upstream;
    private readonly Task upstreamLoop;

    public AppGatewayApiTests()
    {
        var port = FreeTcpPort();
        upstream = new HttpListener();
        upstream.Prefixes.Add($"http://127.0.0.1:{port}/");
        upstream.Start();
        upstreamLoop = Task.Run(EchoUpstreamAsync);

        dir = TestPaths.NewPath("pdn-gw");
        Directory.CreateDirectory(dir);
        configPath = Path.Combine(dir, "node.yaml");
        File.WriteAllText(configPath, $"""
            schemaVersion: 1
            identity:
              callsign: M0LTE-1
            ports: []
            management:
              auth:
                enabled: false
              telnet:
                enabled: false
              http:
                bind: 127.0.0.1
                port: 8080
            applications:
              - id: wall
                command: WALL
                executable: /bin/cat
                ui:
                  upstream: http://127.0.0.1:{port}
                  name: WALL
                  icon: message-square
            """);
        Environment.SetEnvironmentVariable("PACKETNET_CONFIG", configPath);
        Environment.SetEnvironmentVariable("PACKETNET_DB", Path.Combine(dir, "pdn.db"));
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
                $"gateway=[{ctx.Request.Headers["X-Pdn-Gateway"]}]\n" +
                $"prefix=[{ctx.Request.Headers["X-Forwarded-Prefix"]}]\n";
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
    public async Task Apps_feed_lists_apps_that_have_a_ui()
    {
        await using var factory = new WebApplicationFactory<Program>();
        using var client = factory.CreateClient();

        var resp = await client.GetAsync("/api/v1/apps");
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await resp.Content.ReadAsStringAsync();
        body.Should().Contain("\"id\":\"wall\"");
        body.Should().Contain("/apps/wall/");
        body.Should().Contain("message-square");
    }

    [Fact]
    public async Task Proxies_to_the_upstream_rebasing_the_path_and_injecting_identity()
    {
        await using var factory = new WebApplicationFactory<Program>();
        using var client = factory.CreateClient();

        var resp = await client.GetAsync("/apps/wall/hello?x=1");
        var body = await resp.Content.ReadAsStringAsync();
        // The body is passed as a because-ARG, not interpolated: it is arbitrary upstream text and
        // AwesomeAssertions treats the because string itself as a format string.
        (HttpStatusCode.OK == resp.StatusCode).Should().BeTrue("status={0} body=<<{1}>>", resp.StatusCode, body);

        body.Should().Contain("path=/hello?x=1");     // /apps/wall prefix stripped
        body.Should().Contain("gateway=[1]");         // gateway marker injected
        body.Should().Contain("prefix=[/apps/wall]"); // the public mount point, apps prefix absolute URLs with it
        body.Should().Contain("user=[]");             // anonymous (auth off)
    }

    [Fact]
    public async Task Proxies_the_trailing_slash_launcher_url()
    {
        // The launcher links to /apps/{id}/ (trailing slash) - the catch-all must forward it
        // (rest = "" → upstream "/"), NOT 302-loop. (Regression: a `/apps/{id}` redirect route
        // shadowed this and looped; found in lab live-verify.)
        await using var factory = new WebApplicationFactory<Program>();
        using var client = factory.CreateClient();

        var resp = await client.GetAsync("/apps/wall/");
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await resp.Content.ReadAsStringAsync();
        body.Should().Contain("path=/");
        body.Should().Contain("gateway=[1]");
    }

    [Fact]
    public async Task Bare_no_slash_app_url_serves_the_spa_shell_not_the_proxied_app()
    {
        // The bare `/apps/{id}` (no trailing slash) is the SPA's in-panel route for a slot/embedded
        // app - a hard reload there must boot the SPA shell so the app stays embedded in pdn chrome
        // (AppFrame), NOT proxy the raw app. (Regression: F5 on /apps/bbs dropped pdn's chrome and
        // left only the bare BBS UI; found in lab live-verify.)
        //
        // Use a self-contained web root with a stub SPA shell: the gateway serves `index.html` from
        // the web root, and the committed/built wwwroot is NOT reliably at the WebApplicationFactory
        // content root across machines (it's present locally, absent under CI - which silently fell
        // through to the proxy and made this assertion flap). Pinning the web root makes it deterministic.
        string webRoot = TestPaths.NewPath("pdn-gw-webroot");
        Directory.CreateDirectory(webRoot);
        await File.WriteAllTextAsync(Path.Combine(webRoot, "index.html"),
            "<!doctype html><html><body><div id=\"root\"></div></body></html>");
        try
        {
            await using var factory = new WebApplicationFactory<Program>()
                .WithWebHostBuilder(b => b.UseWebRoot(webRoot));
            using var client = factory.CreateClient();

            var resp = await client.GetAsync("/apps/wall");
            resp.StatusCode.Should().Be(HttpStatusCode.OK);
            var body = await resp.Content.ReadAsStringAsync();

            // The SPA shell (index.html from the web root), not the upstream echo. The echo body would
            // carry `gateway=[1]`; the shell carries the React root div.
            body.Should().Contain("<div id=\"root\">");
            body.Should().NotContain("gateway=[1]");
            // The shell must be no-cache so a redeploy's new asset hashes are picked up at once.
            (resp.Headers.CacheControl?.ToString() ?? "").Should().Contain("no-cache");
        }
        finally
        {
            Directory.Delete(webRoot, recursive: true);
        }
    }

    [Fact]
    public async Task Strips_any_client_supplied_identity_header()
    {
        await using var factory = new WebApplicationFactory<Program>();
        using var client = factory.CreateClient();

        using var req = new HttpRequestMessage(HttpMethod.Get, "/apps/wall/x");
        req.Headers.TryAddWithoutValidation("X-Pdn-User", "attacker");
        var resp = await client.SendAsync(req);
        var body = await resp.Content.ReadAsStringAsync();

        // The spoofed identity is dropped — the upstream sees the gateway's value (empty here),
        // never the client's.
        body.Should().Contain("user=[]");
        body.Should().NotContain("attacker");
    }

    [Fact]
    public async Task An_unknown_app_id_is_404()
    {
        await using var factory = new WebApplicationFactory<Program>();
        using var client = factory.CreateClient();

        var resp = await client.GetAsync("/apps/ghost/anything");
        resp.StatusCode.Should().Be(HttpStatusCode.NotFound);
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
