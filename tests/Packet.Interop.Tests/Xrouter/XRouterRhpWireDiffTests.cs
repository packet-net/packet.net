using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Packet.Node.Core.Console;
using Packet.Rhp2;
using Packet.Rhp2.Server;
using Xunit;

namespace Packet.Interop.Tests.Xrouter;

/// <summary>
/// The R-4 conformance diff: drive the <b>real XRouter's</b> RHPv2 server (the interop stack's
/// <c>ghcr.io/packethacking/xrouter</c>, <c>RHPPORT=9000</c> → host 127.0.0.1:8900) and
/// <b>pdn's</b> <see cref="RhpServer"/> with the <em>same</em> protocol-level scenarios, and
/// compare the wire shapes. XRouter is the only complete RHPv2 implementation, so this is the
/// independent oracle — anything it disagrees with us about is either a bug here or a new row
/// for <c>docs/rhp2-server.md</c>'s tables, never silent. Scenarios are deliberately
/// protocol-shape only (no radio behaviour), so they are deterministic against both servers.
/// </summary>
[Trait("Category", "Interop")]
public sealed class XRouterRhpWireDiffTests : IAsyncDisposable
{
    private const string XRouterHost = "127.0.0.1";
    private const int XRouterRhpPort = 8900;
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(15);

    private readonly List<IAsyncDisposable> cleanup = [];

    public async ValueTask DisposeAsync()
    {
        foreach (var d in cleanup)
        {
            await d.DisposeAsync();
        }
    }

    // ── plumbing ──────────────────────────────────────────────────────────

    private sealed class WireClient(TcpClient tcp) : IAsyncDisposable
    {
        private readonly NetworkStream stream = tcp.GetStream();

        public static async Task<WireClient> ConnectAsync(string host, int port)
        {
            var tcp = new TcpClient();
            using var cts = new CancellationTokenSource(Timeout);
            await tcp.ConnectAsync(host, port, cts.Token);
            return new WireClient(tcp);
        }

        public Task SendAsync(string json) => RhpFraming.WriteFrameAsync(stream, Encoding.UTF8.GetBytes(json));

        public async Task<string> ReadAsync()
        {
            using var cts = new CancellationTokenSource(Timeout);
            var frame = await RhpFraming.ReadFrameAsync(stream, cts.Token)
                ?? throw new InvalidOperationException("server closed the connection");
            return Encoding.UTF8.GetString(frame);
        }

        public ValueTask DisposeAsync()
        {
            stream.Dispose();
            tcp.Dispose();
            return ValueTask.CompletedTask;
        }
    }

    private async Task<WireClient> XRouterAsync()
        => Track(await WireClient.ConnectAsync(XRouterHost, XRouterRhpPort));

    private async Task<WireClient> PdnAsync()
    {
        var server = new RhpServer(
            new RhpServerOptions { Bind = IPAddress.Loopback, Port = 0 },
            new NullGateway());
        cleanup.Add(server);
        await server.StartAsync();
        return Track(await WireClient.ConnectAsync("127.0.0.1", server.BoundEndpoint!.Port));
    }

    private WireClient Track(WireClient c)
    {
        cleanup.Add(c);
        return c;
    }

    // Protocol-shape scenarios never reach the radio; this stand-in mirrors the REAL gateway's
    // contracts (SupervisorRhpGateway): opens fail NoRoute, and one listener per callsign —
    // a second registration raises the wire's Duplicate (9), exactly like the supervisor.
    private sealed class NullGateway : IRhpGateway
    {
        private readonly HashSet<string> listening = new(StringComparer.OrdinalIgnoreCase);

        public Task<INodeConnection> OpenAx25StreamAsync(string? portLabel, string? local, string remote, CancellationToken ct = default)
            => throw new RhpGatewayException(RhpErrorCode.NoRoute, "No Route");

        public IDisposable RegisterListener(string? portLabel, string local, Func<INodeConnection, string, Task> onAccepted)
        {
            lock (listening)
            {
                if (!listening.Add(local))
                {
                    throw new RhpGatewayException(RhpErrorCode.DuplicateSocket, $"callsign {local} is already registered.");
                }
            }
            return new Unsub(this, local);
        }

        // Dgram (UI) is off the radio for these protocol-shape scenarios: TX no-ops and the RX tap
        // never fires. (The dgram wire shapes are pinned in Packet.Rhp2.Tests, not the live diff.)
        public Task SendUiAsync(string? portLabel, string local, string remote, ReadOnlyMemory<byte> info, byte pid, CancellationToken ct = default)
            => Task.CompletedTask;

        public IDisposable RegisterUiListener(string? portLabel, Func<UiDatagram, Task> onReceived) => new NoopDisposable();

        private sealed class NoopDisposable : IDisposable
        {
            public void Dispose() { }
        }

        private sealed class Unsub(NullGateway owner, string local) : IDisposable
        {
            public void Dispose()
            {
                lock (owner.listening)
                {
                    owner.listening.Remove(local);
                }
            }
        }
    }

    // The shape we diff: the discriminator, the errCode value, whether the request id was
    // echoed, and the exact error-field casing — the contracts a client keys off.
    private static (string type, int? errCode, int? id, bool capitalErrCode, bool lowercaseErrCode) Shape(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        return (
            root.GetProperty("type").GetString()!,
            root.TryGetProperty("errCode", out var ec) ? ec.GetInt32()
                : root.TryGetProperty("errcode", out var lc) ? lc.GetInt32() : null,
            root.TryGetProperty("id", out var id) ? id.GetInt32() : null,
            json.Contains("\"errCode\"", StringComparison.Ordinal),
            json.Contains("\"errcode\"", StringComparison.Ordinal));
    }

    private static async Task<((string, int?, int?, bool, bool) xr, (string, int?, int?, bool, bool) pdn)> DiffAsync(
        WireClient xrouter, WireClient pdn, string request)
    {
        await xrouter.SendAsync(request);
        await pdn.SendAsync(request);
        return (Shape(await xrouter.ReadAsync()), Shape(await pdn.ReadAsync()));
    }

    // ── the diffs ─────────────────────────────────────────────────────────

    [Fact]
    public async Task Unknown_type_is_answered_identically()
    {
        var (xr, pdn) = await DiffAsync(await XRouterAsync(), await PdnAsync(),
            """{"type":"pnConformanceProbe","id":42}""");

        Assert.Equal(xr, pdn);                                  // type/errCode/id-echo/casing all match
        Assert.Equal("pnConformanceProbeReply", xr.Item1);      // and the shape is the known wire contract
        Assert.Equal(RhpErrorCode.BadOrMissingType, xr.Item2);
        Assert.Equal(42, xr.Item3);
    }

    [Fact]
    public async Task Send_on_a_bogus_handle_is_answered_identically()
    {
        var (xr, pdn) = await DiffAsync(await XRouterAsync(), await PdnAsync(),
            """{"type":"send","id":7,"handle":99999,"data":"x"}""");

        Assert.Equal(xr, pdn);
        Assert.Equal("sendReply", xr.Item1);
        Assert.Equal(RhpErrorCode.InvalidHandle, xr.Item2);
    }

    [Fact]
    public async Task Close_on_a_bogus_handle_is_answered_identically()
    {
        var (xr, pdn) = await DiffAsync(await XRouterAsync(), await PdnAsync(),
            """{"type":"close","id":8,"handle":99999}""");

        Assert.Equal(xr, pdn);
        Assert.Equal("closeReply", xr.Item1);
        Assert.Equal(RhpErrorCode.InvalidHandle, xr.Item2);
    }

    [Fact]
    public async Task Socket_bind_listen_lifecycle_shapes_match()
    {
        var xrouter = await XRouterAsync();
        var pdn = await PdnAsync();

        // socket(ax25, stream) — both allocate a handle with errCode 0, capital casing.
        var (xrSock, pdnSock) = await DiffAsync(xrouter, pdn,
            """{"type":"socket","id":1,"pfam":"ax25","mode":"stream"}""");
        Assert.Equal(("socketReply", (int?)RhpErrorCode.Ok, (int?)1, true, false), xrSock);
        Assert.Equal(xrSock, pdnSock);

        // The allocated handles differ numerically — extract each side's own.
        var xrHandle = await HandleOf(xrouter, """{"type":"socket","id":2,"pfam":"ax25","mode":"stream"}""");
        var pdnHandle = await HandleOf(pdn, """{"type":"socket","id":2,"pfam":"ax25","mode":"stream"}""");

        // bind a distinct test callsign on each (PN9TST won't collide with the stack's nodes).
        var (xrBind, pdnBind) = (
            Shape(await Send(xrouter, $$"""{"type":"bind","id":3,"handle":{{xrHandle}},"local":"PN9TST-1"}""")),
            Shape(await Send(pdn, $$"""{"type":"bind","id":3,"handle":{{pdnHandle}},"local":"PN9TST-1"}""")));
        Assert.Equal(("bindReply", (int?)RhpErrorCode.Ok, (int?)3, true, false), xrBind);
        Assert.Equal(xrBind, pdnBind);

        // listen — both Ok.
        var (xrListen, pdnListen) = (
            Shape(await Send(xrouter, $$"""{"type":"listen","id":4,"handle":{{xrHandle}},"flags":0}""")),
            Shape(await Send(pdn, $$"""{"type":"listen","id":4,"handle":{{pdnHandle}},"flags":0}""")));
        Assert.Equal(("listenReply", (int?)RhpErrorCode.Ok, (int?)4, true, false), xrListen);
        Assert.Equal(xrListen, pdnListen);

        // Re-listen on the SAME socket: idempotent Ok on both (observed XRouter wire — the
        // diff oracle corrected our initial 9 here).
        var (xrAgain, pdnAgain) = (
            Shape(await Send(xrouter, $$"""{"type":"listen","id":5,"handle":{{xrHandle}},"flags":0}""")),
            Shape(await Send(pdn, $$"""{"type":"listen","id":5,"handle":{{pdnHandle}},"flags":0}""")));
        Assert.Equal(xrAgain, pdnAgain);
        Assert.Equal(RhpErrorCode.Ok, xrAgain.Item2);

        // A SECOND SOCKET claiming the same callsign — a DELIBERATE divergence (deviation D5,
        // docs/rhp2-server.md): the live XRouter (this pinned build, null-port binds) answers
        // Ok, leaving accept routing AMBIGUOUS between two listeners; pdn refuses the second
        // with 9 ("Duplicate socket" — the spec's own error) so accepts are deterministic.
        // Asserted on BOTH sides so a change in either behaviour surfaces here, never silently.
        var xrHandle2 = await HandleOf(xrouter, """{"type":"socket","id":6,"pfam":"ax25","mode":"stream"}""");
        var pdnHandle2 = await HandleOf(pdn, """{"type":"socket","id":6,"pfam":"ax25","mode":"stream"}""");
        _ = await Send(xrouter, $$"""{"type":"bind","id":7,"handle":{{xrHandle2}},"local":"PN9TST-1"}""");
        _ = await Send(pdn, $$"""{"type":"bind","id":7,"handle":{{pdnHandle2}},"local":"PN9TST-1"}""");
        var (xrDup, pdnDup) = (
            Shape(await Send(xrouter, $$"""{"type":"listen","id":8,"handle":{{xrHandle2}},"flags":0}""")),
            Shape(await Send(pdn, $$"""{"type":"listen","id":8,"handle":{{pdnHandle2}},"flags":0}""")));
        Assert.Equal(RhpErrorCode.Ok, xrDup.Item2);             // the observed XRouter wire
        Assert.Equal(RhpErrorCode.DuplicateSocket, pdnDup.Item2); // pdn's deliberate D5
        Assert.Equal(8, xrDup.Item3);
        Assert.Equal(8, pdnDup.Item3);
    }

    [Fact]
    public async Task Bad_family_open_is_answered_identically()
    {
        var (xr, pdn) = await DiffAsync(await XRouterAsync(), await PdnAsync(),
            """{"type":"open","id":9,"pfam":"warble","mode":"stream","remote":"PN9TST","flags":128}""");

        Assert.Equal(xr, pdn);
        Assert.Equal(RhpErrorCode.BadOrMissingFamily, xr.Item2);
    }

    // ── R-5: the field-notes / RHPTEST conformance sweep ──────────────────

    [Fact]
    public async Task Idless_success_requests_are_still_replied_to_without_an_id()
    {
        // The spec says a server only replies ON ERROR when id is omitted; the live wire
        // replies to everything, success included — the id is simply absent from the
        // reply. pdn matches the wire (wire-fidelity row 10, docs/rhp2-server.md).
        var (xr, pdn) = await DiffAsync(await XRouterAsync(), await PdnAsync(),
            """{"type":"socket","pfam":"ax25","mode":"stream"}""");

        Assert.Equal(xr, pdn);
        Assert.Equal("socketReply", xr.Item1);
        Assert.Equal(RhpErrorCode.Ok, xr.Item2);
        Assert.Null(xr.Item3);                              // replied, but no id to echo
    }

    [Fact]
    public async Task Idless_unknown_type_is_answered_without_an_id_identically()
    {
        var (xr, pdn) = await DiffAsync(await XRouterAsync(), await PdnAsync(),
            """{"type":"pnIdlessProbe"}""");

        Assert.Equal(xr, pdn);
        Assert.Equal("pnIdlessProbeReply", xr.Item1);
        Assert.Equal(RhpErrorCode.BadOrMissingType, xr.Item2);
        Assert.Null(xr.Item3);
    }

    [Theory]
    [InlineData("""{"type":"close","id":11}""", "closeReply")]
    [InlineData("""{"type":"send","id":12,"data":"x"}""", "sendReply")]
    [InlineData("""{"type":"bind","id":13,"local":"PN9TST-8"}""", "bindReply")]
    [InlineData("""{"type":"listen","id":14,"flags":0}""", "listenReply")]
    [InlineData("""{"type":"connect","id":15,"remote":"PN9TST"}""", "connectReply")]
    [InlineData("""{"type":"status","id":16}""", "statusReply")]
    [InlineData("""{"type":"sendto","id":17,"data":"x"}""", "sendtoReply")]
    public async Task An_absent_handle_is_bad_parameter_12_on_both_sides(string request, string expectedReplyType)
    {
        // RHPTEST: a missing handle is 12, "not 3 — 3 is for handles that are well-formed
        // but unknown". Verified per-op against the live wire (errText "Missing handle").
        var (xr, pdn) = await DiffAsync(await XRouterAsync(), await PdnAsync(), request);

        Assert.Equal(xr, pdn);
        Assert.Equal(expectedReplyType, xr.Item1);
        Assert.Equal(RhpErrorCode.BadParameter, xr.Item2);
    }

    [Fact]
    public async Task Send_with_data_absent_on_a_fresh_socket_is_bad_parameter_12_on_both_sides()
    {
        // RHPTEST: send.data is mandatory even when empty — absence is a protocol
        // violation (12); the live wire answers errText "Missing data".
        var xrouter = await XRouterAsync();
        var pdn = await PdnAsync();
        var xrHandle = await HandleOf(xrouter, """{"type":"socket","id":1,"pfam":"ax25","mode":"stream"}""");
        var pdnHandle = await HandleOf(pdn, """{"type":"socket","id":1,"pfam":"ax25","mode":"stream"}""");

        var (xr, pdnShape) = (
            Shape(await Send(xrouter, $$"""{"type":"send","id":2,"handle":{{xrHandle}}}""")),
            Shape(await Send(pdn, $$"""{"type":"send","id":2,"handle":{{pdnHandle}}}""")));

        Assert.Equal(xr, pdnShape);
        Assert.Equal("sendReply", xr.Item1);
        Assert.Equal(RhpErrorCode.BadParameter, xr.Item2);
    }

    [Fact]
    public async Task Send_on_a_plain_socket_handle_is_not_connected_17_on_both_sides()
    {
        // The other half of the 16/17 split: a non-listening unconnected socket handle.
        var xrouter = await XRouterAsync();
        var pdn = await PdnAsync();
        var xrHandle = await HandleOf(xrouter, """{"type":"socket","id":1,"pfam":"ax25","mode":"stream"}""");
        var pdnHandle = await HandleOf(pdn, """{"type":"socket","id":1,"pfam":"ax25","mode":"stream"}""");

        var (xr, pdnShape) = (
            Shape(await Send(xrouter, $$"""{"type":"send","id":2,"handle":{{xrHandle}},"data":"x"}""")),
            Shape(await Send(pdn, $$"""{"type":"send","id":2,"handle":{{pdnHandle}},"data":"x"}""")));

        Assert.Equal(xr, pdnShape);
        Assert.Equal("sendReply", xr.Item1);
        Assert.Equal(RhpErrorCode.NotConnected, xr.Item2);
    }

    [Fact]
    public async Task Send_on_a_listening_socket_xrouter_answers_17_pdn_answers_16_deliberately()
    {
        // A DELIBERATE divergence (deviation D9, docs/rhp2-server.md): RHPTEST — the
        // protocol author's own harness, asserted against XRouter v505d — says a listener
        // rejects everything but accept/close with 16; the pinned live container (image
        // label 505c) still answers 17, indistinguishable from a plain unconnected socket.
        // pdn implements the author's documented intent. Both sides asserted explicitly so
        // a change in either (e.g. a 505d+ container bump) surfaces here, never silently.
        var xrouter = await XRouterAsync();
        var pdn = await PdnAsync();
        var xrHandle = await HandleOf(xrouter, """{"type":"socket","id":1,"pfam":"ax25","mode":"stream"}""");
        var pdnHandle = await HandleOf(pdn, """{"type":"socket","id":1,"pfam":"ax25","mode":"stream"}""");
        _ = await Send(xrouter, $$"""{"type":"bind","id":2,"handle":{{xrHandle}},"local":"PN9TST-9"}""");
        _ = await Send(pdn, $$"""{"type":"bind","id":2,"handle":{{pdnHandle}},"local":"PN9TST-9"}""");
        _ = await Send(xrouter, $$"""{"type":"listen","id":3,"handle":{{xrHandle}},"flags":0}""");
        _ = await Send(pdn, $$"""{"type":"listen","id":3,"handle":{{pdnHandle}},"flags":0}""");

        var xr = Shape(await Send(xrouter, $$"""{"type":"send","id":4,"handle":{{xrHandle}},"data":"x"}"""));
        var pdnShape = Shape(await Send(pdn, $$"""{"type":"send","id":4,"handle":{{pdnHandle}},"data":"x"}"""));

        Assert.Equal(RhpErrorCode.NotConnected, xr.Item2);            // the observed 505c wire
        Assert.Equal(RhpErrorCode.OperationNotSupported, pdnShape.Item2);   // pdn's deliberate D9 (RHPTEST/v505d intent)
        Assert.Equal(4, xr.Item3);
        Assert.Equal(4, pdnShape.Item3);

        // Tidy: close the XRouter listener so nothing lingers for later scenarios.
        _ = await Send(xrouter, $$"""{"type":"close","id":5,"handle":{{xrHandle}}}""");
    }

    [Fact]
    public async Task Alphabetic_ssid_bind_xrouter_accepts_pdn_refuses_6_deliberately()
    {
        // A DELIBERATE divergence (deviation D7, docs/rhp2-server.md): XRouter accepts a
        // bind to the invalid callsign G9DUM-S — and a subsequent connect from it "Ok"s,
        // then wedges the node in background SABM retries (rhp2lib field notes). pdn
        // refuses the bind itself with a clean 6. NOTE: bind-only on the XRouter side —
        // deliberately no connect — so this scenario can never wedge the shared container.
        var xrouter = await XRouterAsync();
        var pdn = await PdnAsync();
        var xrHandle = await HandleOf(xrouter, """{"type":"socket","id":1,"pfam":"ax25","mode":"stream"}""");
        var pdnHandle = await HandleOf(pdn, """{"type":"socket","id":1,"pfam":"ax25","mode":"stream"}""");

        var xr = Shape(await Send(xrouter, $$"""{"type":"bind","id":2,"handle":{{xrHandle}},"local":"G9DUM-S"}"""));
        var pdnShape = Shape(await Send(pdn, $$"""{"type":"bind","id":2,"handle":{{pdnHandle}},"local":"G9DUM-S"}"""));

        Assert.Equal(RhpErrorCode.Ok, xr.Item2);                          // XRouter accepts the wedge-fuse
        Assert.Equal(RhpErrorCode.InvalidLocalAddress, pdnShape.Item2);   // pdn refuses deterministically (D7)

        // Tidy: release the XRouter handle without ever connecting from it.
        _ = await Send(xrouter, $$"""{"type":"close","id":3,"handle":{{xrHandle}}}""");
    }

    [Fact]
    public async Task Hello_is_answered_with_the_unknown_type_fallback_identically()
    {
        // The `hello`/`helloReply` capability-discovery surface was REMOVED (proposed in
        // the rhp2lib field notes but never agreed — packet-net/packet.net#449). pdn now
        // treats `hello` like any other unsupported type, so it matches XRouter exactly:
        // the unknown-type fallback — helloReply errCode 2 ("Bad or missing type").
        var (xr, pdn) = await DiffAsync(await XRouterAsync(), await PdnAsync(),
            """{"type":"hello","id":18}""");

        Assert.Equal(xr, pdn);                                   // type/errCode/id-echo/casing all match
        Assert.Equal("helloReply", xr.Item1);
        Assert.Equal(RhpErrorCode.BadOrMissingType, xr.Item2);   // the live fallback, on both
        Assert.Equal(18, xr.Item3);
    }

    private static async Task<int> HandleOf(WireClient c, string req)
    {
        await c.SendAsync(req);
        using var doc = JsonDocument.Parse(await c.ReadAsync());
        return doc.RootElement.GetProperty("handle").GetInt32();
    }

    private static async Task<string> Send(WireClient c, string req)
    {
        await c.SendAsync(req);
        return await c.ReadAsync();
    }
}
