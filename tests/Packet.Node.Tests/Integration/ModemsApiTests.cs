using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Packet.Kiss.NinoTnc;
using Packet.Node.Tests.Support;

namespace Packet.Node.Tests.Integration;

/// <summary>
/// Boots the real composition root and exercises the modem-catalogue read surface:
/// <c>GET /api/v1/modems/nino-tnc/modes</c>. The endpoint is entirely static - no device, no
/// serial I/O, no config - so no port is configured here and nothing is stubbed; the point is
/// that the node serves <see cref="NinoTncCatalog"/> itself.
/// </summary>
/// <remarks>
/// This exists because the web control panel used to carry its OWN hand-written NinoTNC mode
/// list and it was fiction: nine rows (0-8) with names that were not a NinoTNC's, so the Ports
/// editor's "Modem mode" picker offered e.g. "mode 5 - 9600 baud GFSK AX.25 (G3RUH)" and then
/// wrote 5 through to the TNC, where mode 5 is 3600 QPSK IL2P+CRC. Serving the researched
/// server-side table is the fix; these tests pin the wire shape the panel consumes, and the
/// contract fixture (<c>ClientContractFixtureTests</c>) pins the panel's offline fallback to
/// the same 16 rows.
/// </remarks>
[Trait("Category", "Node")]
public sealed class ModemsApiTests : IDisposable
{
    private readonly string configPath;

    public ModemsApiTests()
    {
        var dir = TestPaths.NewPath("packetnet-modemsapi");
        Directory.CreateDirectory(dir);
        configPath = Path.Combine(dir, "node.yaml");
        File.WriteAllText(configPath, """
            schemaVersion: 1
            identity:
              callsign: M0LTE-1
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
    public async Task Nino_modes_serves_the_whole_DIP_switch_table()
    {
        await using var factory = new NodeAppFactory();
        using var client = factory.CreateClient();

        var resp = await client.GetAsync("/api/v1/modems/nino-tnc/modes");
        resp.StatusCode.Should().Be(HttpStatusCode.OK);

        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        var modes = doc.RootElement.GetProperty("modes").EnumerateArray().ToArray();

        // All 16 DIP positions, in switch order - the picker renders them as-is.
        modes.Should().HaveCount(16);
        modes.Select(m => m.GetProperty("mode").GetInt32()).Should().Equal(Enumerable.Range(0, 16));

        // Mode 5 spelled out, because getting exactly this one wrong is what prompted the
        // endpoint: a NinoTNC's mode 5 is 3600 QPSK IL2P+CRC, not any flavour of 9600 GFSK.
        var five = modes[5];
        five.GetProperty("name").GetString().Should().Be("3600 QPSK IL2P+CRC");
        five.GetProperty("bitRateHz").GetInt32().Should().Be(3600);
        five.GetProperty("requiresWideChannel").GetBoolean().Should().BeFalse();

        // The wide-channel flag rides along so the editor need not keep its own copy of the
        // rule: modes 0/1/2 are the 25 kHz ones (NinoTncCatalog.WideChannelModes).
        modes.Where(m => m.GetProperty("requiresWideChannel").GetBoolean())
             .Select(m => m.GetProperty("mode").GetInt32())
             .Should().Equal(0, 1, 2);

        // Mode 15 is the SETHW escape - variable rate, so 0 bits per second is the honest answer.
        modes[15].GetProperty("name").GetString().Should().Be("Set from KISS");
        modes[15].GetProperty("bitRateHz").GetInt32().Should().Be(0);
    }

    [Fact]
    public async Task Nino_modes_is_the_catalogue_itself_not_a_second_copy_of_it()
    {
        await using var factory = new NodeAppFactory();
        using var client = factory.CreateClient();

        using var doc = JsonDocument.Parse(
            await client.GetStringAsync("/api/v1/modems/nino-tnc/modes"));

        // Every row must equal NinoTncCatalog's, name for name and rate for rate. A hand-typed
        // table in the API layer would be the same mistake the UI made, one layer down.
        foreach (var row in doc.RootElement.GetProperty("modes").EnumerateArray())
        {
            var mode = (byte)row.GetProperty("mode").GetInt32();
            var expected = NinoTncCatalog.ByMode[mode];
            row.GetProperty("name").GetString().Should().Be(expected.Name);
            row.GetProperty("bitRateHz").GetInt32().Should().Be(expected.BitRateHz);
            row.GetProperty("requiresWideChannel").GetBoolean()
                .Should().Be(NinoTncCatalog.RequiresWideChannel(mode));
        }
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("PACKETNET_CONFIG", null);
        Environment.SetEnvironmentVariable("PACKETNET_DB", null);
        try { File.Delete(configPath); } catch (IOException) { /* best effort */ }
    }
}
