using System.Text.Json.Nodes;
using Packet.Node.Core.Oarc;
using Xunit;

namespace Packet.Node.Tests.Oarc;

/// <summary>
/// Live validation of the OARC ingest path (#459) against the REAL collector
/// (<c>node-api.packet.oarc.uk</c>), using the synthetic callsign <c>Q0PDN</c> (a non-allocatable
/// Q-prefix, safe on the public map). Double-gated so it never runs unattended: it requires the
/// env var <c>OARC_LIVE_TEST=1</c> AND carries <c>Category=LiveExternal</c>. CI (which sets neither)
/// skips it; run it deliberately with
/// <c>OARC_LIVE_TEST=1 dotnet test --filter "FullyQualifiedName~OarcLiveIngest"</c>.
/// It reports the node up, reads it back from the network view, and marks it down again (courteous
/// cleanup). This is the on-the-wire proof behind the deterministic stub-based tests.
/// </summary>
[Trait("Category", "LiveExternal")]
public sealed class OarcLiveIngestTests
{
    private const string BaseUrl = "https://node-api.packet.oarc.uk/";
    private const string Callsign = "Q0PDN";

    private static bool Enabled => Environment.GetEnvironmentVariable("OARC_LIVE_TEST") == "1";

    private static HttpClient NewHttp() => new() { Timeout = TimeSpan.FromSeconds(20) };

    [SkippableFact]
    public async Task Reports_a_node_up_that_appears_on_the_map_then_marks_it_down()
    {
        Skip.IfNot(Enabled, "live OARC test (set OARC_LIVE_TEST=1 to run against the real collector)");

        using var http = NewHttp();
        var client = new OarcIngestClient(http);
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        var up = new OarcNodeUpEvent
        {
            Time = now,
            NodeCall = Callsign,
            NodeAlias = "PDNTST",
            Locator = "IO91wm",
            Software = "pdn",
            Version = "live-test",
        };

        var result = await client.ReportAsync(up, BaseUrl);
        result.Accepted.Should().BeTrue($"the collector should accept node-up (got {result.Outcome}/{result.StatusCode}: {result.Detail})");

        // The collector processes asynchronously (RabbitMQ); poll the read-back for a short while.
        JsonObject? node = null;
        for (var i = 0; i < 15 && node is null; i++)
        {
            await Task.Delay(TimeSpan.FromSeconds(1));
            node = await FetchNodeAsync(http, Callsign);
        }

        node.Should().NotBeNull("Q0PDN should appear in the network view within ~15s of node-up");
        ((string?)node!["callsign"]).Should().Be(Callsign);
        ((string?)node["locator"]).Should().Be("IO91wm");
        ((string?)node["software"]).Should().Be("pdn");

        // Courteous cleanup: mark the synthetic node down so it doesn't linger "up" on the public map.
        var down = new OarcNodeDownEvent
        {
            Time = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            NodeCall = Callsign,
            NodeAlias = "PDNTST",
            Reason = "live test complete",
        };
        (await client.ReportAsync(down, BaseUrl)).Accepted.Should().BeTrue();
    }

    [SkippableFact]
    public async Task A_link_status_lands_in_the_node_history()
    {
        Skip.IfNot(Enabled, "live OARC test (set OARC_LIVE_TEST=1 to run against the real collector)");

        using var http = NewHttp();
        var client = new OarcIngestClient(http);
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        var link = new OarcLinkStatusEvent
        {
            Time = now, Node = Callsign, Id = 1, Direction = "outgoing", Port = "1",
            Remote = "GB7RDG", Local = $"{Callsign}-1",
            FramesSent = 10, FramesReceived = 12, FramesResent = 1, FramesQueued = 0,
            BytesSent = 800, BytesReceived = 950, L2RttMs = 340,
        };

        (await client.ReportAsync(link, BaseUrl)).Accepted.Should().BeTrue();

        // It must surface in the node's event history (the L2 link store) within a short window.
        var landed = false;
        for (var i = 0; i < 15 && !landed; i++)
        {
            await Task.Delay(TimeSpan.FromSeconds(1));
            landed = await NodeHistoryHasLinkAsync(http, Callsign, remote: "GB7RDG");
        }
        landed.Should().BeTrue("the link-status should appear in Q0PDN's event history");
    }

    private static async Task<JsonObject?> FetchNodeAsync(HttpClient http, string call)
    {
        try
        {
            var json = await http.GetStringAsync($"{BaseUrl}api/network/nodes/base/{call}");
            var arr = JsonNode.Parse(json) as JsonArray;
            return arr is { Count: > 0 } ? arr[0] as JsonObject : null;
        }
        catch
        {
            return null;
        }
    }

    private static async Task<bool> NodeHistoryHasLinkAsync(HttpClient http, string call, string remote)
    {
        try
        {
            var json = await http.GetStringAsync($"{BaseUrl}api/history/events?node={call}&sortOrder=desc&limit=30");
            var data = (JsonNode.Parse(json) as JsonObject)?["data"] as JsonArray;
            return data is not null && data.Any(e =>
                e?["event"]?["remote"]?.GetValue<string>() == remote);
        }
        catch
        {
            return false;
        }
    }
}
