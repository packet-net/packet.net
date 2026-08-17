using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Packet.Node.Api;
using Packet.Node.Core.Console;
using Packet.Node.Tests.Support;

namespace Packet.Node.Tests.Integration;

/// <summary>
/// Boots the real <c>Packet.Node</c> composition root and exercises the Slice 3
/// session-action + ping API (step 4): <c>POST /api/v1/sessions</c> (connect-out),
/// <c>DELETE /api/v1/sessions/{id}</c> (disconnect), <c>POST /api/v1/sessions/{id}/send</c>
/// (send a line), and <c>POST /api/v1/ping</c>. Mirrors <see cref="PortsApiTests"/> — a
/// temp YAML config with no ports and telnet disabled (so no fixed TCP port is bound under
/// the WAF), the routing store in the same temp dir.
/// </summary>
/// <remarks>
/// The actual connect / send / disconnect against a real peer can't be WAF-tested (no
/// modem): the human live-verifies against GB7RDG. These cover the deterministically
/// reachable contract — the 4xx/5xx behaviours when there is no live session / no running
/// port / a bad target — plus ping's no-running-port 404 and the pure session-id split
/// helper (<see cref="SessionIdSplitTests"/>).
/// </remarks>
[Trait("Category", "Node")]
public sealed class SessionsApiTests : IDisposable
{
    private const string Callsign = "M0LTE-1";
    private readonly string configPath;

    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    public SessionsApiTests()
    {
        var dir = TestPaths.NewPath("packetnet-sessionsapi");
        Directory.CreateDirectory(dir);
        configPath = Path.Combine(dir, "node.yaml");
        // No ports + telnet off: the WAF host binds no fixed TCP port and has no running
        // listener — so every connect/disconnect/send hits its no-live-session path.
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
    public async Task Connect_with_no_running_port_returns_404()
    {
        await using var factory = new NodeAppFactory();
        using var client = factory.CreateClient();

        // A parseable target, but no port is up to dial it on.
        var resp = await client.PostAsJsonAsync("/api/v1/sessions", new { target = "GB7RDG-1" });
        resp.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Connect_with_an_unparseable_target_returns_400()
    {
        await using var factory = new NodeAppFactory();
        using var client = factory.CreateClient();

        // A target that can't be a callsign or alias (lowercase + symbols).
        var resp = await client.PostAsJsonAsync("/api/v1/sessions", new { target = "not a call!" });
        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Connect_with_a_missing_target_returns_400()
    {
        await using var factory = new NodeAppFactory();
        using var client = factory.CreateClient();

        var resp = await client.PostAsJsonAsync("/api/v1/sessions", new { target = "" });
        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Connect_naming_an_unknown_port_returns_404()
    {
        await using var factory = new NodeAppFactory();
        using var client = factory.CreateClient();

        var resp = await client.PostAsJsonAsync("/api/v1/sessions",
            new { target = "GB7RDG-1", portId = "ghost" });
        resp.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Disconnect_an_unknown_session_returns_404()
    {
        await using var factory = new NodeAppFactory();
        using var client = factory.CreateClient();

        var resp = await client.DeleteAsync("/api/v1/sessions/vhf:GB7RDG-1");
        resp.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Disconnect_a_malformed_session_id_returns_404()
    {
        await using var factory = new NodeAppFactory();
        using var client = factory.CreateClient();

        // No ':' → not a valid session id → no match → 404.
        var resp = await client.DeleteAsync("/api/v1/sessions/garbage");
        resp.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Send_to_an_unknown_session_returns_404()
    {
        await using var factory = new NodeAppFactory();
        using var client = factory.CreateClient();

        var resp = await client.PostAsJsonAsync("/api/v1/sessions/vhf:GB7RDG-1/send",
            new { line = "hello" });
        resp.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Ping_with_no_running_port_returns_404()
    {
        await using var factory = new NodeAppFactory();
        using var client = factory.CreateClient();

        // This fixture configures no ports, so 'vhf' names no running port — ping has no
        // listener to send the TEST command on. (The pinger's correlation core + validation
        // matrix are covered by AxPingerTests + PingApiTests.)
        var resp = await client.PostAsJsonAsync("/api/v1/ping",
            new { station = "GB7RDG-1", portId = "vhf", count = 3 });
        resp.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Stream_of_an_unknown_session_returns_404()
    {
        await using var factory = new NodeAppFactory();
        using var client = factory.CreateClient();

        // No managed console for this id → the endpoint 404s BEFORE it starts writing the
        // event stream (so this is a clean status, not a half-written body).
        var resp = await client.GetAsync("/api/v1/sessions/vhf:GB7RDG-1/stream",
            HttpCompletionOption.ResponseHeadersRead);
        resp.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Managed_session_streams_an_output_event_with_a_json_encoded_chunk()
    {
        await using var factory = new NodeAppFactory();
        using var client = factory.CreateClient();

        // Drive the manager directly (no modem): adopt a fake connection whose ReadAsync
        // yields one scripted chunk — containing a CR, which JSON-encoding must preserve as
        // \r inside the data: line rather than letting it break SSE framing — then blocks.
        var console = factory.Services.GetRequiredService<SysopConsoleManager>();
        const string Id = "vhf:GB7RDG-1";
        const string Chunk = "GB7RDG:RDG} \r";
        await using var fake = new ScriptedConnection(Chunk);
        console.Open(Id, fake);

        try
        {
            using var resp = await client.GetAsync($"/api/v1/sessions/{Id}/stream",
                HttpCompletionOption.ResponseHeadersRead);
            resp.StatusCode.Should().Be(HttpStatusCode.OK);
            resp.Content.Headers.ContentType!.MediaType.Should().Be("text/event-stream");

            await using var stream = await resp.Content.ReadAsStreamAsync();
            using var reader = new StreamReader(stream);

            // Bounded so a regression fails fast instead of hanging CI. The FIRST named event of
            // ANY subscription is the replayed history - a distinct `backlog` event (even when
            // empty), which is what lets a reconnecting client reset instead of appending a
            // second copy of the history (review item C045, #689). Live chunks follow as
            // `output`. WHICH of the two carries this chunk is a race here (the scripted
            // connection may drain into the backlog before the subscription lands), so the two
            // things are asserted separately; the backlog-then-output ordering itself is pinned
            // deterministically by ConsoleApiTests, which drives the identical stream shape.
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            var expectedData = "data: " + JsonSerializer.Serialize(Chunk);

            string? firstEventName = null;
            bool sawChunkData = false;
            while (!cts.IsCancellationRequested && !sawChunkData)
            {
                var line = await reader.ReadLineAsync(cts.Token);
                if (line is null)
                {
                    break;
                }
                const string EventPrefix = "event: ";
                if (line.StartsWith(EventPrefix, StringComparison.Ordinal))
                {
                    firstEventName ??= line[EventPrefix.Length..];
                }
                else if (line == expectedData)
                {
                    sawChunkData = true;
                }
            }

            firstEventName.Should().Be("backlog", "a subscription opens with the replayed history under its own event name");
            sawChunkData.Should().BeTrue(
                "the chunk should arrive JSON-encoded so its embedded CR survives SSE line framing");
        }
        finally
        {
            await console.CloseAsync(Id);
        }
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

    /// <summary>
    /// A test-double <see cref="INodeConnection"/>: <see cref="ReadAsync"/> yields one scripted
    /// chunk on its first call, then blocks until cancelled (mimicking a quiet peer that has
    /// sent its banner and is now idle). Writes are captured for assertion; disposing cancels
    /// the parked read so the manager's pump completes cleanly.
    /// </summary>
    private sealed class ScriptedConnection(string firstChunk) : INodeConnection
    {
        private readonly byte[] firstBytes = Encoding.UTF8.GetBytes(firstChunk);
        private readonly CancellationTokenSource closed = new();
        private readonly TaskCompletionSource completion =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int read;
        private int disposed;

        public string PeerId => "GB7RDG-1";
        public NodeTransportKind TransportKind => NodeTransportKind.Ax25;
        public Task Completion => completion.Task;

        public async ValueTask<ReadOnlyMemory<byte>> ReadAsync(CancellationToken cancellationToken = default)
        {
            if (Interlocked.Exchange(ref read, 1) == 0)
            {
                return firstBytes;   // one scripted chunk, then go quiet
            }
            // Park until the connection (or the caller) is cancelled — no more output.
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, closed.Token);
            try
            {
                await Task.Delay(Timeout.Infinite, linked.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Closed / cancelled — report EOF so the pump ends.
            }
            return ReadOnlyMemory<byte>.Empty;
        }

        public ValueTask WriteAsync(ReadOnlyMemory<byte> bytes, CancellationToken cancellationToken = default)
            => ValueTask.CompletedTask;

        public ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref disposed, 1) != 0)
            {
                return ValueTask.CompletedTask;   // idempotent: manager + test both dispose
            }
            closed.Cancel();
            closed.Dispose();
            completion.TrySetResult();
            return ValueTask.CompletedTask;
        }
    }
}

/// <summary>
/// Unit tests for the pure session-id split helper (<c>PdnSessionsApi.TrySplitSessionId</c>)
/// — the contract that mirrors <c>PdnReadApi.BuildSessions</c>'s <c>$"{portId}:{peer}"</c>
/// id minting. The peer (a callsign, possibly SSID'd) contains no ':', so a split on the
/// FIRST ':' is unambiguous.
/// </summary>
[Trait("Category", "Node")]
public sealed class SessionIdSplitTests
{
    [Theory]
    [InlineData("vhf:GB7RDG-1", "vhf", "GB7RDG-1")]
    [InlineData("link-dn:M0LTE", "link-dn", "M0LTE")]
    [InlineData("a:b", "a", "b")]
    public void Splits_at_the_first_colon(string id, string expectedPort, string expectedPeer)
    {
        PdnSessionsApi.TrySplitSessionId(id, out var port, out var peer).Should().BeTrue();
        port.Should().Be(expectedPort);
        peer.Should().Be(expectedPeer);
    }

    [Theory]
    [InlineData("")]
    [InlineData("noColon")]
    [InlineData(":leadingColon")]
    [InlineData("trailingColon:")]
    public void Rejects_ids_without_a_well_formed_split(string id)
    {
        PdnSessionsApi.TrySplitSessionId(id, out _, out _).Should().BeFalse();
    }
}

/// <summary>
/// Unit tests for the <see cref="SysopConsoleManager"/> the stream endpoint consumes,
/// driving it directly with a fake connection (no WAF / modem). These pin the two
/// behaviours the SSE contract leans on: a late subscriber sees the backlog the peer
/// already sent, and an unmanaged id yields no subscription (the endpoint's 404 case).
/// </summary>
[Trait("Category", "Node")]
public sealed class SysopConsoleManagerTests
{
    [Fact]
    public void Subscribe_to_an_unmanaged_id_returns_null()
    {
        var manager = new SysopConsoleManager(NullLogger<SysopConsoleManager>.Instance);
        var sub = manager.Subscribe("vhf:GB7RDG-1", out var backlog, out var reader);
        sub.Should().BeNull();
        reader.Should().BeNull();
        backlog.Should().BeEmpty();
    }

    [Fact]
    public async Task Late_subscriber_replays_the_backlog_the_peer_already_sent()
    {
        await using var manager = new SysopConsoleManager(NullLogger<SysopConsoleManager>.Instance);
        const string Id = "vhf:GB7RDG-1";
        const string Banner = "GB7RDG:RDG} Welcome\r";

        await using var fake = new ManagerScriptedConnection(Banner);
        manager.Open(Id, fake);

        // The pump runs on a background task; give it a bounded window to drain the one
        // scripted chunk into the backlog before we subscribe "late".
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        string backlog = string.Empty;
        IDisposable? sub = null;
        while (!cts.IsCancellationRequested)
        {
            sub?.Dispose();
            sub = manager.Subscribe(Id, out backlog, out _);
            sub.Should().NotBeNull("the session is managed");
            if (backlog.Contains(Banner, StringComparison.Ordinal))
            {
                break;
            }
            await Task.Delay(20, cts.Token);
        }

        sub?.Dispose();
        backlog.Should().Contain(Banner, "a late subscriber must see the output the peer already sent");
        await manager.CloseAsync(Id);
    }

    // Same shape as SessionsApiTests.ScriptedConnection, scoped to this manager-only test
    // class: yield one chunk, then block until disposed.
    private sealed class ManagerScriptedConnection(string firstChunk) : INodeConnection
    {
        private readonly byte[] firstBytes = Encoding.UTF8.GetBytes(firstChunk);
        private readonly CancellationTokenSource closed = new();
        private int read;
        private int disposed;

        public string PeerId => "GB7RDG-1";
        public NodeTransportKind TransportKind => NodeTransportKind.Ax25;
        public Task Completion => Task.CompletedTask;

        public async ValueTask<ReadOnlyMemory<byte>> ReadAsync(CancellationToken cancellationToken = default)
        {
            if (Interlocked.Exchange(ref read, 1) == 0)
            {
                return firstBytes;
            }
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, closed.Token);
            try
            {
                await Task.Delay(Timeout.Infinite, linked.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Closed — report EOF.
            }
            return ReadOnlyMemory<byte>.Empty;
        }

        public ValueTask WriteAsync(ReadOnlyMemory<byte> bytes, CancellationToken cancellationToken = default)
            => ValueTask.CompletedTask;

        public ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref disposed, 1) != 0)
            {
                return ValueTask.CompletedTask;   // idempotent: manager + test both dispose
            }
            closed.Cancel();
            closed.Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
