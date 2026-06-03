using System.Net;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Packet.Node.Tests.Integration;

/// <summary>
/// Boots the real <c>Packet.Node</c> composition root (exit criterion i): the
/// binary comes up from a YAML config and serves <c>GET /healthz</c> — and ONLY
/// that. The web server is present-but-inert in slice 1; no authenticated
/// endpoints are mapped, so any other path 404s. Uses
/// <see cref="WebApplicationFactory{TEntryPoint}"/> (TestServer), so no real port
/// is bound.
/// </summary>
[Trait("Category", "Node")]
public sealed class HostBootTests : IDisposable
{
    private readonly string configPath;

    public HostBootTests()
    {
        // Point the host at a temp config dir. Pre-write an idle-node config with
        // telnet disabled so the WAF-hosted node doesn't bind a fixed TCP port
        // (which could clash across parallel test classes). The first-start
        // template path is covered separately by FileConfigProviderTests.
        var dir = Path.Combine(Path.GetTempPath(), "packetnet-host-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        configPath = Path.Combine(dir, "node.yaml");
        File.WriteAllText(configPath, """
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
            """);
        Environment.SetEnvironmentVariable("PACKETNET_CONFIG", configPath);
    }

    private sealed class NodeAppFactory : WebApplicationFactory<Program>
    {
        // The factory boots Program.Main's host; Kestrel is replaced by the
        // in-memory TestServer, so the config's http bind is inert here (exactly
        // the "present-but-inert" shape — we just need the endpoint map).
    }

    [Fact]
    public async Task Boots_from_config_and_serves_healthz()
    {
        await using var factory = new NodeAppFactory();
        using var client = factory.CreateClient();

        var resp = await client.GetAsync("/healthz");
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        (await resp.Content.ReadAsStringAsync()).Should().Contain("ok");
    }

    [Fact]
    public async Task Maps_no_other_endpoints_in_slice_1()
    {
        await using var factory = new NodeAppFactory();
        using var client = factory.CreateClient();

        // No API / auth / UI surface yet — anything but /healthz is unmapped.
        foreach (var path in new[] { "/", "/api/ports", "/setup", "/index.html" })
        {
            var resp = await client.GetAsync(path);
            resp.StatusCode.Should().Be(HttpStatusCode.NotFound, "slice 1 maps only /healthz; {0} must be unmapped", path);
        }
    }

    public void Dispose()
    {
        GC.SuppressFinalize(this);
        Environment.SetEnvironmentVariable("PACKETNET_CONFIG", null);
        try
        {
            var dir = Path.GetDirectoryName(configPath);
            if (dir is not null) Directory.Delete(dir, recursive: true);
        }
        catch { /* best effort */ }
    }
}
