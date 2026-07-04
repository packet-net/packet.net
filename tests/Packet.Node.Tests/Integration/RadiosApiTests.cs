using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Packet.Node.Core.Api;
using Packet.Node.Core.Radios;

namespace Packet.Node.Tests.Integration;

/// <summary>
/// Boots the real composition root and exercises the radio-control read endpoints:
/// <c>GET /api/v1/radios</c>, <c>GET /api/v1/ports/{id}/radio</c>, and <c>GET /api/v1/radios/scan</c>
/// (with an injected fake scanner so no serial hardware is touched). The node has one radio-less
/// port, so the status endpoints exercise the "no radio / not attached" projections.
/// </summary>
[Trait("Category", "Node")]
public sealed class RadiosApiTests : IDisposable
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);
    private readonly string configPath;

    public RadiosApiTests()
    {
        var dir = Path.Combine(Path.GetTempPath(), "packetnet-radiosapi-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        configPath = Path.Combine(dir, "node.yaml");
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
                  port: 8199
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

    private sealed class FakeScanner : IRadioScanner
    {
        public Task<IReadOnlyList<RadioScanResult>> ScanAsync(
            IReadOnlyList<int>? baudRates = null, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<RadioScanResult>>(
            [
                new RadioScanResult("1G000123", "Tait TM8110", "CCDI-1.0", 28800,
                    "/dev/ttyUSB0", "/dev/serial/by-id/usb-Tait-if00"),
            ]);
    }

    [Fact]
    public async Task Radios_is_an_empty_array_when_no_port_has_a_radio()
    {
        await using var factory = new NodeAppFactory();
        using var client = factory.CreateClient();

        var resp = await client.GetAsync("/api/v1/radios");
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        JsonSerializer.Deserialize<JsonElement[]>(await resp.Content.ReadAsStringAsync(), Web)!
            .Should().BeEmpty();
    }

    [Fact]
    public async Task Port_radio_reports_not_attached_for_a_radio_less_port_and_404_for_an_unknown_port()
    {
        await using var factory = new NodeAppFactory();
        using var client = factory.CreateClient();

        var known = await client.GetAsync("/api/v1/ports/vhf/radio");
        known.StatusCode.Should().Be(HttpStatusCode.OK);
        using var doc = JsonDocument.Parse(await known.Content.ReadAsStringAsync());
        doc.RootElement.GetProperty("attached").GetBoolean().Should().BeFalse();

        var unknown = await client.GetAsync("/api/v1/ports/nope/radio");
        unknown.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Scan_returns_the_scanner_results_keyed_by_ccdi_serial()
    {
        await using var factory = new NodeAppFactory().WithWebHostBuilder(b =>
            b.ConfigureTestServices(services => services.AddSingleton<IRadioScanner>(new FakeScanner())));
        using var client = factory.CreateClient();

        var resp = await client.GetAsync("/api/v1/radios/scan");
        resp.StatusCode.Should().Be(HttpStatusCode.OK);

        var arr = JsonSerializer.Deserialize<JsonElement[]>(await resp.Content.ReadAsStringAsync(), Web)!;
        arr.Should().ContainSingle();
        arr[0].GetProperty("serial").GetString().Should().Be("1G000123");
        arr[0].GetProperty("devicePath").GetString().Should().Be("/dev/ttyUSB0");
        arr[0].GetProperty("byIdPath").GetString().Should().Be("/dev/serial/by-id/usb-Tait-if00");
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
