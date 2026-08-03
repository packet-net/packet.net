using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Packet.Node.Tests.Integration;

/// <summary>
/// Boots the real <c>Packet.Node</c> composition root and exercises the input-validation
/// boundary of <c>POST /api/v1/ping</c> — the connectionless TEST ping ("axping"). The
/// success path (a real TEST echo) needs a live peer that answers TEST, which can't be
/// stood up under the in-memory WAF (and the human live-verifies it against GB7RDG), so
/// these tests assert the two pre-flight rejections that don't touch the air: an
/// unparseable <c>station</c> → 400, and a <c>portId</c> that names no running port → 404.
/// Mirrors <see cref="ReadApiTests"/>: a temp YAML config with telnet disabled (no fixed
/// TCP port bound under the WAF) and the routing store in the same temp dir.
/// </summary>
[Trait("Category", "Node")]
public sealed class PingApiTests : IDisposable
{
    private const string Callsign = "M0LTE-1";
    private readonly string configPath;

    public PingApiTests()
    {
        var dir = Path.Combine(Path.GetTempPath(), "packetnet-pingapi-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        configPath = Path.Combine(dir, "node.yaml");
        // One disabled port: it's configured (so the config is valid) but not brought up, so
        // its id never names a *running* port — exactly the 404 case for ping.
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
              auth:
                enabled: false
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
    public async Task Ping_unknown_port_is_404()
    {
        await using var factory = new NodeAppFactory();
        using var client = factory.CreateClient();

        var resp = await client.PostAsJsonAsync("/api/v1/ping", new
        {
            station = "GB7RDG-1",
            portId = "no-such-port",
            count = 3,
        });

        resp.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Ping_disabled_port_is_404()
    {
        await using var factory = new NodeAppFactory();
        using var client = factory.CreateClient();

        // 'vhf' is configured but disabled — not a running port, so ping can't send on it.
        var resp = await client.PostAsJsonAsync("/api/v1/ping", new
        {
            station = "GB7RDG-1",
            portId = "vhf",
            count = 3,
        });

        resp.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Ping_unparseable_station_is_400()
    {
        await using var factory = new NodeAppFactory();
        using var client = factory.CreateClient();

        var resp = await client.PostAsJsonAsync("/api/v1/ping", new
        {
            station = "not a callsign!!",
            portId = "vhf",
            count = 3,
        });

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Ping_missing_station_is_400()
    {
        await using var factory = new NodeAppFactory();
        using var client = factory.CreateClient();

        var resp = await client.PostAsJsonAsync("/api/v1/ping", new
        {
            station = "",
            portId = "vhf",
            count = 3,
        });

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("PACKETNET_CONFIG", null);
        Environment.SetEnvironmentVariable("PACKETNET_DB", null);
        try
        {
            var dir = Path.GetDirectoryName(configPath);
            if (dir is not null && Directory.Exists(dir))
            {
                Directory.Delete(dir, recursive: true);
            }
        }
        catch (IOException)
        {
            // Best-effort temp cleanup; a locked SQLite handle on a slow box isn't a failure.
        }
    }
}
