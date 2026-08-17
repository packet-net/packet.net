using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;
using Packet.Node.Tests.Support;

namespace Packet.Node.Tests.Integration;

/// <summary>
/// With auth ON and a real proxied request in flight, the app upstream must see the injected
/// identity headers and <b>neither</b> of the viewer's credentials (review item C054).
/// </summary>
/// <remarks>
/// The unit-level proof is in <c>AppGatewayTransformerTests</c>; this is the end-to-end one,
/// because the credential the browser actually carries to <c>/apps/*</c> is the <c>pdn_at</c>
/// cookie the JwtBearer pipeline reads - the request authenticates FROM that cookie, and the
/// cookie was then forwarded to the app anyway.
/// </remarks>
[Trait("Category", "Node")]
public sealed class AppGatewayCredentialTests : IDisposable
{
    private readonly AuthNode node = new("gwcreds");
    private readonly HttpListener upstream;
    private readonly Task upstreamLoop;
    private readonly string appsBlock;

    public AppGatewayCredentialTests()
    {
        var port = FreeTcpPort();
        upstream = new HttpListener();
        upstream.Prefixes.Add($"http://127.0.0.1:{port}/");
        upstream.Start();
        upstreamLoop = Task.Run(EchoUpstreamAsync);

        appsBlock = $"""
            applications:
              - id: wall
                command: WALL
                executable: /bin/cat
                ui:
                  upstream: http://127.0.0.1:{port}
                  name: WALL
            """;
    }

    [Fact]
    public async Task The_upstream_sees_the_identity_headers_and_neither_credential()
    {
        node.WriteConfig(authEnabled: false, appsBlock);
        await using (var setupFactory = node.Factory())
        using (var setupClient = setupFactory.CreateClient())
        {
            await AuthNode.Setup(setupClient, "sysop", "sysoppassword");
            await AuthNode.FlipAuthOn(setupClient, appsBlock);
        }

        await using var factory = node.Factory();
        using var client = factory.CreateClient();

        var token = await AuthNode.Login(client, "sysop", "sysoppassword");

        // Both credentials on one request: the bearer header the panel's fetch uses and the
        // pdn_at cookie a browser navigation carries.
        using var req = new HttpRequestMessage(HttpMethod.Get, "/apps/wall/page");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        req.Headers.Add("Cookie", $"pdn_at={token}; wall_prefs=dark");
        var resp = await client.SendAsync(req);

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var echoed = await resp.Content.ReadAsStringAsync();

        echoed.Should().Contain("user=[sysop]");            // identity still injected (C011 too)
        echoed.Should().Contain("scope=[admin]");
        echoed.Should().Contain("authorization=[]");        // the bearer never leaves pdn
        echoed.Should().Contain("cookie=[wall_prefs=dark]"); // the app's own cookie survives
        echoed.Should().NotContain(token);
    }

    // The stub app: echo the identity headers plus the two credential-carrying ones, so the
    // test asserts on exactly what crossed the loopback boundary.
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
                $"authorization=[{ctx.Request.Headers["Authorization"]}]\n" +
                $"cookie=[{ctx.Request.Headers["Cookie"]}]\n";
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

    private static int FreeTcpPort()
    {
        var listener = new TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        int port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    public void Dispose()
    {
        try { upstream.Stop(); } catch { /* ignore */ }
        try { upstream.Close(); } catch { /* ignore */ }
        try { upstreamLoop.Wait(TimeSpan.FromSeconds(2)); } catch { /* ignore */ }
        node.Dispose();
    }
}
