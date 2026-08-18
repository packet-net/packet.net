using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Packet.Node.Tests.Integration;

/// <summary>
/// Forwarded-headers trust scoping (network-access.md S1). pdn enables
/// <c>UseForwardedHeaders</c> so that behind the loopback TLS edge (the embedded
/// Tailscale tsnet sidecar) it sees the PUBLIC https scheme/host - making the
/// app-gateway's <c>pdn_at</c> cookie Secure flag and any request-derived WebAuthn
/// origin correct. The trust is scoped to a LOOPBACK proxy only (anti-spoof): a
/// request carrying <c>X-Forwarded-Proto: https</c> from a non-loopback peer must NOT
/// be honoured. These tests stand up a minimal pipeline wired with the exact options
/// from <c>Program.cs</c> and assert both halves of that contract.
/// </summary>
[Trait("Category", "Node")]
public sealed class ForwardedHeadersTests
{
    // Mirrors the Program.cs Configure<ForwardedHeadersOptions> block verbatim: trust
    // X-Forwarded-Proto/Host/For from loopback only (clear defaults, add the two
    // loopback addresses). A test middleware sets the connection's remote IP from an
    // X-Test-RemoteIp header so we can simulate a loopback vs a remote peer.
    private static TestServer BuildServer()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.Configure<ForwardedHeadersOptions>(o =>
        {
            o.ForwardedHeaders = ForwardedHeaders.XForwardedFor
                | ForwardedHeaders.XForwardedProto
                | ForwardedHeaders.XForwardedHost;
            o.KnownProxies.Clear();
            o.KnownIPNetworks.Clear();
            o.KnownProxies.Add(IPAddress.Loopback);
            o.KnownProxies.Add(IPAddress.IPv6Loopback);
        });

        var app = builder.Build();

        // Simulate the connecting peer's IP (TestServer leaves RemoteIpAddress null).
        // Must run BEFORE UseForwardedHeaders so the middleware sees the right peer.
        app.Use(async (ctx, next) =>
        {
            if (ctx.Request.Headers.TryGetValue("X-Test-RemoteIp", out var ip)
                && IPAddress.TryParse(ip.ToString(), out var parsed))
            {
                ctx.Connection.RemoteIpAddress = parsed;
            }
            await next();
        });

        app.UseForwardedHeaders();

        app.MapGet("/echo", (HttpContext ctx) =>
            Results.Ok(new { scheme = ctx.Request.Scheme, host = ctx.Request.Host.Value, isHttps = ctx.Request.IsHttps }));

        app.Start();
        return app.GetTestServer();
    }

    [Fact]
    public async Task Loopback_proxy_with_x_forwarded_proto_https_is_seen_as_https()
    {
        using var server = BuildServer();
        using var client = server.CreateClient();

        var req = new HttpRequestMessage(HttpMethod.Get, "/echo");
        req.Headers.Add("X-Test-RemoteIp", "127.0.0.1");        // the loopback sidecar
        req.Headers.Add("X-Forwarded-Proto", "https");
        req.Headers.Add("X-Forwarded-Host", "pdn.tailnet.ts.net");

        var resp = await client.SendAsync(req);
        var body = await resp.Content.ReadAsStringAsync();

        body.Should().Contain("\"scheme\":\"https\"", "a loopback proxy is trusted");
        body.Should().Contain("\"isHttps\":true");
        body.Should().Contain("pdn.tailnet.ts.net", "the forwarded host is honoured too");
    }

    [Fact]
    public async Task Non_loopback_remote_x_forwarded_proto_is_not_trusted()
    {
        using var server = BuildServer();
        using var client = server.CreateClient();

        var req = new HttpRequestMessage(HttpMethod.Get, "/echo");
        req.Headers.Add("X-Test-RemoteIp", "203.0.113.7");      // an arbitrary remote client
        req.Headers.Add("X-Forwarded-Proto", "https");
        req.Headers.Add("X-Forwarded-Host", "evil.example");

        var resp = await client.SendAsync(req);
        var body = await resp.Content.ReadAsStringAsync();

        // The spoofed scheme/host are ignored - the request stays plain http (anti-spoof).
        body.Should().Contain("\"scheme\":\"http\"", "a non-loopback client must not be trusted to set the scheme");
        body.Should().Contain("\"isHttps\":false");
        body.Should().NotContain("evil.example", "a non-loopback client must not be trusted to set the host");
    }

    [Fact]
    public async Task No_forwarded_headers_is_a_no_op_plain_http()
    {
        using var server = BuildServer();
        using var client = server.CreateClient();

        // With no proxy/headers (the default LAN deployment), enabling the middleware
        // changes nothing - the request is plain http.
        var req = new HttpRequestMessage(HttpMethod.Get, "/echo");
        req.Headers.Add("X-Test-RemoteIp", "127.0.0.1");

        var resp = await client.SendAsync(req);
        var body = await resp.Content.ReadAsStringAsync();

        body.Should().Contain("\"scheme\":\"http\"");
        body.Should().Contain("\"isHttps\":false");
    }
}
