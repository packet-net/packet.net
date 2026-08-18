using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Packet.Node.Tests.Support;

namespace Packet.Node.Tests.Integration;

/// <summary>
/// Boots the real <c>Packet.Node</c> composition root and exercises the browser node-command-console
/// API (<see cref="Packet.Node.Api.PdnConsoleApi"/>): <c>POST /api/v1/console</c> (open a node command
/// shell over the in-process loopback bridge), <c>GET /api/v1/console/{id}/stream</c> (SSE output),
/// <c>POST /api/v1/console/{id}/input</c> (feed a command), <c>DELETE /api/v1/console/{id}</c> (close).
/// </summary>
/// <remarks>
/// Mirrors <see cref="SessionsApiTests"/> - a temp YAML config with no ports and telnet disabled (so
/// no fixed TCP port is bound under the WAF). Auth is off in this fixture, so the admin gate passes
/// through (the explicit admin-refused-when-auth-on assertion lives in the auth-enabled suites; the
/// gate is the same <see cref="Packet.Node.Api.PdnAuthPolicies.Admin"/> policy the MCP-token API uses,
/// which IS covered with auth on). These cover the real happy path end-to-end: the console's own
/// banner/prompt streams back, a typed command (<c>?</c> help) produces its output on the stream, and
/// an unknown / closed id 404s.
/// </remarks>
[Trait("Category", "Node")]
public sealed class ConsoleApiTests : IDisposable
{
    private const string Callsign = "M0LTE-1";
    private readonly string configPath;

    public ConsoleApiTests()
    {
        var dir = TestPaths.NewPath("packetnet-consoleapi");
        Directory.CreateDirectory(dir);
        configPath = Path.Combine(dir, "node.yaml");
        // No ports + telnet off: the WAF host binds no fixed TCP port. The node command console still
        // runs (banner/prompt + the read-only verbs); connect-out simply reports "not available".
        File.WriteAllText(configPath, $"""
            schemaVersion: 1
            identity:
              callsign: {Callsign}
              alias: LONDON
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

    private sealed class NodeAppFactory : WebApplicationFactory<Program>;

    [Fact]
    public async Task Open_returns_a_console_prefixed_id()
    {
        await using var factory = new NodeAppFactory();
        using var client = factory.CreateClient();

        using var resp = await client.PostAsync("/api/v1/console", content: null);
        resp.StatusCode.Should().Be(HttpStatusCode.OK);

        var doc = await resp.Content.ReadFromJsonAsync<JsonElement>();
        var id = doc.GetProperty("id").GetString();
        id.Should().NotBeNullOrWhiteSpace();
        id!.Should().StartWith("console:", "the console id is minted distinct from the portId-peer session ids");

        // Tidy up the running console so the host doesn't carry it to shutdown.
        await client.DeleteAsync($"/api/v1/console/{id}");
    }

    [Fact]
    public async Task Stream_of_an_unknown_console_returns_404()
    {
        await using var factory = new NodeAppFactory();
        using var client = factory.CreateClient();

        // No managed console for this id → 404 BEFORE any bytes are written (clean status).
        using var resp = await client.GetAsync("/api/v1/console/console:ghost/stream",
            HttpCompletionOption.ResponseHeadersRead);
        resp.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Input_to_an_unknown_console_returns_404()
    {
        await using var factory = new NodeAppFactory();
        using var client = factory.CreateClient();

        using var resp = await client.PostAsJsonAsync("/api/v1/console/console:ghost/input", new { data = "?\r" });
        resp.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Open_stream_command_close_round_trips_the_consoles_own_output()
    {
        await using var factory = new NodeAppFactory();
        using var client = factory.CreateClient();

        // 1) Open a console session.
        using var openResp = await client.PostAsync("/api/v1/console", content: null);
        openResp.StatusCode.Should().Be(HttpStatusCode.OK);
        var id = (await openResp.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString();
        id.Should().NotBeNullOrWhiteSpace();

        try
        {
            // 2) Open the SSE stream.
            using var streamResp = await client.GetAsync($"/api/v1/console/{id}/stream",
                HttpCompletionOption.ResponseHeadersRead);
            streamResp.StatusCode.Should().Be(HttpStatusCode.OK);
            streamResp.Content.Headers.ContentType!.MediaType.Should().Be("text/event-stream");

            await using var stream = await streamResp.Content.ReadAsStreamAsync();
            using var reader = new StreamReader(stream);

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));

            // 3) Drain until the banner (carrying the node alias) arrives - the console emits the
            //    banner + first prompt as its opening write, so this proves the bridge is live.
            (await DrainUntilDataContainsAsync(reader, "LONDON", cts.Token))
                .Should().BeTrue("the console's banner (with the node alias) must stream back over SSE");

            // 4) Type the help command (a CR-terminated line - the line discipline the console wants)
            //    and assert its output (the "Commands:" help block) arrives on the same stream.
            using var inputResp = await client.PostAsJsonAsync($"/api/v1/console/{id}/input", new { data = "?\r" });
            inputResp.StatusCode.Should().Be(HttpStatusCode.Accepted);

            (await DrainUntilDataContainsAsync(reader, "Commands:", cts.Token))
                .Should().BeTrue("the '?' help command's output must come back over the stream");
        }
        finally
        {
            // 5) Close: 204, and the running NodeCommandService is torn down (no leak).
            using var delResp = await client.DeleteAsync($"/api/v1/console/{id}");
            delResp.StatusCode.Should().Be(HttpStatusCode.NoContent);
        }
    }

    [Fact]
    public async Task The_replayed_backlog_is_a_backlog_event_and_live_output_is_an_output_event()
    {
        // The replay is sent on EVERY subscription - including the browser's silent EventSource
        // auto-reconnect - so it cannot share the `output` name with live chunks: a client that
        // appended both rewrote the whole history on each reconnect (review item C045, #689).
        await using var factory = new NodeAppFactory();
        using var client = factory.CreateClient();

        using var openResp = await client.PostAsync("/api/v1/console", content: null);
        openResp.StatusCode.Should().Be(HttpStatusCode.OK);
        var id = (await openResp.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString();

        try
        {
            using var streamResp = await client.GetAsync($"/api/v1/console/{id}/stream",
                HttpCompletionOption.ResponseHeadersRead);
            streamResp.StatusCode.Should().Be(HttpStatusCode.OK);

            await using var stream = await streamResp.Content.ReadAsStreamAsync();
            using var reader = new StreamReader(stream);
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));

            // The FIRST named event of the subscription is the replay, and it is `backlog`.
            (await NextEventNameAsync(reader, cts.Token)).Should().Be("backlog");

            // Everything after it is live: type the help command and read the next event name.
            using var inputResp = await client.PostAsJsonAsync($"/api/v1/console/{id}/input", new { data = "?\r" });
            inputResp.StatusCode.Should().Be(HttpStatusCode.Accepted);

            (await NextEventNameAsync(reader, cts.Token)).Should().Be("output");
        }
        finally
        {
            using var delResp = await client.DeleteAsync($"/api/v1/console/{id}");
            delResp.StatusCode.Should().Be(HttpStatusCode.NoContent);
        }
    }

    // The name of the next named SSE event on the stream (`event: <name>`), skipping heartbeat
    // comments and blank framing lines. Null if the stream ends or the token trips first.
    private static async Task<string?> NextEventNameAsync(StreamReader reader, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            var line = await reader.ReadLineAsync(ct);
            if (line is null)
            {
                return null;
            }
            const string EventPrefix = "event: ";
            if (line.StartsWith(EventPrefix, StringComparison.Ordinal))
            {
                return line[EventPrefix.Length..];
            }
        }
        return null;
    }

    // Read SSE `data:` lines until one (after JSON-decoding the chunk) contains the needle, or the
    // token trips. Each `output` event's data is a JSON-encoded string; we substring-match on the
    // decoded text so embedded CR/LF don't matter. Bounded so a regression fails fast, not hangs.
    private static async Task<bool> DrainUntilDataContainsAsync(StreamReader reader, string needle, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            var line = await reader.ReadLineAsync(ct);
            if (line is null)
            {
                return false;   // stream ended
            }
            const string DataPrefix = "data: ";
            if (!line.StartsWith(DataPrefix, StringComparison.Ordinal))
            {
                continue;
            }
            var json = line[DataPrefix.Length..];
            string? chunk;
            try
            {
                chunk = JsonSerializer.Deserialize<string>(json);
            }
            catch (JsonException)
            {
                continue;   // not a JSON-encoded string line (shouldn't happen) - skip
            }
            if (chunk is not null && chunk.Contains(needle, StringComparison.Ordinal))
            {
                return true;
            }
        }
        return false;
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
