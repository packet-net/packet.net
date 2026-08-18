using System.Globalization;
using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Packet.Node.Core.Traffic;
using Packet.Node.Tests.Support;

namespace Packet.Node.Tests.Integration;

/// <summary>
/// Boots the real <c>Packet.Node</c> composition root and exercises
/// <c>GET /api/v1/traffic</c> (the persisted traffic log's read surface) plus the
/// <c>traffic</c> health block on <c>GET /api/v1/status</c>. Mirrors
/// <see cref="ReadApiTests"/> - a temp YAML config with telnet disabled, the db
/// paths pointed at the same temp dir - and pre-seeds the traffic store on disk so
/// the endpoint's row shape, ordering, and filters are asserted against real rows.
/// </summary>
[Trait("Category", "Node")]
public sealed class TrafficApiTests : IDisposable
{
    private const string Callsign = "M0LTE-1";
    private readonly string dir;
    private readonly string configPath;
    private readonly string trafficDbPath;

    public TrafficApiTests()
    {
        dir = TestPaths.NewPath("packetnet-trafficapi");
        Directory.CreateDirectory(dir);
        configPath = Path.Combine(dir, "node.yaml");
        trafficDbPath = Path.Combine(dir, "traffic.db");
        WriteConfig(trafficEnabled: true);
        Environment.SetEnvironmentVariable("PACKETNET_CONFIG", configPath);
        Environment.SetEnvironmentVariable("PACKETNET_DB", Path.Combine(dir, "pdn.db"));
    }

    private void WriteConfig(bool trafficEnabled)
        => File.WriteAllText(configPath, $"""
            schemaVersion: 1
            identity:
              callsign: {Callsign}
            ports: []
            management:
              auth:
                enabled: false
              telnet:
                enabled: false
              http:
                bind: 127.0.0.1
                port: 8080
            traffic:
              enabled: {(trafficEnabled ? "true" : "false")}
              path: {trafficDbPath}
            """);

    private sealed class NodeAppFactory : WebApplicationFactory<Program>
    {
        // Boots Program.Main's host; Kestrel is replaced by the in-memory TestServer.
    }

    // Recent wall-clock instants (the node's startup prune enforces the 14-day
    // retention against the real clock - fixed dates would age out of the fixture).
    private static readonly DateTimeOffset Base = DateTimeOffset.UtcNow.AddMinutes(-5);

    private static DateTimeOffset At(int seconds) => Base.AddSeconds(seconds);

    private void Seed()
        => new SqliteTrafficStore(trafficDbPath).Append(
        [
            new TrafficRecord(At(0), "vhf", "rx", "G7XYZ-2", Callsign, "SABM",
                Ns: null, Nr: null, Pf: 1, Control: 0x3F, Pid: null, InfoLength: 0, Raw: [0x9C, 0x94]),
            new TrafficRecord(At(1), "vhf", "tx", Callsign, "G7XYZ-2", "UA",
                Ns: null, Nr: null, Pf: 1, Control: 0x73, Pid: null, InfoLength: 0, Raw: [0x9C]),
            new TrafficRecord(At(2), "hf", "rx", "G7XYZ-2", Callsign, "I",
                Ns: 0, Nr: 0, Pf: 0, Control: 0x00, Pid: 0xF0, InfoLength: 2, Raw: [0x9C, 0x94, 0x61]),
        ]).Should().BeTrue();

    [Fact]
    public async Task Traffic_returns_seeded_rows_newest_first_in_the_wire_shape()
    {
        Seed();
        await using var factory = new NodeAppFactory();
        using var client = factory.CreateClient();

        var resp = await client.GetAsync("/api/v1/traffic");
        resp.StatusCode.Should().Be(HttpStatusCode.OK);

        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        var rows = doc.RootElement.EnumerateArray().ToArray();
        rows.Should().HaveCount(3);

        // Newest first; web-default camelCase property names.
        var newest = rows[0];
        newest.GetProperty("portId").GetString().Should().Be("hf");
        newest.GetProperty("direction").GetString().Should().Be("rx");
        newest.GetProperty("source").GetString().Should().Be("G7XYZ-2");
        newest.GetProperty("dest").GetString().Should().Be(Callsign);
        newest.GetProperty("kind").GetString().Should().Be("I");
        newest.GetProperty("ns").GetInt32().Should().Be(0);
        newest.GetProperty("nr").GetInt32().Should().Be(0);
        newest.GetProperty("pf").GetInt32().Should().Be(0);
        newest.GetProperty("control").GetInt32().Should().Be(0x00);
        newest.GetProperty("pid").GetInt32().Should().Be(0xF0);
        newest.GetProperty("infoLength").GetInt32().Should().Be(2);
        newest.GetProperty("raw").EnumerateArray().Select(e => e.GetInt32())
            .Should().Equal(0x9C, 0x94, 0x61);

        // The nullable columns serialise as JSON null for the U frames.
        rows[2].GetProperty("kind").GetString().Should().Be("SABM");
        rows[2].GetProperty("ns").ValueKind.Should().Be(JsonValueKind.Null);
        rows[2].GetProperty("pid").ValueKind.Should().Be(JsonValueKind.Null);
    }

    [Fact]
    public async Task Traffic_honours_port_time_and_limit_filters()
    {
        Seed();
        await using var factory = new NodeAppFactory();
        using var client = factory.CreateClient();

        // port filter.
        using (var doc = await GetJson(client, "/api/v1/traffic?port=vhf"))
        {
            doc.RootElement.GetArrayLength().Should().Be(2);
        }

        // limit clamps the count, newest kept.
        using (var doc = await GetJson(client, "/api/v1/traffic?limit=1"))
        {
            doc.RootElement.GetArrayLength().Should().Be(1);
            doc.RootElement[0].GetProperty("kind").GetString().Should().Be("I");
        }

        // since/until (inclusive UTC instants) bound the range to the middle row.
        string middle = Uri.EscapeDataString(At(1).UtcDateTime.ToString("o", CultureInfo.InvariantCulture));
        using (var doc = await GetJson(client, $"/api/v1/traffic?since={middle}&until={middle}"))
        {
            doc.RootElement.GetArrayLength().Should().Be(1);
            doc.RootElement[0].GetProperty("kind").GetString().Should().Be("UA");
        }
    }

    [Fact]
    public async Task Status_reports_the_traffic_log_enabled_with_no_drops()
    {
        await using var factory = new NodeAppFactory();
        using var client = factory.CreateClient();

        using var doc = await GetJson(client, "/api/v1/status");
        var traffic = doc.RootElement.GetProperty("traffic");
        traffic.GetProperty("enabled").GetBoolean().Should().BeTrue();
        traffic.GetProperty("dropped").GetInt64().Should().Be(0);
    }

    [Fact]
    public async Task Disabled_traffic_log_serves_an_empty_array_and_says_so_in_status()
    {
        Seed();   // rows exist on disk, but the store is not wired this boot.
        WriteConfig(trafficEnabled: false);
        await using var factory = new NodeAppFactory();
        using var client = factory.CreateClient();

        using (var doc = await GetJson(client, "/api/v1/traffic"))
        {
            doc.RootElement.ValueKind.Should().Be(JsonValueKind.Array);
            doc.RootElement.GetArrayLength().Should().Be(0);
        }

        using (var doc = await GetJson(client, "/api/v1/status"))
        {
            doc.RootElement.GetProperty("traffic").GetProperty("enabled").GetBoolean().Should().BeFalse();
        }
    }

    private static async Task<JsonDocument> GetJson(HttpClient client, string url)
    {
        var resp = await client.GetAsync(new Uri(url, UriKind.Relative));
        resp.StatusCode.Should().Be(HttpStatusCode.OK, "GET {0} should succeed", url);
        return JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
    }

    public void Dispose()
    {
        GC.SuppressFinalize(this);
        Environment.SetEnvironmentVariable("PACKETNET_CONFIG", null);
        Environment.SetEnvironmentVariable("PACKETNET_DB", null);
        try { Directory.Delete(dir, recursive: true); } catch { /* best effort */ }
    }
}
