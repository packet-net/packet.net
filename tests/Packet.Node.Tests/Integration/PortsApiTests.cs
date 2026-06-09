using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Packet.Node.Core.Api;

namespace Packet.Node.Tests.Integration;

/// <summary>
/// Boots the real <c>Packet.Node</c> composition root and exercises the Slice 3
/// port-management API (step 3): <c>POST/PUT/DELETE /api/v1/ports</c> + the
/// <c>/ports/{id}/lifecycle</c> up/down/restart actions. Mirrors
/// <see cref="ConfigWriteApiTests"/> / <see cref="ReadApiTests"/> — a temp YAML config
/// with no ports and telnet disabled (so no fixed TCP port is bound under the WAF) and
/// the routing store in the same temp dir. Each mutation flows through the live
/// <c>FileConfigProvider</c> write seam, so a follow-up <c>GET /api/v1/ports</c> (or
/// <c>/config</c>) reflects the applied state.
/// </summary>
[Trait("Category", "Node")]
public sealed class PortsApiTests : IDisposable
{
    private const string Callsign = "M0LTE-1";
    private readonly string configPath;

    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    public PortsApiTests()
    {
        // Start with NO ports — every test adds/removes its own. Telnet off so the
        // WAF-hosted node binds no fixed TCP port (could clash across parallel classes).
        var dir = Path.Combine(Path.GetTempPath(), "packetnet-portsapi-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        configPath = Path.Combine(dir, "node.yaml");
        File.WriteAllText(configPath, $"""
            schemaVersion: 1
            identity:
              callsign: {Callsign}
              alias: LONDON
            ports: []
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

    // A PortConfig request body as a JSON object: a disabled kiss-tcp port (disabled so
    // the WAF host never opens a real socket to a non-existent endpoint).
    private static object KissTcpPort(string id, string host, int port, bool enabled = false) => new
    {
        id,
        enabled,
        transport = new { kind = "kiss-tcp", host, port },
    };

    private static async Task<JsonElement[]> GetPortsAsync(HttpClient client)
    {
        var json = await client.GetStringAsync("/api/v1/ports");
        return JsonSerializer.Deserialize<JsonElement[]>(json, Web)!;
    }

    [Fact]
    public async Task Post_adds_a_port_and_get_ports_lists_it()
    {
        await using var factory = new NodeAppFactory();
        using var client = factory.CreateClient();

        var resp = await client.PostAsJsonAsync("/api/v1/ports", KissTcpPort("vhf", "127.0.0.1", 8101));
        resp.StatusCode.Should().Be(HttpStatusCode.OK);

        var result = await resp.Content.ReadFromJsonAsync<ReconcileResult>(Web);
        result.Should().NotBeNull();
        result!.Applied.Should().BeTrue();

        var ports = await GetPortsAsync(client);
        ports.Should().ContainSingle(p => p.GetProperty("id").GetString() == "vhf");
    }

    [Fact]
    public async Task Put_edits_a_port_and_get_reflects_the_change()
    {
        await using var factory = new NodeAppFactory();
        using var client = factory.CreateClient();

        (await client.PostAsJsonAsync("/api/v1/ports", KissTcpPort("vhf", "127.0.0.1", 8101)))
            .StatusCode.Should().Be(HttpStatusCode.OK);

        // Edit: change the transport port (8101 -> 8102).
        var edit = await client.PutAsJsonAsync("/api/v1/ports/vhf", KissTcpPort("vhf", "127.0.0.1", 8102));
        edit.StatusCode.Should().Be(HttpStatusCode.OK);
        (await edit.Content.ReadFromJsonAsync<ReconcileResult>(Web))!.Applied.Should().BeTrue();

        // GET /config reflects the edited transport port.
        using var doc = JsonDocument.Parse(await client.GetStringAsync("/api/v1/config"));
        var vhf = doc.RootElement.GetProperty("ports").EnumerateArray()
            .Single(p => p.GetProperty("id").GetString() == "vhf");
        vhf.GetProperty("transport").GetProperty("port").GetInt32().Should().Be(8102);
    }

    [Fact]
    public async Task Delete_removes_a_port_and_get_no_longer_lists_it()
    {
        await using var factory = new NodeAppFactory();
        using var client = factory.CreateClient();

        (await client.PostAsJsonAsync("/api/v1/ports", KissTcpPort("vhf", "127.0.0.1", 8101)))
            .StatusCode.Should().Be(HttpStatusCode.OK);

        var del = await client.DeleteAsync("/api/v1/ports/vhf");
        del.StatusCode.Should().Be(HttpStatusCode.OK);
        (await del.Content.ReadFromJsonAsync<ReconcileResult>(Web))!.Applied.Should().BeTrue();

        var ports = await GetPortsAsync(client);
        ports.Should().NotContain(p => p.GetProperty("id").GetString() == "vhf");
    }

    [Fact]
    public async Task Post_a_duplicate_id_returns_422()
    {
        await using var factory = new NodeAppFactory();
        using var client = factory.CreateClient();

        (await client.PostAsJsonAsync("/api/v1/ports", KissTcpPort("vhf", "127.0.0.1", 8101)))
            .StatusCode.Should().Be(HttpStatusCode.OK);

        // Same id again — distinct endpoint so it is the unique-id rule that rejects it.
        var dup = await client.PostAsJsonAsync("/api/v1/ports", KissTcpPort("vhf", "127.0.0.1", 8102));
        dup.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);

        var problem = await dup.Content.ReadFromJsonAsync<ValidationProblem>(Web);
        problem.Should().NotBeNull();
        problem!.Errors.Should().NotBeEmpty();
    }

    [Fact]
    public async Task Put_an_unknown_id_returns_404()
    {
        await using var factory = new NodeAppFactory();
        using var client = factory.CreateClient();

        var resp = await client.PutAsJsonAsync("/api/v1/ports/nope", KissTcpPort("nope", "127.0.0.1", 8101));
        resp.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Delete_an_unknown_id_returns_404()
    {
        await using var factory = new NodeAppFactory();
        using var client = factory.CreateClient();

        var resp = await client.DeleteAsync("/api/v1/ports/nope");
        resp.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Lifecycle_on_an_unknown_id_returns_404()
    {
        await using var factory = new NodeAppFactory();
        using var client = factory.CreateClient();

        var resp = await client.PostAsJsonAsync("/api/v1/ports/nope/lifecycle", new { action = "up" });
        resp.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Lifecycle_down_then_up_toggles_enabled()
    {
        await using var factory = new NodeAppFactory();
        using var client = factory.CreateClient();

        // Add an enabled-by-config port, but pointed at a dead endpoint — it will fail to
        // come up under the WAF (no listener), which is fine: we assert the persisted
        // enabled flag, not the live transport state.
        (await client.PostAsJsonAsync("/api/v1/ports", KissTcpPort("vhf", "127.0.0.1", 8101, enabled: true)))
            .StatusCode.Should().Be(HttpStatusCode.OK);

        // down → enabled flips false.
        var down = await client.PostAsJsonAsync("/api/v1/ports/vhf/lifecycle", new { action = "down" });
        down.StatusCode.Should().Be(HttpStatusCode.OK);
        var afterDown = await down.Content.ReadFromJsonAsync<PortStatus>(Web);
        afterDown.Should().NotBeNull();
        afterDown!.Enabled.Should().BeFalse();
        afterDown.State.Should().Be("down");

        ConfiguredEnabled(await client.GetStringAsync("/api/v1/config"), "vhf").Should().BeFalse();

        // up → enabled flips back true (the live state may still read down/faulted while
        // the async reconcile runs — we assert the persisted enabled flag).
        var up = await client.PostAsJsonAsync("/api/v1/ports/vhf/lifecycle", new { action = "up" });
        up.StatusCode.Should().Be(HttpStatusCode.OK);
        var afterUp = await up.Content.ReadFromJsonAsync<PortStatus>(Web);
        afterUp.Should().NotBeNull();
        afterUp!.Enabled.Should().BeTrue();

        ConfiguredEnabled(await client.GetStringAsync("/api/v1/config"), "vhf").Should().BeTrue();
    }

    [Fact]
    public async Task Lifecycle_restart_is_deferred_with_a_501_and_a_message()
    {
        await using var factory = new NodeAppFactory();
        using var client = factory.CreateClient();

        (await client.PostAsJsonAsync("/api/v1/ports", KissTcpPort("vhf", "127.0.0.1", 8101)))
            .StatusCode.Should().Be(HttpStatusCode.OK);

        var resp = await client.PostAsJsonAsync("/api/v1/ports/vhf/lifecycle", new { action = "restart" });
        resp.StatusCode.Should().Be(HttpStatusCode.NotImplemented);

        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        doc.RootElement.GetProperty("error").GetString().Should().Contain("restart");
    }

    private static bool ConfiguredEnabled(string configJson, string id)
    {
        using var doc = JsonDocument.Parse(configJson);
        return doc.RootElement.GetProperty("ports").EnumerateArray()
            .Single(p => p.GetProperty("id").GetString() == id)
            .GetProperty("enabled").GetBoolean();
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
