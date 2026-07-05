using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Packet.Node.Core.Api;
using Packet.Node.Core.Configuration;
using Packet.Node.Core.Radios;

namespace Packet.Node.Tests.Integration;

/// <summary>
/// Boots the real composition root and exercises the split-station head-end endpoints:
/// <c>GET /api/v1/radios/headends</c> (the scan/preview, driven by an injected fake scanner so no
/// real head-end is dialled) and <c>POST /api/v1/radios/headends/{instanceId}/adopt</c> (which
/// creates the matched port through the same validate→preview→apply seam the ports API uses).
/// </summary>
[Trait("Category", "Node")]
public sealed class HeadEndAdoptApiTests : IDisposable
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);
    private readonly string configPath;

    public HeadEndAdoptApiTests()
    {
        var dir = Path.Combine(Path.GetTempPath(), "packetnet-headendapi-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        configPath = Path.Combine(dir, "node.yaml");
        File.WriteAllText(configPath, """
            schemaVersion: 1
            identity:
              callsign: M0LTE-1
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

    private sealed class NodeAppFactory : WebApplicationFactory<Program>;

    // A canned fleet scan: one instance with a free NinoTNC + a free Tait and one auto-suggested pair.
    private sealed class FakeHeadEndScanner : IHeadEndRadioScanner
    {
        public Task<HeadEndScan> ScanAsync(NodeConfig config, CancellationToken cancellationToken = default) =>
            Task.FromResult(new HeadEndScan(
            [
                new HeadEndInstanceScan(
                    "pi-shack", "127.0.0.1", 7300, "mdns", Reachable: true, Error: null,
                    Devices:
                    [
                        new HeadEndDeviceScan("nino0", HeadEndDeviceKind.NinoTnc, null, "3.41", null, 57600, Free: true),
                        new HeadEndDeviceScan("tait0", HeadEndDeviceKind.TaitCcdi, "Tait TM8110", "03.02", "1G000123", 28800, Free: true),
                    ],
                    ProposedPairs: [new HeadEndPairProposal("nino0", "tait0", Auto: true)],
                    PairingAmbiguous: false),
            ],
            Conflicts: []));
    }

    // A canned keyup result: nino0 physically pairs with tait0.
    private sealed class FakeKeyupPairer : IHeadEndKeyupPairer
    {
        public Task<HeadEndKeyupResult> PairByKeyupAsync(NodeConfig config, string instanceId, CancellationToken cancellationToken = default) =>
            Task.FromResult(new HeadEndKeyupResult(
                instanceId, Reachable: true, Error: null,
                Pairs: [new HeadEndKeyupPair("nino0", "tait0")],
                UnpairedTncs: [], UnpairedRadios: [], Ambiguous: [], HeadEndKeyupCaveat.Text));
    }

    [Fact]
    public async Task Pair_by_keyup_returns_the_physical_pairs_and_the_rf_caveat()
    {
        await using var factory = new NodeAppFactory().WithWebHostBuilder(b =>
            b.ConfigureTestServices(s => s.AddSingleton<IHeadEndKeyupPairer>(new FakeKeyupPairer())));
        using var client = factory.CreateClient();

        var resp = await client.PostAsync("/api/v1/radios/headends/pi-shack/pair-by-keyup", content: null);
        resp.StatusCode.Should().Be(HttpStatusCode.OK);

        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        doc.RootElement.GetProperty("reachable").GetBoolean().Should().BeTrue();
        var pair = doc.RootElement.GetProperty("pairs")[0];
        pair.GetProperty("tncDeviceId").GetString().Should().Be("nino0");
        pair.GetProperty("radioDeviceId").GetString().Should().Be("tait0");
        // The RF caveat is always surfaced on the response.
        doc.RootElement.GetProperty("caveat").GetString().Should().Contain("RF");
    }

    [Fact]
    public async Task Headends_scan_returns_instances_devices_and_the_proposed_pair()
    {
        await using var factory = new NodeAppFactory().WithWebHostBuilder(b =>
            b.ConfigureTestServices(s => s.AddSingleton<IHeadEndRadioScanner>(new FakeHeadEndScanner())));
        using var client = factory.CreateClient();

        var resp = await client.GetAsync("/api/v1/radios/headends");
        resp.StatusCode.Should().Be(HttpStatusCode.OK);

        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        var instance = doc.RootElement.GetProperty("instances")[0];
        instance.GetProperty("instanceId").GetString().Should().Be("pi-shack");
        instance.GetProperty("devices").GetArrayLength().Should().Be(2);
        var pair = instance.GetProperty("proposedPairs")[0];
        pair.GetProperty("tncDeviceId").GetString().Should().Be("nino0");
        pair.GetProperty("radioDeviceId").GetString().Should().Be("tait0");
        pair.GetProperty("auto").GetBoolean().Should().BeTrue();
        doc.RootElement.GetProperty("conflicts").GetArrayLength().Should().Be(0);
    }

    [Fact]
    public async Task Adopt_creates_a_matched_nino_tnc_tcp_plus_tait_ccdi_port_through_the_config_seam()
    {
        await using var factory = new NodeAppFactory();
        using var client = factory.CreateClient();

        // enabled:false so the WAF host never dials the (non-existent) head-end — we assert the
        // config was created, not that the port came up.
        var body = new { tncDeviceId = "nino0", radioDeviceId = "tait0", mode = 6, enabled = false };
        var resp = await client.PostAsJsonAsync("/api/v1/radios/headends/pi-shack/adopt", body);
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var result = await resp.Content.ReadFromJsonAsync<ReconcileResult>(Web);
        result!.Applied.Should().BeTrue();

        // The port now exists.
        var ports = JsonSerializer.Deserialize<JsonElement[]>(await client.GetStringAsync("/api/v1/ports"), Web)!;
        ports.Should().ContainSingle(p => p.GetProperty("id").GetString() == "pi-shack");

        // The transport is nino-tnc-tcp and the port carries a head-end-bound tait-ccdi radio.
        using var cfg = JsonDocument.Parse(await client.GetStringAsync("/api/v1/config"));
        var port = cfg.RootElement.GetProperty("ports").EnumerateArray()
            .Single(p => p.GetProperty("id").GetString() == "pi-shack");
        port.GetProperty("transport").GetProperty("kind").GetString().Should().Be("nino-tnc-tcp");
        port.GetProperty("transport").GetProperty("deviceId").GetString().Should().Be("nino0");
        var radio = port.GetProperty("radio");
        radio.GetProperty("kind").GetString().Should().Be("tait-ccdi");
        radio.GetProperty("headEndId").GetString().Should().Be("pi-shack");
        radio.GetProperty("deviceId").GetString().Should().Be("tait0");

        // And the head-end was declared so the reference resolves (discover mode — blank address).
        cfg.RootElement.GetProperty("headEnds").EnumerateArray()
            .Should().ContainSingle(h => h.GetProperty("id").GetString() == "pi-shack");
    }

    [Fact]
    public async Task Adopt_rejects_a_body_missing_a_device_id()
    {
        await using var factory = new NodeAppFactory();
        using var client = factory.CreateClient();

        var resp = await client.PostAsJsonAsync("/api/v1/radios/headends/pi-shack/adopt",
            new { tncDeviceId = "nino0" }); // no radioDeviceId
        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
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
