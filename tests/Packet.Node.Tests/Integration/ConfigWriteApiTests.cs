using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Mvc.Testing;
using Packet.Node.Core.Api;
using Packet.Node.Core.Configuration;
using Packet.Node.Tests.Support;

namespace Packet.Node.Tests.Integration;

/// <summary>
/// Boots the real <c>Packet.Node</c> composition root and exercises the Slice 3
/// write API (step 2): <c>PUT /api/v1/config</c> + its dry-run, the 422 validation
/// path, and the raw-YAML round-trip. Mirrors <see cref="ReadApiTests"/> - a temp
/// YAML config with telnet disabled (so no fixed TCP port is bound under the WAF)
/// and the routing store pointed at the same temp dir. The config file is writable
/// in the temp dir, so a successful PUT persists through the live
/// <c>FileConfigProvider</c> and a follow-up GET reflects it.
/// </summary>
[Trait("Category", "Node")]
public sealed class ConfigWriteApiTests : IDisposable
{
    private const string Callsign = "M0LTE-1";
    private readonly string configPath;

    // The WAF HttpClient's default JSON knows none of the config dialect (the
    // polymorphic TransportConfig union, string enums, second-valued durations), so a
    // GET /config response we want to deserialise to NodeConfig (to tweak + PUT back)
    // uses the canonical option set - the very same one Program.cs hands the binder.
    private static readonly JsonSerializerOptions Web = NodeConfigJson.CreateOptions();

    public ConfigWriteApiTests()
    {
        var dir = TestPaths.NewPath("packetnet-cfgwriteapi");
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

    private static async Task<NodeConfig> GetConfigAsync(HttpClient client)
    {
        var json = await client.GetStringAsync("/api/v1/config");
        var config = JsonSerializer.Deserialize<NodeConfig>(json, Web);
        config.Should().NotBeNull();
        return config!;
    }

    [Fact]
    public async Task Put_config_applies_a_benign_change_and_a_follow_up_get_reflects_it()
    {
        await using var factory = new NodeAppFactory();
        using var client = factory.CreateClient();

        var current = await GetConfigAsync(client);
        var candidate = current with { Identity = current.Identity with { Grid = "JO01aa" } };

        var resp = await client.PutAsync("/api/v1/config",
            JsonContent.Create(candidate, options: Web));
        resp.StatusCode.Should().Be(HttpStatusCode.OK);

        var result = await resp.Content.ReadFromJsonAsync<ReconcileResult>(Web);
        result.Should().NotBeNull();
        result!.Applied.Should().BeTrue();

        var after = await GetConfigAsync(client);
        after.Identity.Grid.Should().Be("JO01aa");
    }

    [Fact]
    public async Task Put_config_dry_run_previews_without_applying()
    {
        await using var factory = new NodeAppFactory();
        using var client = factory.CreateClient();

        var current = await GetConfigAsync(client);
        var candidate = current with { Identity = current.Identity with { Grid = "JO01aa" } };

        var resp = await client.PutAsync("/api/v1/config?dryRun=true",
            JsonContent.Create(candidate, options: Web));
        resp.StatusCode.Should().Be(HttpStatusCode.OK);

        var result = await resp.Content.ReadFromJsonAsync<ReconcileResult>(Web);
        result.Should().NotBeNull();
        result!.Applied.Should().BeFalse();

        // A dry-run must not have touched the live config.
        var after = await GetConfigAsync(client);
        after.Identity.Grid.Should().Be(current.Identity.Grid);
    }

    [Fact]
    public async Task Put_config_with_an_invalid_candidate_returns_422()
    {
        await using var factory = new NodeAppFactory();
        using var client = factory.CreateClient();

        var current = await GetConfigAsync(client);
        var invalid = current with { Identity = current.Identity with { Callsign = "" } };

        var resp = await client.PutAsync("/api/v1/config",
            JsonContent.Create(invalid, options: Web));
        resp.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);

        var problem = await resp.Content.ReadFromJsonAsync<ValidationProblem>(Web);
        problem.Should().NotBeNull();
        problem!.Errors.Should().NotBeEmpty();
    }

    [Fact]
    public async Task Get_config_serialises_netrom_enums_as_strings_and_inp3_durations_as_seconds()
    {
        await using var factory = new NodeAppFactory();
        using var client = factory.CreateClient();

        // Read the raw text, not a typed model: the point of the test is the wire shape
        // the control panel and docs/node-api.yaml are written against.
        using var doc = JsonDocument.Parse(await client.GetStringAsync("/api/v1/config"));
        var netRom = doc.RootElement.GetProperty("netRom");

        netRom.GetProperty("forwardMode").ValueKind.Should().Be(JsonValueKind.String);
        netRom.GetProperty("forwardMode").GetString().Should().Be("PerFlow");
        netRom.GetProperty("effectiveRouting").ValueKind.Should().Be(JsonValueKind.String);

        var inp3 = netRom.GetProperty("inp3");
        inp3.GetProperty("l3RttInterval").ValueKind.Should().Be(JsonValueKind.Number);
        inp3.GetProperty("l3RttInterval").GetInt32().Should().Be(60);
        inp3.GetProperty("l3RttResetWindow").GetInt32().Should().Be(180);
        inp3.GetProperty("rifInterval").GetInt32().Should().Be(300);
        inp3.GetProperty("positiveDebounce").GetInt32().Should().Be(5);
    }

    [Fact]
    public async Task Put_config_accepts_the_documented_string_enum_and_seconds_duration_and_round_trips_them()
    {
        await using var factory = new NodeAppFactory();
        using var client = factory.CreateClient();

        // Edit the live config as TEXT, in the dialect docs/node-api.yaml documents and
        // the panel's config screen posts: routing as a name, an INP3 timer as seconds.
        var node = JsonNode.Parse(await client.GetStringAsync("/api/v1/config"))!.AsObject();
        var netRom = node["netRom"]!.AsObject();
        netRom["routing"] = "Transit";
        netRom["inp3"]!["l3RttInterval"] = 120;

        var resp = await client.PutAsync("/api/v1/config",
            new StringContent(node.ToJsonString(), Encoding.UTF8, "application/json"));

        // The regression this guards: an int-only enum binder threw a JsonException in
        // body binding, so the documented body came back as an empty 400.
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var result = await resp.Content.ReadFromJsonAsync<ReconcileResult>(Web);
        result.Should().NotBeNull();
        result!.Applied.Should().BeTrue();

        using var after = JsonDocument.Parse(await client.GetStringAsync("/api/v1/config"));
        var savedNetRom = after.RootElement.GetProperty("netRom");
        savedNetRom.GetProperty("routing").GetString().Should().Be("Transit");
        savedNetRom.GetProperty("inp3").GetProperty("l3RttInterval").GetInt32().Should().Be(120);
    }

    [Fact]
    public async Task Get_config_raw_serves_yaml_containing_the_callsign()
    {
        await using var factory = new NodeAppFactory();
        using var client = factory.CreateClient();

        var resp = await client.GetAsync("/api/v1/config/raw");
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        resp.Content.Headers.ContentType!.MediaType.Should().Be("text/plain");

        var yaml = await resp.Content.ReadAsStringAsync();
        yaml.Should().Contain(Callsign);
    }

    [Fact]
    public async Task Put_config_raw_applies_an_edited_yaml_and_rejects_malformed_yaml()
    {
        await using var factory = new NodeAppFactory();
        using var client = factory.CreateClient();

        // Take the live YAML and tweak the grid by appending it under identity.
        var yaml = await client.GetStringAsync("/api/v1/config/raw");
        var edited = yaml.Replace("alias: LONDON", "alias: LONDON\n  grid: JO02bb", StringComparison.Ordinal);
        edited.Should().Contain("JO02bb");   // guard: the edit landed in the text

        var ok = await client.PutAsync("/api/v1/config/raw",
            new StringContent(edited, Encoding.UTF8, "text/plain"));
        ok.StatusCode.Should().Be(HttpStatusCode.OK);
        var result = await ok.Content.ReadFromJsonAsync<ReconcileResult>(Web);
        result.Should().NotBeNull();
        result!.Applied.Should().BeTrue();

        var after = await GetConfigAsync(client);
        after.Identity.Grid.Should().Be("JO02bb");

        // Malformed YAML → 422 (the parse failure surfaces as a (yaml)-path error).
        var bad = await client.PutAsync("/api/v1/config/raw",
            new StringContent(":::not yaml", Encoding.UTF8, "text/plain"));
        bad.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
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
