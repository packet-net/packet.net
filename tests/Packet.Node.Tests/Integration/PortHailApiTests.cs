using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Packet.Node.Tests.Integration;

/// <summary>
/// Boots the real <c>Packet.Node</c> composition root and exercises the routing boundary of the SDM
/// station-hail endpoint <c>POST /api/v1/ports/{id}/hail</c> (admin/audited/transmits). The success
/// path hails a peer over a live radio's SDM side channel, which can't be stood up under the
/// in-memory WAF (and is covered hardware-free by <c>PortHailServiceTests</c>), so these assert the
/// 404s that don't touch the air: an unknown port id, and a configured-but-not-running port. Auth is
/// off by default, so the admin-scoped POST is reachable here.
/// </summary>
[Trait("Category", "Node")]
public sealed class PortHailApiTests : IDisposable
{
    private readonly string configPath;

    public PortHailApiTests()
    {
        var dir = Path.Combine(Path.GetTempPath(), "packetnet-hailapi-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        configPath = Path.Combine(dir, "node.yaml");
        File.WriteAllText(configPath, """
            schemaVersion: 1
            identity:
              callsign: M0LTE-1
            ports:
              - id: vhf
                enabled: false
                transport:
                  kind: kiss-tcp
                  host: 127.0.0.1
                  port: 8123
            management:
              telnet:
                enabled: false
              http:
                bind: 127.0.0.1
                port: 8080
            """);
        Environment.SetEnvironmentVariable("PACKETNET_CONFIG", configPath);
        Environment.SetEnvironmentVariable("PACKETNET_DB", Path.Combine(dir, "pdn.db"));
    }

    private sealed class NodeAppFactory : WebApplicationFactory<Program>;

    [Fact]
    public async Task Hail_is_404_for_an_unknown_port()
    {
        await using var factory = new NodeAppFactory();
        using var client = factory.CreateClient();

        var resp = await client.PostAsJsonAsync("/api/v1/ports/no-such-port/hail", new { peerSdmId = "PDN00001" });

        resp.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Hail_is_404_for_a_configured_but_not_running_port()
    {
        await using var factory = new NodeAppFactory();
        using var client = factory.CreateClient();

        var resp = await client.PostAsJsonAsync("/api/v1/ports/vhf/hail", new { peerSdmId = "PDN00001" });

        resp.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("PACKETNET_CONFIG", null);
        Environment.SetEnvironmentVariable("PACKETNET_DB", null);
        try
        {
            var dir = Path.GetDirectoryName(configPath);
            if (dir is not null)
            {
                Directory.Delete(dir, recursive: true);
            }
        }
        catch { /* best effort */ }
    }
}
