using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Packet.Node.Core.Api;
using Packet.Node.Core.Audit;
using Packet.Node.Tests.Support;

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
        var dir = TestPaths.NewPath("packetnet-portsapi");
        Directory.CreateDirectory(dir);
        configPath = Path.Combine(dir, "node.yaml");
        File.WriteAllText(configPath, $"""
            schemaVersion: 1
            identity:
              callsign: {Callsign}
              alias: LONDON
            ports: []
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
    public async Task Port_writes_are_recorded_in_the_node_audit_log()
    {
        // The §6 promise on the REST surface: privileged port writes are attributable in
        // pdn.db (actor/source/action/target), not just MCP writes. Auth is off in this
        // test config, so the actor is the local "owner".
        await using var factory = new NodeAppFactory();
        using var client = factory.CreateClient();

        (await client.PostAsJsonAsync("/api/v1/ports", KissTcpPort("vhf", "127.0.0.1", 8101)))
            .StatusCode.Should().Be(HttpStatusCode.OK);
        (await client.PostAsJsonAsync("/api/v1/ports/vhf/lifecycle", new { action = "down" }))
            .EnsureSuccessStatusCode();

        var recent = factory.Services.GetRequiredService<IAuditLog>().Recent(50);
        recent.Should().Contain(e => e.Action == "add_port" && e.Target == "vhf" && e.Source == "rest");
        recent.Should().Contain(e =>
            e.Action == "port_lifecycle" && e.Target == "vhf" && e.Detail.Contains("down", StringComparison.Ordinal));
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

    // #672 — the shape of the failure matters as much as the failure. A KISS knob outside
    // the byte the wire carries used to fail JSON model binding (the record typed them
    // byte?), so the API answered a bare 400 with nothing naming the field: the operator
    // who wrote 300 thinking in milliseconds got no clue which of ten fields was wrong.
    // It is now an ordinary 422 ValidationProblem like every other bad value.
    [Fact]
    public async Task Post_an_out_of_range_kiss_param_returns_422_naming_the_field()
    {
        await using var factory = new NodeAppFactory();
        using var client = factory.CreateClient();

        var body = new
        {
            id = "2m-1",
            enabled = false,
            transport = new { kind = "kiss-tcp", host = "127.0.0.1", port = 8101 },
            kiss = new { txDelay = 300, slotTime = 100, txTail = 50, persistence = 63 },
        };

        var resp = await client.PostAsJsonAsync("/api/v1/ports", body);

        resp.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity,
            "an out-of-range value is a validation failure, not an unparseable request");

        var problem = await resp.Content.ReadFromJsonAsync<ValidationProblem>(Web);
        problem.Should().NotBeNull();
        problem!.Errors.Should().NotBeEmpty();
        string.Join(" ", problem.Errors.Select(e => e.Message))
            .Should().Contain("kiss.txDelay").And.Contain("0..255").And.Contain("10 ms");

        // And the port was not created.
        (await GetPortsAsync(client)).Should().BeEmpty();
    }

    [Fact]
    public async Task Post_an_in_range_kiss_param_is_accepted()
    {
        await using var factory = new NodeAppFactory();
        using var client = factory.CreateClient();

        // The same intent expressed correctly: 300 ms of TX delay is 30 wire units.
        var body = new
        {
            id = "2m-1",
            enabled = false,
            transport = new { kind = "kiss-tcp", host = "127.0.0.1", port = 8101 },
            kiss = new { txDelay = 30, slotTime = 10, txTail = 5, persistence = 63 },
        };

        (await client.PostAsJsonAsync("/api/v1/ports", body)).StatusCode.Should().Be(HttpStatusCode.OK);
        (await GetPortsAsync(client)).Should().ContainSingle();
    }

    [Fact]
    public async Task Post_dry_run_previews_without_adding_the_port()
    {
        await using var factory = new NodeAppFactory();
        using var client = factory.CreateClient();

        var resp = await client.PostAsJsonAsync("/api/v1/ports?dryRun=true", KissTcpPort("vhf", "127.0.0.1", 8101));
        resp.StatusCode.Should().Be(HttpStatusCode.OK);

        var result = await resp.Content.ReadFromJsonAsync<ReconcileResult>(Web);
        result.Should().NotBeNull();
        result!.Valid.Should().BeTrue();
        result.Applied.Should().BeFalse();

        // Nothing was persisted - the preview is what the web port editor asks for before the
        // operator commits, so it must not touch the node.
        (await GetPortsAsync(client)).Should().BeEmpty();
    }

    [Fact]
    public async Task Put_dry_run_previews_the_restart_class_without_applying_the_edit()
    {
        await using var factory = new NodeAppFactory();
        using var client = factory.CreateClient();

        // An ENABLED port, so a transport change is classified rather than subsumed by the
        // enabled-toggle arm. kiss-tcp to a closed local port stays down; that is fine here.
        (await client.PostAsJsonAsync("/api/v1/ports", KissTcpPort("vhf", "127.0.0.1", 8101, enabled: true)))
            .StatusCode.Should().Be(HttpStatusCode.OK);

        var resp = await client.PutAsJsonAsync("/api/v1/ports/vhf?dryRun=true", KissTcpPort("vhf", "127.0.0.1", 8102, enabled: true));
        resp.StatusCode.Should().Be(HttpStatusCode.OK);

        var result = await resp.Content.ReadFromJsonAsync<ReconcileResult>(Web);
        result!.Applied.Should().BeFalse();
        // A transport change is a single-port restart (ReconcilePlanner) - the classification the
        // editor's confirmation prompt is written from.
        result.PortRestart.Should().NotBeEmpty();

        // The live config still carries the ORIGINAL transport port.
        using var doc = JsonDocument.Parse(await client.GetStringAsync("/api/v1/config"));
        var vhf = doc.RootElement.GetProperty("ports").EnumerateArray()
            .Single(p => p.GetProperty("id").GetString() == "vhf");
        vhf.GetProperty("transport").GetProperty("port").GetInt32().Should().Be(8101);
    }

    [Fact]
    public async Task Dry_run_of_an_invalid_port_returns_422_without_touching_the_node()
    {
        await using var factory = new NodeAppFactory();
        using var client = factory.CreateClient();

        // An unknown channel profile - the rule that rejected every port the web editor created
        // while it sent a UI catalogue id as `profile` (#690 C002).
        var resp = await client.PostAsJsonAsync(
            "/api/v1/ports?dryRun=true",
            new { id = "vhf", enabled = false, profile = "vhf-fm-1200", transport = new { kind = "kiss-tcp", host = "127.0.0.1", port = 8101 } });

        resp.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
        (await GetPortsAsync(client)).Should().BeEmpty();
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
    public async Task Lifecycle_restart_on_an_unknown_id_returns_404()
    {
        await using var factory = new NodeAppFactory();
        using var client = factory.CreateClient();

        var resp = await client.PostAsJsonAsync("/api/v1/ports/nope/lifecycle", new { action = "restart" });
        resp.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Lifecycle_restart_on_a_disabled_port_returns_409()
    {
        await using var factory = new NodeAppFactory();
        using var client = factory.CreateClient();

        // Disabled port (the default in KissTcpPort) — RestartPortAsync returns false for a
        // disabled port, which the endpoint maps to 409 (bring it up before restarting).
        (await client.PostAsJsonAsync("/api/v1/ports", KissTcpPort("vhf", "127.0.0.1", 8101, enabled: false)))
            .StatusCode.Should().Be(HttpStatusCode.OK);

        var resp = await client.PostAsJsonAsync("/api/v1/ports/vhf/lifecycle", new { action = "restart" });
        resp.StatusCode.Should().Be(HttpStatusCode.Conflict);

        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        doc.RootElement.GetProperty("error").GetString().Should().Contain("disabled");
    }

    [Fact]
    public async Task Lifecycle_restart_on_an_enabled_port_returns_its_port_status()
    {
        await using var factory = new NodeAppFactory();
        using var client = factory.CreateClient();

        // Enabled but pointed at a dead endpoint — RestartPortAsync still returns true (the
        // port is configured + enabled; the transient bring-up faults under the WAF, which is
        // fine: we assert the endpoint applied the restart and returned the port's status,
        // not a live transport state).
        (await client.PostAsJsonAsync("/api/v1/ports", KissTcpPort("vhf", "127.0.0.1", 8101, enabled: true)))
            .StatusCode.Should().Be(HttpStatusCode.OK);

        var resp = await client.PostAsJsonAsync("/api/v1/ports/vhf/lifecycle", new { action = "restart" });
        resp.StatusCode.Should().Be(HttpStatusCode.OK);

        var status = await resp.Content.ReadFromJsonAsync<PortStatus>(Web);
        status.Should().NotBeNull();
        status!.Id.Should().Be("vhf");
        status.Enabled.Should().BeTrue();
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
            if (dir is not null)
            {
                Directory.Delete(dir, recursive: true);
            }
        }
        catch { /* best effort */ }
    }
}
