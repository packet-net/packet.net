using System.Net;
using System.Net.Sockets;
using System.Text;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Packet.Node.Tests.Integration;

/// <summary>
/// End-to-end tests for the app-gateway (the human plane, Slice 3): the launcher feed
/// (<c>GET /api/v1/apps</c>) and the reverse proxy (<c>/apps/{id}/*</c>). Boots the real node
/// over the in-memory TestServer with one app whose <c>ui.upstream</c> points at a stub
/// <see cref="HttpListener"/> running on loopback; the forwarder's outbound call is real, so a
/// request through the node reaches the stub, which echoes back the rebased path and the
/// injected identity headers. Auth is off here (an idle node), so the read gate passes — the
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

        dir = Path.Combine(Path.GetTempPath(), "pdn-gw-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        configPath = Path.Combine(dir, "node.yaml");
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
    public async Task Apps_feed_lists_apps_that_have_a_ui()
    {
        await using var factory = new WebApplicationFactory<Program>();
        using var client = factory.CreateClient();

        var resp = await client.GetAsync("/api/v1/apps");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var body = await resp.Content.ReadAsStringAsync();
        Assert.Contains("\"id\":\"wall\"", body, StringComparison.Ordinal);
        Assert.Contains("/apps/wall/", body, StringComparison.Ordinal);
        Assert.Contains("message-square", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Proxies_to_the_upstream_rebasing_the_path_and_injecting_identity()
    {
        await using var factory = new WebApplicationFactory<Program>();
        using var client = factory.CreateClient();

        var resp = await client.GetAsync("/apps/wall/hello?x=1");
        var body = await resp.Content.ReadAsStringAsync();
        Assert.True(HttpStatusCode.OK == resp.StatusCode, $"status={resp.StatusCode} body=<<{body}>>");

        Assert.Contains("path=/hello?x=1", body, StringComparison.Ordinal);   // /apps/wall prefix stripped
        Assert.Contains("gateway=[1]", body, StringComparison.Ordinal);       // gateway marker injected
        Assert.Contains("user=[]", body, StringComparison.Ordinal);           // anonymous (auth off)
    }

    [Fact]
    public async Task Proxies_the_trailing_slash_launcher_url()
    {
        // The launcher links to /apps/{id}/ (trailing slash) — the catch-all must forward it
        // (rest = "" → upstream "/"), NOT 302-loop. (Regression: a `/apps/{id}` redirect route
        // shadowed this and looped; found in lab live-verify.)
        await using var factory = new WebApplicationFactory<Program>();
        using var client = factory.CreateClient();

        var resp = await client.GetAsync("/apps/wall/");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var body = await resp.Content.ReadAsStringAsync();
        Assert.Contains("path=/", body, StringComparison.Ordinal);
        Assert.Contains("gateway=[1]", body, StringComparison.Ordinal);
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
        Assert.Contains("user=[]", body, StringComparison.Ordinal);
        Assert.DoesNotContain("attacker", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_unknown_app_id_is_404()
    {
        await using var factory = new WebApplicationFactory<Program>();
        using var client = factory.CreateClient();

        var resp = await client.GetAsync("/apps/ghost/anything");
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
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
