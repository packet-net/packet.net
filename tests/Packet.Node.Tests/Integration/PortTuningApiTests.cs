using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Packet.Node.Tests.Integration;

/// <summary>
/// Boots the real <c>Packet.Node</c> composition root and exercises the routing/validation boundary
/// of the guided deviation-tuning API. Arming a real session drives serial + radio hardware (the SDM
/// link, NinoTNC bursts) which can't be stood up under the in-memory WAF — that path is a documented
/// human bench follow-up, and the session state machine is covered hardware-free by
/// <c>PortTuningSessionTests</c>. These assert the responses that never touch the air: unknown/not-
/// running ports (404), a malformed role (400), and the SSE feed 404 when no session exists. Auth is
/// off by default, so the admin-scoped verbs are reachable (the scope gate is a no-op until auth is
/// enabled).
/// </summary>
[Trait("Category", "Node")]
public sealed class PortTuningApiTests : IDisposable
{
    private readonly string configPath;

    public PortTuningApiTests()
    {
        var dir = Path.Combine(Path.GetTempPath(), "packetnet-tuningapi-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        configPath = Path.Combine(dir, "node.yaml");
        // One disabled port: a valid config whose id never names a *running* port.
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
    public async Task Start_is_404_for_an_unknown_port()
    {
        await using var factory = new NodeAppFactory();
        using var client = factory.CreateClient();

        var resp = await client.PostAsJsonAsync(
            "/api/v1/ports/no-such-port/tuning/session", new { role = "tuned", peerSdmId = "12345678" });

        resp.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Start_is_404_for_a_configured_but_not_running_port()
    {
        await using var factory = new NodeAppFactory();
        using var client = factory.CreateClient();

        var resp = await client.PostAsJsonAsync(
            "/api/v1/ports/vhf/tuning/session", new { role = "meter", peerSdmId = "12345678" });

        resp.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Start_is_400_for_a_malformed_role()
    {
        await using var factory = new NodeAppFactory();
        using var client = factory.CreateClient();

        var resp = await client.PostAsJsonAsync(
            "/api/v1/ports/vhf/tuning/session", new { role = "bogus", peerSdmId = "12345678" });

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Events_feed_is_404_when_no_session_is_active()
    {
        await using var factory = new NodeAppFactory();
        using var client = factory.CreateClient();

        var resp = await client.GetAsync("/api/v1/ports/vhf/tuning/events");

        resp.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Next_is_404_when_no_session_is_active()
    {
        await using var factory = new NodeAppFactory();
        using var client = factory.CreateClient();

        var resp = await client.PostAsync("/api/v1/ports/vhf/tuning/next", content: null);

        resp.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Stop_is_404_when_no_session_is_active()
    {
        await using var factory = new NodeAppFactory();
        using var client = factory.CreateClient();

        var resp = await client.PostAsync("/api/v1/ports/vhf/tuning/stop", content: null);

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
