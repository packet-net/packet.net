using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Packet.Node.Core.Rigs;
using Packet.Node.Core.Transports;
using Packet.Node.Tests.Support;

namespace Packet.Node.Tests.Integration;

/// <summary>
/// The rig mutation surface end to end through the composition root: an enabled port with a
/// fake transport + fake rig (both DI-injected through <c>NodeHostedService</c>'s optional ctor
/// seams) attaches for real, then POST set-frequency/set-mode drive the live
/// <c>IRigControl</c>, read back the result, and the guard paths (unknown port / bad body /
/// configured-but-not-attached) map to 404/400/409.
/// </summary>
[Trait("Category", "Node")]
public sealed class RigMutationApiTests : IDisposable
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);
    private readonly FakeRigControl rig = new() { FrequencyHz = 14_074_000, ModeToken = "USB", PassbandHz = 2400 };

    public RigMutationApiTests()
    {
        var dir = Path.Combine(Path.GetTempPath(), "packetnet-rigmut-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var configPath = Path.Combine(dir, "node.yaml");
        File.WriteAllText(configPath, """
            schemaVersion: 1
            identity:
              callsign: M0LTE-1
            ports:
              - id: hf
                enabled: true
                transport:
                  kind: serial-kiss
                  device: /dev/pty-hf
                rig:
                  kind: hamlib
              - id: cold
                enabled: false
                transport:
                  kind: kiss-tcp
                  host: 127.0.0.1
                  port: 8198
                rig:
                  kind: hamlib
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

    private sealed class NodeAppFactory(FakeRigControl rig) : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(Microsoft.AspNetCore.Hosting.IWebHostBuilder builder)
            => builder.ConfigureTestServices(services =>
            {
                var bus = new SharedRadioBus();
                services.AddSingleton<ITransportFactory>(
                    new FakeTransportFactory().Provide("serial-kiss:/dev/pty-hf", bus.Attach()));
                services.AddSingleton<IRigControlFactory>(new FakeRigControlFactory().Provide(rig));
            });
    }

    private static async Task WaitForAttachedAsync(HttpClient client)
    {
        for (var i = 0; i < 100; i++)
        {
            var status = await client.GetFromJsonAsync<JsonElement>("/api/v1/ports/hf/rig", Web);
            if (status.GetProperty("attached").GetBoolean())
            {
                return;
            }
            await Task.Delay(50);
        }
        throw new InvalidOperationException("the hf rig never attached");
    }

    [Fact]
    public async Task Set_frequency_drives_the_rig_and_returns_the_read_back()
    {
        await using var factory = new NodeAppFactory(rig);
        using var client = factory.CreateClient();
        await WaitForAttachedAsync(client);

        var resp = await client.PostAsJsonAsync("/api/v1/ports/hf/rig/frequency", new { frequencyHz = 7_074_000 }, Web);

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>(Web);
        body.GetProperty("frequencyHz").GetInt64().Should().Be(7_074_000);
        rig.FrequencyHz.Should().Be(7_074_000);
    }

    [Fact]
    public async Task Set_mode_drives_the_rig_and_returns_the_read_back()
    {
        await using var factory = new NodeAppFactory(rig);
        using var client = factory.CreateClient();
        await WaitForAttachedAsync(client);

        var resp = await client.PostAsJsonAsync("/api/v1/ports/hf/rig/mode", new { mode = "pktusb", passbandHz = 3000 }, Web);

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>(Web);
        body.GetProperty("mode").GetString().Should().Be("PKTUSB"); // normalised token
        body.GetProperty("passbandHz").GetInt32().Should().Be(3000);
        rig.ModeToken.Should().Be("PKTUSB");
    }

    [Fact]
    public async Task Guard_paths_map_to_404_400_and_409()
    {
        await using var factory = new NodeAppFactory(rig);
        using var client = factory.CreateClient();
        await WaitForAttachedAsync(client);

        // Unknown port → 404.
        (await client.PostAsJsonAsync("/api/v1/ports/nope/rig/frequency", new { frequencyHz = 1 }, Web))
            .StatusCode.Should().Be(HttpStatusCode.NotFound);

        // Bad bodies → 400.
        (await client.PostAsJsonAsync("/api/v1/ports/hf/rig/frequency", new { frequencyHz = 0 }, Web))
            .StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await client.PostAsJsonAsync("/api/v1/ports/hf/rig/mode", new { mode = "" }, Web))
            .StatusCode.Should().Be(HttpStatusCode.BadRequest);

        // Configured but not attached (the disabled port) → 409 with the honest error.
        var cold = await client.PostAsJsonAsync("/api/v1/ports/cold/rig/frequency", new { frequencyHz = 7_074_000 }, Web);
        cold.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await cold.Content.ReadFromJsonAsync<JsonElement>(Web))
            .GetProperty("error").GetString().Should().Contain("not attached");
    }

    [Fact]
    public async Task A_capability_the_rig_does_not_advertise_is_refused_without_touching_it()
    {
        // Tait-shaped slice: no FrequencySet.
        rig.GetType(); // keep the shared fixture rig for other tests; use a fresh restricted one here
        var restricted = new FakeRigControl
        {
            Capabilities = Packet.Rig.RigCapabilities.PttGet | Packet.Rig.RigCapabilities.RfPowerMeter,
        };
        await using var factory = new NodeAppFactory(restricted);
        using var client = factory.CreateClient();
        await WaitForAttachedAsync(client);

        var resp = await client.PostAsJsonAsync("/api/v1/ports/hf/rig/frequency", new { frequencyHz = 7_074_000 }, Web);

        resp.StatusCode.Should().Be(HttpStatusCode.Conflict);
        restricted.FrequencyHz.Should().Be(14_074_000, "the set must never reach a rig that doesn't advertise it");
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("PACKETNET_CONFIG", null);
        Environment.SetEnvironmentVariable("PACKETNET_DB", null);
    }
}
