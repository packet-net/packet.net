using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Packet.Node.Tests.Integration;

/// <summary>
/// Boots the real <c>Packet.Node</c> composition root and exercises the Slice 3
/// read API (step 1): the unauthenticated <c>GET /api/v1/*</c> endpoints the web
/// monitor consumes. Mirrors <see cref="HostBootTests"/> — a temp YAML config with
/// telnet disabled (so no fixed TCP port is bound under the WAF) and the routing
/// store pointed at the same temp dir — and asserts each endpoint is reachable and
/// projects the configured node identity / port set.
/// </summary>
[Trait("Category", "Node")]
public sealed class ReadApiTests : IDisposable
{
    private const string Callsign = "M0LTE-1";
    private readonly string configPath;

    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    public ReadApiTests()
    {
        // One configured (disabled) port so /api/v1/ports has a non-empty array to
        // size against, while telnet stays off so the WAF-hosted node binds no fixed
        // TCP port (could clash across parallel test classes). A disabled port is a
        // legal idle-node port — it is not brought up, so no transport is opened.
        var dir = Path.Combine(Path.GetTempPath(), "packetnet-readapi-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        configPath = Path.Combine(dir, "node.yaml");
        File.WriteAllText(configPath, $"""
            schemaVersion: 1
            identity:
              callsign: {Callsign}
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
            """);
        Environment.SetEnvironmentVariable("PACKETNET_CONFIG", configPath);
        Environment.SetEnvironmentVariable("PACKETNET_DB", Path.Combine(dir, "pdn.db"));
    }

    private sealed class NodeAppFactory : WebApplicationFactory<Program>
    {
        // Boots Program.Main's host; Kestrel is replaced by the in-memory TestServer.
    }

    [Fact]
    public async Task Healthz_still_serves_after_api_is_mapped()
    {
        await using var factory = new NodeAppFactory();
        using var client = factory.CreateClient();

        var resp = await client.GetAsync("/healthz");
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Status_returns_configured_callsign()
    {
        await using var factory = new NodeAppFactory();
        using var client = factory.CreateClient();

        var resp = await client.GetAsync("/api/v1/status");
        resp.StatusCode.Should().Be(HttpStatusCode.OK);

        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        doc.RootElement.GetProperty("callsign").GetString().Should().Be(Callsign);
        // PortsTotal counts configured ports (the disabled one is still configured).
        doc.RootElement.GetProperty("portsTotal").GetInt32().Should().Be(1);
    }

    [Fact]
    public async Task Config_is_served()
    {
        await using var factory = new NodeAppFactory();
        using var client = factory.CreateClient();

        var resp = await client.GetAsync("/api/v1/config");
        resp.StatusCode.Should().Be(HttpStatusCode.OK);

        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        doc.RootElement.GetProperty("identity").GetProperty("callsign").GetString().Should().Be(Callsign);
    }

    [Fact]
    public async Task Ports_returns_an_array_sized_to_the_configured_ports()
    {
        await using var factory = new NodeAppFactory();
        using var client = factory.CreateClient();

        var resp = await client.GetAsync("/api/v1/ports");
        resp.StatusCode.Should().Be(HttpStatusCode.OK);

        var ports = JsonSerializer.Deserialize<JsonElement[]>(
            await resp.Content.ReadAsStringAsync(), Web);
        ports.Should().NotBeNull();
        ports!.Should().HaveCount(1);
        ports[0].GetProperty("id").GetString().Should().Be("vhf");
        // A configured-but-disabled port reports "down" (not running by design).
        ports[0].GetProperty("state").GetString().Should().Be("down");
    }

    [Fact]
    public async Task NetRom_routes_is_served()
    {
        await using var factory = new NodeAppFactory();
        using var client = factory.CreateClient();

        var resp = await client.GetAsync("/api/v1/netrom/routes");
        resp.StatusCode.Should().Be(HttpStatusCode.OK);

        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        doc.RootElement.GetProperty("neighbours").ValueKind.Should().Be(JsonValueKind.Array);
        doc.RootElement.GetProperty("destinations").ValueKind.Should().Be(JsonValueKind.Array);
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
