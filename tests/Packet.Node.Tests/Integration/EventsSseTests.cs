using System.Net;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Packet.Ax25;
using Packet.Ax25.Session;
using Packet.Core;
using Packet.Node.Core.Hosting;

namespace Packet.Node.Tests.Integration;

/// <summary>
/// Boots the real <c>Packet.Node</c> composition root and exercises the Slice 3
/// step-1b live SSE feed (<c>GET /api/v1/events</c>) the web monitor's EventSource
/// consumes. Mirrors <see cref="ReadApiTests"/>'s temp-config / telnet-disabled
/// WAF setup, then drives a synthetic frame through the host's
/// <see cref="NodeHostedService.Telemetry"/> singleton and asserts it surfaces on
/// the stream as a named <c>frame</c> event whose <c>data:</c> JSON carries the
/// expected callsign + type.
/// </summary>
[Trait("Category", "Node")]
public sealed class EventsSseTests : IDisposable
{
    private readonly string configPath;

    public EventsSseTests()
    {
        var dir = Path.Combine(Path.GetTempPath(), "packetnet-sse-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        configPath = Path.Combine(dir, "node.yaml");
        File.WriteAllText(configPath, """
            schemaVersion: 1
            identity:
              callsign: M0LTE-1
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

    [Fact]
    public async Task Events_endpoint_returns_text_event_stream()
    {
        await using var factory = new NodeAppFactory();
        using var client = factory.CreateClient();

        using var resp = await client.GetAsync("/api/v1/events", HttpCompletionOption.ResponseHeadersRead);

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        resp.Content.Headers.ContentType!.MediaType.Should().Be("text/event-stream");
    }

    [Fact]
    public async Task Observed_frame_is_delivered_as_a_named_frame_SSE_event()
    {
        await using var factory = new NodeAppFactory();
        using var client = factory.CreateClient();

        // Resolve the live host so we can drive its telemetry tap directly (no modem).
        var host = factory.Services.GetRequiredService<NodeHostedService>();

        using var resp = await client.GetAsync("/api/v1/events", HttpCompletionOption.ResponseHeadersRead);
        resp.StatusCode.Should().Be(HttpStatusCode.OK);

        await using var stream = await resp.Content.ReadAsStreamAsync();
        using var reader = new StreamReader(stream);

        // Generous-but-bounded timeout: a regression that stops delivering frames
        // must fail fast, not hang CI. The stream never ends, so we read lines.
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));

        // Push a synthetic frame onto the tap once the stream is open. The peer
        // (G7XYZ-2) is the source on a Received frame.
        var dest = Callsign.Parse("M0LTE-1");
        var src = Callsign.Parse("G7XYZ-2");
        host.Telemetry.Observe("p1", new Ax25FrameEventArgs
        {
            Frame = Ax25Frame.Ui(dest, src, "hello"u8),
            Direction = FrameDirection.Received,
            Timestamp = new DateTimeOffset(2026, 6, 1, 12, 0, 0, TimeSpan.Zero),
        });

        // Drain lines until we see the named event and its data payload. The initial
        // ": connected" comment + any ": ping" heartbeats are skipped.
        bool sawFrameEvent = false;
        string? dataLine = null;
        while (!cts.IsCancellationRequested)
        {
            var line = await reader.ReadLineAsync(cts.Token);
            if (line is null)
            {
                break;
            }
            if (line == "event: frame")
            {
                sawFrameEvent = true;
            }
            else if (sawFrameEvent && line.StartsWith("data:", StringComparison.Ordinal))
            {
                dataLine = line;
                break;
            }
        }

        sawFrameEvent.Should().BeTrue("the observed frame should arrive as a named 'frame' SSE event");
        dataLine.Should().NotBeNull();
        // The camelCase JSON payload carries the decoded frame fields.
        dataLine.Should().Contain("\"type\":\"UI\"");
        dataLine.Should().Contain("\"source\":\"G7XYZ-2\"");
        dataLine.Should().Contain("\"portId\":\"p1\"");
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
