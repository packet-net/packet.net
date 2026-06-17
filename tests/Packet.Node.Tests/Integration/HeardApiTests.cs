using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Packet.Node.Tests.Integration;

/// <summary>
/// Boots the real <c>Packet.Node</c> composition root and exercises the MHeard REST surface (#454):
/// <c>GET /api/v1/heard</c> (node-wide) and <c>?port=&lt;id&gt;</c> (per-port) are mapped, reachable,
/// and return a JSON array (empty on an idle node — nothing has been heard yet). Mirrors
/// <see cref="ReadApiTests"/>'s temp-config harness.
/// </summary>
[Trait("Category", "Node")]
public sealed class HeardApiTests : IDisposable
{
    private readonly string configPath;
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    public HeardApiTests()
    {
        var dir = Path.Combine(Path.GetTempPath(), "packetnet-heardapi-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        configPath = Path.Combine(dir, "node.yaml");
        File.WriteAllText(configPath, """
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
                  port: 8141
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
    public async Task Heard_node_wide_and_per_port_are_served_as_arrays()
    {
        await using var factory = new NodeAppFactory();
        using var client = factory.CreateClient();

        var wide = await client.GetAsync("/api/v1/heard");
        wide.StatusCode.Should().Be(HttpStatusCode.OK);
        JsonSerializer.Deserialize<JsonElement[]>(await wide.Content.ReadAsStringAsync(), Web)
            .Should().NotBeNull().And.BeEmpty();   // idle node — nothing heard yet

        var perPort = await client.GetAsync("/api/v1/heard?port=vhf");
        perPort.StatusCode.Should().Be(HttpStatusCode.OK);
        JsonSerializer.Deserialize<JsonElement[]>(await perPort.Content.ReadAsStringAsync(), Web)
            .Should().NotBeNull().And.BeEmpty();
    }

    public void Dispose()
    {
        GC.SuppressFinalize(this);
        Environment.SetEnvironmentVariable("PACKETNET_CONFIG", null);
        Environment.SetEnvironmentVariable("PACKETNET_DB", null);
        try
        {
            var dir = Path.GetDirectoryName(configPath);
            if (dir is not null) Directory.Delete(dir, recursive: true);
        }
        catch { /* best effort */ }
    }
}
