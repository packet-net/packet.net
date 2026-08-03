using System.Net;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Packet.Node.Tests.Integration;

/// <summary>
/// Boots the real <c>Packet.Node</c> composition root and exercises the Prometheus
/// <c>GET /metrics</c> endpoint (#457): it is mapped on the same listener as the REST API,
/// stays anonymous whether or not auth is on, serves the Prometheus text content type, and the
/// body parses cleanly into HELP/TYPE/sample lines all in the <c>pdn_*</c> namespace. Mirrors
/// <see cref="ReadApiTests"/>'s temp-config harness.
/// </summary>
[Trait("Category", "Node")]
public sealed class MetricsEndpointTests : IDisposable
{
    private const string Callsign = "M0LTE-1";
    private readonly string configPath;

    public MetricsEndpointTests()
    {
        var dir = Path.Combine(Path.GetTempPath(), "packetnet-metrics-" + Guid.NewGuid().ToString("N"));
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
                  port: 8131
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
    public async Task Metrics_is_served_in_the_prometheus_exposition_format()
    {
        await using var factory = new NodeAppFactory();
        using var client = factory.CreateClient();

        var resp = await client.GetAsync("/metrics");
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        resp.Content.Headers.ContentType!.MediaType.Should().Be("text/plain");

        var body = await resp.Content.ReadAsStringAsync();

        // Well-formed: HELP + TYPE headers present, and every sample line is pdn_-namespaced.
        body.Should().Contain("# HELP pdn_");
        body.Should().Contain("# TYPE pdn_");
        body.Should().Contain("pdn_build_info{");
        body.Should().Contain($"callsign=\"{Callsign}\"");
        // The configured-but-disabled port appears with the port label.
        body.Should().Contain("pdn_port_up{port=\"vhf\"}");
        body.Should().Contain("pdn_ports_total 1");

        foreach (var line in body.Split('\n'))
        {
            if (line.Length == 0 || line.StartsWith('#'))
            {
                continue;
            }
            line.Should().StartWith("pdn_");
            // Each sample line ends `... <value>` — a final space-delimited numeric token.
            int sp = line.LastIndexOf(' ');
            sp.Should().BeGreaterThan(0);
            double.TryParse(line[(sp + 1)..], System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out _).Should().BeTrue($"value on line '{line}' parses");
        }
    }

    [Fact]
    public async Task Metrics_stays_anonymous_with_auth_on()
    {
        // The Prometheus contract: a scraper holds a static config, not a login, and this
        // node's access tokens live 60 minutes — so /metrics is AllowAnonymous regardless of
        // management.auth.enabled. Auth defaults on now, so without this the documented
        // scrape workflow would 401 on every stock install. Deliberate (Tom, 2026-08-03):
        // metrics are public; the exposure trade is documented in docs/observability.md.
        File.WriteAllText(configPath, File.ReadAllText(configPath).Replace(
            """
              auth:
                enabled: false
            """,
            """
              auth:
                enabled: true
            """,
            StringComparison.Ordinal));

        await using var factory = new NodeAppFactory();
        using var client = factory.CreateClient();

        var resp = await client.GetAsync("/metrics");   // no Authorization header
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        (await resp.Content.ReadAsStringAsync()).Should().Contain("pdn_build_info{");

        // Contrast: an ordinary read endpoint under the same config does demand a token.
        (await client.GetAsync("/api/v1/status")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    public void Dispose()
    {
        GC.SuppressFinalize(this);
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
