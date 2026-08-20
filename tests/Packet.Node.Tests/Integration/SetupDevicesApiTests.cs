using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Packet.Node.Core.Api;
using Packet.Node.Core.Configuration;
using Packet.Node.Core.Modems;
using Packet.Node.Tests.Support;

namespace Packet.Node.Tests.Integration;

/// <summary>
/// <c>GET /api/v1/setup/devices</c> - the first-run wizard's modem picker.
/// </summary>
/// <remarks>
/// This endpoint is unauthenticated, because it is consumed before any account exists, and it
/// opens serial devices. Both facts make its <b>gate</b> the thing worth testing: it is open only
/// while the node is unclaimed (exactly the window <c>POST /setup</c> is open in) and 403 the
/// moment an admin exists. A fake scanner stands in for the real one so no test ever touches
/// serial hardware.
/// </remarks>
[Trait("Category", "Node")]
public sealed class SetupDevicesApiTests : IDisposable
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);
    private readonly string dir;

    public SetupDevicesApiTests()
    {
        dir = TestPaths.NewPath("packetnet-setupdevices");
        Directory.CreateDirectory(dir);
        var configPath = Path.Combine(dir, "node.yaml");
        File.WriteAllText(configPath, """
            schemaVersion: 1
            identity:
              callsign: N0CALL
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

    private sealed class FakeScanner : IModemScanner
    {
        public int Calls { get; private set; }

        public Task<ModemScan> ScanAsync(NodeConfig current, CancellationToken cancellationToken = default)
        {
            Calls++;
            return Task.FromResult(new ModemScan(
            [
                new ModemScanDevice(
                    "/dev/serial/by-id/usb-Microchip_Technology_Inc._NinoTNC-if00", "/dev/ttyACM0",
                    "usb-Microchip_Technology_Inc._NinoTNC-if00", "nino-tnc", "3.44", null, null),
            ], PermissionDenied: false));
        }
    }

    private static WebApplicationFactory<Program> Factory(IModemScanner scanner)
        => new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
            b.ConfigureTestServices(services => services.AddSingleton(scanner)));

    [Fact]
    public async Task An_unclaimed_node_serves_the_scan_without_a_token()
    {
        var scanner = new FakeScanner();
        await using var factory = Factory(scanner);
        using var client = factory.CreateClient();

        var resp = await client.GetAsync("/api/v1/setup/devices");
        resp.StatusCode.Should().Be(HttpStatusCode.OK);

        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        var device = doc.RootElement.GetProperty("devices").EnumerateArray().Single();
        device.GetProperty("kind").GetString().Should().Be("nino-tnc");
        device.GetProperty("firmwareVersion").GetString().Should().Be("3.44");
        // The wizard binds the port to devicePath, so it must be the stable by-id name.
        device.GetProperty("devicePath").GetString().Should().StartWith("/dev/serial/by-id/");
        doc.RootElement.GetProperty("permissionDenied").GetBoolean().Should().BeFalse();
        scanner.Calls.Should().Be(1);
    }

    [Fact]
    public async Task Once_the_node_is_claimed_the_endpoint_closes_and_stops_scanning()
    {
        // Same one-shot window as POST /setup. After the claim the same information is on the
        // Ports screen behind auth, so nothing is lost by closing this door - and leaving it open
        // would hand an unauthenticated caller a device inventory of the box for ever.
        var scanner = new FakeScanner();
        await using var factory = Factory(scanner);
        using var client = factory.CreateClient();

        var claim = await client.PostAsJsonAsync("/api/v1/setup", new
        {
            identity = new { callsign = "M0LTE-1" },
            admin = new { username = "admin", password = "hunter2hunter2" },
        }, Web);
        claim.StatusCode.Should().Be(HttpStatusCode.OK);

        var resp = await client.GetAsync("/api/v1/setup/devices");
        resp.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        // The gate is checked BEFORE the scan: a closed endpoint must not open serial ports.
        scanner.Calls.Should().Be(0);
    }

    public void Dispose()
    {
        GC.SuppressFinalize(this);
        Environment.SetEnvironmentVariable("PACKETNET_CONFIG", null);
        Environment.SetEnvironmentVariable("PACKETNET_DB", null);
        try { Directory.Delete(dir, recursive: true); } catch { /* best effort */ }
    }
}
