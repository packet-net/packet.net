using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Packet.Node.Tests.Integration;

/// <summary>
/// Boots the real <c>Packet.Node</c> composition root and exercises the routing boundary of the
/// capability doctor — <c>GET /api/v1/ports/{id}/doctor</c> (safe/read) and
/// <c>POST /api/v1/ports/{id}/doctor?interrupt=true</c> (admin/audited/transmits). The success
/// path probes a live port's serial handles, which can't be stood up under the in-memory WAF (and
/// is covered hardware-free by <c>PortDoctorRunnerTests</c>), so these assert the 404s that don't
/// touch the air: an unknown port id, and a configured-but-not-running port. Auth is off by default
/// (no <c>management.auth.enabled</c>), so the admin-scoped POST is reachable here — the scope gate
/// is a no-op until auth is turned on.
/// </summary>
[Trait("Category", "Node")]
public sealed class PortDoctorApiTests : IDisposable
{
    private readonly string configPath;

    public PortDoctorApiTests()
    {
        var dir = Path.Combine(Path.GetTempPath(), "packetnet-doctorapi-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        configPath = Path.Combine(dir, "node.yaml");
        // One disabled port: configured (valid config) but not brought up, so its id never names a
        // *running* port — the doctor has no live handles to probe → 404.
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
    public async Task Doctor_get_is_404_for_an_unknown_port()
    {
        await using var factory = new NodeAppFactory();
        using var client = factory.CreateClient();

        var resp = await client.GetAsync("/api/v1/ports/no-such-port/doctor");

        resp.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Doctor_get_is_404_for_a_configured_but_not_running_port()
    {
        await using var factory = new NodeAppFactory();
        using var client = factory.CreateClient();

        var resp = await client.GetAsync("/api/v1/ports/vhf/doctor");

        resp.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Doctor_interrupt_post_is_404_for_an_unknown_port()
    {
        await using var factory = new NodeAppFactory();
        using var client = factory.CreateClient();

        var resp = await client.PostAsync("/api/v1/ports/no-such-port/doctor?interrupt=true", content: null);

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
