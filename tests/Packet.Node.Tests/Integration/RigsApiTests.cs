using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace Packet.Node.Tests.Integration;

/// <summary>
/// Boots the real composition root and exercises the rig-control read surface:
/// <c>GET /api/v1/rigs</c>, <c>GET /api/v1/ports/{id}/rig</c>, and the <c>event: rig</c> SSE
/// feed at <c>GET /api/v1/rigs/events</c>. Ports stay disabled (no transports are opened) —
/// the endpoints exercise the configured-but-not-attached projections, and the SSE test drives
/// the live <c>RigTelemetry</c> hub directly through the composition root.
/// </summary>
[Trait("Category", "Node")]
public sealed class RigsApiTests : IDisposable
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);
    private readonly string configPath;

    public RigsApiTests()
    {
        var dir = Path.Combine(Path.GetTempPath(), "packetnet-rigsapi-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        configPath = Path.Combine(dir, "node.yaml");
        File.WriteAllText(configPath, """
            schemaVersion: 1
            identity:
              callsign: M0LTE-1
            ports:
              - id: hf
                enabled: false
                transport:
                  kind: kiss-tcp
                  host: 127.0.0.1
                  port: 8199
                rig:
                  kind: hamlib
              - id: bare
                enabled: false
                transport:
                  kind: kiss-tcp
                  host: 127.0.0.1
                  port: 8198
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
    public async Task Rigs_lists_the_configured_rig_as_not_attached_when_its_port_is_down()
    {
        await using var factory = new NodeAppFactory();
        using var client = factory.CreateClient();

        var resp = await client.GetAsync("/api/v1/rigs");
        resp.StatusCode.Should().Be(HttpStatusCode.OK);

        var rigs = JsonSerializer.Deserialize<JsonElement[]>(await resp.Content.ReadAsStringAsync(), Web)!;
        rigs.Should().ContainSingle("only the hf port has a rig block");
        rigs[0].GetProperty("portId").GetString().Should().Be("hf");
        rigs[0].GetProperty("attached").GetBoolean().Should().BeFalse();
        rigs[0].GetProperty("kind").GetString().Should().Be("hamlib");
        rigs[0].GetProperty("endpoint").GetString().Should().Be("127.0.0.1:4532", "the kind default resolves");
        rigs[0].GetProperty("connectionState").GetString().Should().Be("unknown");
    }

    [Fact]
    public async Task Port_rig_distinguishes_no_rig_from_no_such_port()
    {
        await using var factory = new NodeAppFactory();
        using var client = factory.CreateClient();

        var bare = await client.GetAsync("/api/v1/ports/bare/rig");
        bare.StatusCode.Should().Be(HttpStatusCode.OK);
        JsonSerializer.Deserialize<JsonElement>(await bare.Content.ReadAsStringAsync(), Web)
            .GetProperty("kind").GetString().Should().BeEmpty("an honest 'this port has no rig'");

        var missing = await client.GetAsync("/api/v1/ports/nope/rig");
        missing.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Rigs_events_streams_a_rig_event_when_a_poll_tick_publishes()
    {
        await using var factory = new NodeAppFactory();
        using var client = factory.CreateClient();

        // Reach the live hub through the composition root — the same object the SSE endpoint
        // subscribes to — and publish a status as a poll tick would.
        var host = factory.Services.GetRequiredService<Packet.Node.Core.Hosting.NodeHostedService>();

        using var req = new HttpRequestMessage(HttpMethod.Get, "/api/v1/rigs/events");
        using var resp = await client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead);
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        resp.Content.Headers.ContentType!.MediaType.Should().Be("text/event-stream");

        using var stream = await resp.Content.ReadAsStreamAsync();
        using var reader = new StreamReader(stream);

        (await reader.ReadLineAsync()).Should().Be(": connected");

        host.RigTelemetry.Publish(new Packet.Node.Core.Api.RigStatus(
            PortId: "hf", Attached: true, Kind: "hamlib", Endpoint: "127.0.0.1:4532",
            Backend: "Hamlib rigctld", Manufacturer: "Hamlib", Model: "Dummy",
            Capabilities: ["frequencyGet"], ConnectionState: "healthy",
            FrequencyHz: 14_074_000, Mode: "PKTUSB", PassbandHz: 3000,
            Transmitting: false, Meters: null, SampledAt: DateTimeOffset.UnixEpoch));

        // Skip blank keep-alive separators until the event line lands.
        string? line;
        do
        {
            line = await reader.ReadLineAsync();
        }
        while (line is not null && line.Length == 0);

        line.Should().Be("event: rig");
        var data = await reader.ReadLineAsync();
        data.Should().StartWith("data: ");
        var status = JsonSerializer.Deserialize<JsonElement>(data!["data: ".Length..], Web);
        status.GetProperty("portId").GetString().Should().Be("hf");
        status.GetProperty("frequencyHz").GetInt64().Should().Be(14_074_000);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("PACKETNET_CONFIG", null);
        Environment.SetEnvironmentVariable("PACKETNET_DB", null);
    }
}
