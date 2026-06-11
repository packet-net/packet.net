using System.Net;
using System.Text;
using Packet.Rhp2;
using Packet.Rhp2.Server;
using Xunit;
using static Packet.Rhp2.Tests.Server.RhpServerTests;

namespace Packet.Rhp2.Tests.Server;

/// <summary>
/// Raw-wire XRouter conformance for <see cref="RhpServer"/>: every assertion in this class is
/// made against the raw JSON TEXT the server emits, never via deserialized DTOs. The codec
/// reads case-insensitively, so the sibling suites (<see cref="RhpServerTests"/> /
/// <see cref="RhpServerPassiveTests"/>) would still pass if the server regressed to, say,
/// lowercase <c>errcode</c> — a break every real XRouter client would feel. Each fact names
/// the rhp2lib reference assertion it mirrors (<c>m0lte/rhp2lib-net</c>:
/// <c>tests/RhpV2.Client.IntegrationTests</c> + <c>docs/protocol.md</c>), which were pinned
/// against live XRouter.
/// </summary>
public sealed class XRouterWireConformanceTests : IAsyncDisposable
{
    private readonly List<IAsyncDisposable> cleanup = [];

    public async ValueTask DisposeAsync()
    {
        foreach (var d in cleanup)
        {
            await d.DisposeAsync();
        }
    }

    [Fact]
    public async Task Every_reply_type_emits_capital_errCode_and_errText_never_lowercase()
    {
        // Mirrors rhp2lib RealXRouterTests.ErrorReplies_All_Use_CapitalC_ErrCode +
        // SocketReply_Wire_Uses_CapitalC_ErrCode: live XRouter capitalises errCode/errText on
        // EVERY reply type, although the published spec implies lowercase except on authReply.
        var raws = new List<string>();

        // authReply — a failing auth (RequireAuth + always-deny validator).
        var (authServer, _) = await StartServerAsync(requireAuth: true, auth: (_, _) => false);
        var authClient = await ConnectAsync(authServer);
        raws.Add(await RoundTripAsync(authClient, new AuthMessage { Id = 1, User = "x", Pass = "y" }));

        // The rest of the rhp2lib battery, one error reply per request type.
        var (server, _) = await StartServerAsync();
        var client = await ConnectAsync(server);
        raws.Add(await RoundTripAsync(client, new OpenMessage { Id = 1, Pfam = "nonsense", Mode = SocketMode.Stream, Remote = "GB7RDG", Flags = (int)OpenFlags.Active }));
        raws.Add(await RoundTripAsync(client, new SocketMessage { Id = 1, Pfam = "nonsense", Mode = SocketMode.Stream }));
        raws.Add(await RoundTripAsync(client, new BindMessage { Id = 1, Handle = 99999, Local = "G9DUM" }));
        raws.Add(await RoundTripAsync(client, new ListenMessage { Id = 1, Handle = 99999, Flags = 0 }));
        raws.Add(await RoundTripAsync(client, new ConnectMessage { Id = 1, Handle = 99999, Remote = "G9DUM-2" }));
        raws.Add(await RoundTripAsync(client, new SendMessage { Id = 1, Handle = 99999, Data = "x" }));
        raws.Add(await RoundTripAsync(client, new SendToMessage { Id = 1, Handle = 99999, Data = "x" }));
        raws.Add(await RoundTripAsync(client, new StatusMessage { Id = 1, Handle = 99999 }));
        raws.Add(await RoundTripAsync(client, new CloseMessage { Id = 1, Handle = 99999 }));

        Assert.All(raws, json =>
        {
            Assert.Contains("\"errCode\":", json, StringComparison.Ordinal);
            Assert.Contains("\"errText\":", json, StringComparison.Ordinal);
            Assert.DoesNotContain("\"errcode\"", json, StringComparison.Ordinal);
            Assert.DoesNotContain("\"errtext\"", json, StringComparison.Ordinal);
        });
    }

    [Fact]
    public async Task Type_is_the_first_key_of_every_emitted_message()
    {
        // The reference serializer deliberately lifts `type` to the first key so an
        // XRouter-style dispatcher can route on it without buffering the whole object
        // (rhp2lib's MockRhpServer emits the same shape). Pin it on every server output
        // path — typed replies, async pushes, AND the raw-JSON {type}Reply path.
        var raws = new List<string>();

        // Active side: unknown-type reply, openReply (error + ok), status push, recv push, closeReply.
        var (serverA, gatewayA) = await StartServerAsync();
        var clientA = await ConnectAsync(serverA);

        await clientA.SendRawAsync("""{"type":"thisIsNotReal","id":1}""");
        raws.Add((await clientA.ReadRawAsync()).json);

        raws.Add(await RoundTripAsync(clientA, new OpenMessage { Id = 2, Pfam = "nonsense", Mode = SocketMode.Stream, Remote = "GB7RDG", Flags = (int)OpenFlags.Active }));

        var (handleA, openJson, statusJson) = await OpenRawAsync(clientA, id: 3);
        raws.Add(openJson);
        raws.Add(statusJson);

        gatewayA.Connection.Inject("hi\r"u8.ToArray());
        raws.Add((await clientA.ReadRawAsync()).json);

        raws.Add(await RoundTripAsync(clientA, new CloseMessage { Id = 4, Handle = handleA }));

        // Passive side: socketReply, bindReply, listenReply, accept push, sendReply,
        // server-initiated close push.
        var (serverB, gatewayB) = await StartServerAsync();
        var clientB = await ConnectAsync(serverB);

        var socketJson = await RoundTripAsync(clientB, new SocketMessage { Id = 1, Pfam = ProtocolFamily.Ax25, Mode = SocketMode.Stream });
        raws.Add(socketJson);
        var listener = Parse<SocketReplyMessage>(socketJson).Handle!.Value;
        raws.Add(await RoundTripAsync(clientB, new BindMessage { Id = 2, Handle = listener, Local = "M0LTE-7" }));
        raws.Add(await RoundTripAsync(clientB, new ListenMessage { Id = 3, Handle = listener, Flags = 0 }));

        await gatewayB.AcceptHandler!(gatewayB.Connection, "2");
        var acceptJson = (await clientB.ReadRawAsync()).json;
        raws.Add(acceptJson);
        var child = Parse<AcceptMessage>(acceptJson).Child;
        raws.Add((await clientB.ReadRawAsync()).json);   // the child's connected status push

        raws.Add(await RoundTripAsync(clientB, new SendMessage { Id = 4, Handle = child, Data = "x" }));

        gatewayB.Connection.Drop();   // the peer vanishes → server-initiated close push
        raws.Add((await clientB.ReadRawAsync()).json);

        // authReply.
        var (serverC, _) = await StartServerAsync(requireAuth: true, auth: (_, _) => false);
        var clientC = await ConnectAsync(serverC);
        raws.Add(await RoundTripAsync(clientC, new AuthMessage { Id = 1, User = "x", Pass = "y" }));

        Assert.All(raws, json => Assert.StartsWith("{\"type\":\"", json, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Replies_echo_the_request_id_and_pushes_carry_seqno_never_an_id()
    {
        // Mirrors the id/seqno discrimination rhp2lib pins throughout RealXRouterTests and
        // docs/protocol.md: a reply echoes the request id verbatim; an async notification
        // (status/recv) carries a server-assigned seqno and never an id.
        var (server, gateway) = await StartServerAsync();
        var client = await ConnectAsync(server);

        var (handle, openJson, statusJson) = await OpenRawAsync(client, id: 7);

        Assert.Contains("\"id\":7", openJson, StringComparison.Ordinal);          // echoed verbatim

        Assert.Contains("\"seqno\":", statusJson, StringComparison.Ordinal);      // a push...
        Assert.DoesNotContain("\"id\":", statusJson, StringComparison.Ordinal);   // ...not a reply

        gateway.Connection.Inject("GB7RDG:GLOSTR} hello\r"u8.ToArray());
        var (type, recvJson) = await client.ReadRawAsync();
        Assert.Equal("recv", type);
        Assert.Contains("\"seqno\":", recvJson, StringComparison.Ordinal);
        Assert.Contains($"\"handle\":{handle}", recvJson, StringComparison.Ordinal);
        Assert.DoesNotContain("\"id\":", recvJson, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Unknown_type_is_answered_with_typeReply_and_errCode_2()
    {
        // Mirrors rhp2lib RealXRouterTests.Unknown_Type_Comes_Back_As_TypeReply_With_BadType_Error:
        // XRouter manufactures the reply name by appending "Reply" to whatever type string it
        // received, with errCode 2 (Bad or missing type).
        var (server, _) = await StartServerAsync();
        var client = await ConnectAsync(server);

        await client.SendRawAsync("""{"type":"thisIsNotReal","id":7}""");

        var (type, json) = await client.ReadRawAsync();
        Assert.Equal("thisIsNotRealReply", type);
        Assert.Contains("\"type\":\"thisIsNotRealReply\"", json, StringComparison.Ordinal);
        Assert.Contains("\"errCode\":2", json, StringComparison.Ordinal);
        Assert.Contains("\"id\":7", json, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Accept_push_carries_port_as_a_json_string()
    {
        // Mirrors rhp2lib Ax25OverAxudpTests.Passive_Listener_Receives_Accept_When_Peer_Connects
        // + docs/protocol.md: real XRouter emits accept.port as a JSON STRING ("port":"2"),
        // not the unquoted number the PWP-0222 example shows.
        var (server, gateway) = await StartServerAsync();
        var client = await ConnectAsync(server);

        // The DAPPS listener sequence: socket → bind("M0LTE-7", port:null = all ports) → listen.
        var socketJson = await RoundTripAsync(client, new SocketMessage { Id = 1, Pfam = ProtocolFamily.Ax25, Mode = SocketMode.Stream });
        var listener = Parse<SocketReplyMessage>(socketJson).Handle!.Value;
        _ = await RoundTripAsync(client, new BindMessage { Id = 2, Handle = listener, Local = "M0LTE-7", Port = null });
        _ = await RoundTripAsync(client, new ListenMessage { Id = 3, Handle = listener, Flags = 0 });

        await gateway.AcceptHandler!(gateway.Connection, "2");   // a station connects on port "2"

        var (type, json) = await client.ReadRawAsync();
        Assert.Equal("accept", type);
        Assert.Contains("\"port\":\"2\"", json, StringComparison.Ordinal);    // quoted — the XRouter shape
        Assert.DoesNotContain("\"port\":2", json, StringComparison.Ordinal);  // never the spec example's bare number
        Assert.Contains("\"child\":", json, StringComparison.Ordinal);
        Assert.Contains("\"remote\":", json, StringComparison.Ordinal);
        Assert.Contains("\"seqno\":", json, StringComparison.Ordinal);        // a push...
        Assert.DoesNotContain("\"id\":", json, StringComparison.Ordinal);     // ...not a reply
    }

    [Fact]
    public async Task Error_codes_land_on_the_raw_wire_per_operation()
    {
        // The numeric errCode ladder as XRouter clients see it (rhp2lib docs/protocol.md error
        // table; the duplicate-listen semantics are pinned against live XRouter by the R-4
        // wire diff — XRouterRhpWireDiffTests).
        var (server, gateway) = await StartServerAsync();
        var client = await ConnectAsync(server);

        // send on an unknown handle → 3 (Invalid handle).
        var sendJson = await RoundTripAsync(client, new SendMessage { Id = 1, Handle = 99999, Data = "x" });
        Assert.Contains("\"errCode\":3", sendJson, StringComparison.Ordinal);

        // re-listen on the SAME socket → idempotent 0 Ok (the live XRouter wire — the R-4
        // diff corrected our initial 9 here)...
        var socketJson = await RoundTripAsync(client, new SocketMessage { Id = 2, Pfam = ProtocolFamily.Ax25, Mode = SocketMode.Stream });
        var listener = Parse<SocketReplyMessage>(socketJson).Handle!.Value;
        _ = await RoundTripAsync(client, new BindMessage { Id = 3, Handle = listener, Local = "M0LTE-7" });
        _ = await RoundTripAsync(client, new ListenMessage { Id = 4, Handle = listener, Flags = 0 });
        var againJson = await RoundTripAsync(client, new ListenMessage { Id = 5, Handle = listener, Flags = 0 });
        Assert.Contains("\"errCode\":0", againJson, StringComparison.Ordinal);

        // ...while a DIFFERENT socket claiming an already-listening callsign → 9 (Duplicate
        // socket) raised by the engine (deviation D5, docs/rhp2-server.md).
        gateway.ListenFail = new RhpGatewayException(RhpErrorCode.DuplicateSocket, "callsign M0LTE-7 is already registered.");
        var socket2Json = await RoundTripAsync(client, new SocketMessage { Id = 6, Pfam = ProtocolFamily.Ax25, Mode = SocketMode.Stream });
        var listener2 = Parse<SocketReplyMessage>(socket2Json).Handle!.Value;
        _ = await RoundTripAsync(client, new BindMessage { Id = 7, Handle = listener2, Local = "M0LTE-7" });
        var dupJson = await RoundTripAsync(client, new ListenMessage { Id = 8, Handle = listener2, Flags = 0 });
        Assert.Contains("\"errCode\":9", dupJson, StringComparison.Ordinal);

        // open with an unknown family → 8 (Bad or missing family).
        var badFamilyJson = await RoundTripAsync(client, new OpenMessage { Id = 6, Pfam = "nonsense", Mode = SocketMode.Stream, Remote = "GB7RDG", Flags = (int)OpenFlags.Active });
        Assert.Contains("\"errCode\":8", badFamilyJson, StringComparison.Ordinal);

        // a valid-but-unimplemented family (inet) → 16 (Operation not supported).
        var inetJson = await RoundTripAsync(client, new OpenMessage { Id = 7, Pfam = ProtocolFamily.Inet, Mode = SocketMode.Stream, Remote = "GB7RDG", Flags = (int)OpenFlags.Active });
        Assert.Contains("\"errCode\":16", inetJson, StringComparison.Ordinal);

        // any pre-auth request under RequireAuth → 14 (Unauthorised).
        var (authServer, _) = await StartServerAsync(requireAuth: true, auth: (_, _) => false);
        var authClient = await ConnectAsync(authServer);
        var preAuthJson = await RoundTripAsync(authClient, new OpenMessage { Id = 8, Pfam = ProtocolFamily.Ax25, Mode = SocketMode.Stream, Remote = "GB7RDG", Flags = (int)OpenFlags.Active });
        Assert.Contains("\"errCode\":14", preAuthJson, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Data_round_trips_as_latin1_escaped_text_not_base64()
    {
        // Mirrors rhp2lib Ax25OverAxudpTests.Binary_Bytes_Round_Trip_Via_Dgram_Through_Real_Xrouter:
        // the data field carries payload bytes as a Latin-1 string (one char per byte; the JSON
        // serializer's escaping handles control/high bytes), NOT base64.
        var (server, gateway) = await StartServerAsync();
        var client = await ConnectAsync(server);
        var (handle, _, _) = await OpenRawAsync(client, id: 1);

        // Client → server: a send whose data was built with the codec's Latin-1 encoder must
        // land on the session as exactly the original bytes.
        var binary = new byte[] { 0x00, 0x7F, 0xFF, 0x0A };
        _ = await RoundTripAsync(client, new SendMessage { Id = 2, Handle = handle, Data = RhpDataEncoding.ToWireString(binary) });
        Assert.Equal(binary, await gateway.Connection.WrittenAsync());

        // Server → client: the same bytes injected by the peer come back as a recv push whose
        // data decodes to the original payload via the Latin-1 decoder.
        gateway.Connection.Inject(binary);
        var (recvType, recvJson) = await client.ReadRawAsync();
        Assert.Equal("recv", recvType);
        Assert.Equal(binary, RhpDataEncoding.FromWireString(Parse<RecvMessage>(recvJson).Data));

        // And the raw shape is readable escaped text, not a base64 blob: payload "hello\r"
        // appears as hello\r (the JSON two-character escape); its base64 form (aGVsbG8N) must
        // appear nowhere.
        gateway.Connection.Inject("hello\r"u8.ToArray());
        var (_, textJson) = await client.ReadRawAsync();
        Assert.Contains("hello\\r", textJson, StringComparison.Ordinal);
        Assert.DoesNotContain("aGVsbG8N", textJson, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_fresh_connections_first_push_carries_seqno_zero_on_the_raw_wire()
    {
        // RHPTEST: "seqno starts at 0 ... the first notification after an active open is
        // status with seqno: 0, and the first recv carries seqno: 1" — per RHP connection.
        // Also observed live (R-5 probe): a fresh connection's first push is "seqno":0.
        var (server, gateway) = await StartServerAsync();
        var client = await ConnectAsync(server);

        var (handle, _, statusJson) = await OpenRawAsync(client, id: 1);
        Assert.Contains("\"seqno\":0", statusJson, StringComparison.Ordinal);

        gateway.Connection.Inject("x\r"u8.ToArray());
        var (recvType, recvJson) = await client.ReadRawAsync();
        Assert.Equal("recv", recvType);
        Assert.Contains("\"seqno\":1", recvJson, StringComparison.Ordinal);
        Assert.Contains($"\"handle\":{handle}", recvJson, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Absent_handle_and_absent_data_answer_12_with_the_wires_errText_and_no_handle_echo()
    {
        // Live-XRouter observed shape (R-5 probe): errCode 12 with errText "Missing handle" /
        // "Missing data" — NOT the spec table's "Bad parameter" — and no handle field on the
        // reply (there is nothing truthful to echo). 3 stays reserved for well-formed-but-
        // unknown handles (RHPTEST).
        var (server, _) = await StartServerAsync();
        var client = await ConnectAsync(server);

        await client.SendRawAsync("""{"type":"close","id":21}""");
        var (closeType, closeJson) = await client.ReadRawAsync();
        Assert.Equal("closeReply", closeType);
        Assert.Contains("\"errCode\":12", closeJson, StringComparison.Ordinal);
        Assert.Contains("\"errText\":\"Missing handle\"", closeJson, StringComparison.Ordinal);
        Assert.DoesNotContain("\"handle\"", closeJson, StringComparison.Ordinal);

        var socketJson = await RoundTripAsync(client, new SocketMessage { Id = 22, Pfam = ProtocolFamily.Ax25, Mode = SocketMode.Stream });
        var handle = Parse<SocketReplyMessage>(socketJson).Handle!.Value;
        await client.SendRawAsync($$"""{"type":"send","id":23,"handle":{{handle}}}""");
        var (sendType, sendJson) = await client.ReadRawAsync();
        Assert.Equal("sendReply", sendType);
        Assert.Contains("\"errCode\":12", sendJson, StringComparison.Ordinal);
        Assert.Contains("\"errText\":\"Missing data\"", sendJson, StringComparison.Ordinal);
        Assert.DoesNotContain("\"handle\"", sendJson, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Accept_is_followed_by_a_child_connected_status_push()
    {
        // The protocol lifecycle's incoming-listener sequence (rhp2lib protocol primer):
        // accept {handle:L, child:N} then status {handle:N, flags:CONNECTED} — pinned on
        // the raw wire: the status push carries the CHILD handle, the Connected bit, a
        // seqno, and never an id.
        var (server, gateway) = await StartServerAsync();
        var client = await ConnectAsync(server);

        var socketJson = await RoundTripAsync(client, new SocketMessage { Id = 1, Pfam = ProtocolFamily.Ax25, Mode = SocketMode.Stream });
        var listener = Parse<SocketReplyMessage>(socketJson).Handle!.Value;
        _ = await RoundTripAsync(client, new BindMessage { Id = 2, Handle = listener, Local = "M0LTE-7" });
        _ = await RoundTripAsync(client, new ListenMessage { Id = 3, Handle = listener, Flags = 0 });

        await gateway.AcceptHandler!(gateway.Connection, "2");

        var (acceptType, acceptJson) = await client.ReadRawAsync();
        Assert.Equal("accept", acceptType);
        var child = Parse<AcceptMessage>(acceptJson).Child;

        var (statusType, statusJson) = await client.ReadRawAsync();
        Assert.Equal("status", statusType);
        Assert.Contains($"\"handle\":{child}", statusJson, StringComparison.Ordinal);
        Assert.Contains("\"flags\":3", statusJson, StringComparison.Ordinal);   // Connected|ConOk — the open path's announcement shape
        Assert.Contains("\"seqno\":", statusJson, StringComparison.Ordinal);
        Assert.DoesNotContain("\"id\":", statusJson, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Hello_is_answered_with_a_capability_advertisement()
    {
        // The pdn extension (docs/rhp2-server.md §pdn extensions, from the rhp2lib field
        // notes' hello/helloReply proposal). errCode 0 + capability fields; a server
        // without the extension answers helloReply errCode 2 via the unknown-type
        // fallback (verified against the live container), which is the client's
        // "baseline v2" signal — so this is perfectly backwards-compatible.
        var (server, _) = await StartServerAsync();
        var client = await ConnectAsync(server);

        await client.SendRawAsync("""{"type":"hello","id":31}""");

        var (type, json) = await client.ReadRawAsync();
        Assert.Equal("helloReply", type);
        Assert.Contains("\"errCode\":0", json, StringComparison.Ordinal);
        Assert.Contains("\"errText\":\"Ok\"", json, StringComparison.Ordinal);
        Assert.Contains("\"id\":31", json, StringComparison.Ordinal);
        Assert.Contains("\"proto\":\"2\"", json, StringComparison.Ordinal);
        Assert.Contains("\"impl\":\"pdn/", json, StringComparison.Ordinal);
        Assert.Contains("\"pfams\":[\"ax25\"]", json, StringComparison.Ordinal);
        Assert.Contains($"\"maxData\":{RhpFraming.MaxPayloadLength}", json, StringComparison.Ordinal);
        Assert.Contains("\"enc\":\"latin1\"", json, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Hello_before_auth_is_refused_like_any_other_request()
    {
        // The pre-auth gate is uniform: hello gets helloReply errCode 14 (no capability
        // fields) until auth succeeds — a client still learns the server SUPPORTS the
        // extension (a non-supporting server would answer errCode 2).
        var (server, _) = await StartServerAsync(requireAuth: true, auth: (_, _) => false);
        var client = await ConnectAsync(server);

        await client.SendRawAsync("""{"type":"hello","id":32}""");

        var (type, json) = await client.ReadRawAsync();
        Assert.Equal("helloReply", type);
        Assert.Contains("\"errCode\":14", json, StringComparison.Ordinal);
        Assert.DoesNotContain("\"pfams\"", json, StringComparison.Ordinal);
    }

    // ── helpers (harness types reused from RhpServerTests — same assembly) ─

    private async Task<(RhpServer Server, FakeGateway Gateway)> StartServerAsync(bool requireAuth = false, Func<string, string, bool>? auth = null)
    {
        var gateway = new FakeGateway();
        var server = new RhpServer(new RhpServerOptions
        {
            Bind = IPAddress.Loopback,
            Port = 0,                      // ephemeral — no clashes across parallel test classes
            RequireAuth = requireAuth,
            Authenticate = auth,
        }, gateway);
        cleanup.Add(server);
        await server.StartAsync();
        return (server, gateway);
    }

    private async Task<RhpTestClient> ConnectAsync(RhpServer server)
    {
        var client = await RhpTestClient.ConnectAsync(server.BoundEndpoint!);
        cleanup.Add(client);
        return client;
    }

    // Sends one request frame and returns the next frame's raw JSON text.
    private static async Task<string> RoundTripAsync(RhpTestClient client, RhpMessage msg)
    {
        await client.SendAsync(msg);
        return (await client.ReadRawAsync()).json;
    }

    // Re-parses a raw frame through the codec when a test needs a field VALUE (a handle, the
    // decoded data) — the conformance assertions themselves stay on the raw text.
    private static T Parse<T>(string json) where T : RhpMessage
        => Assert.IsType<T>(RhpJson.Deserialize(Encoding.UTF8.GetBytes(json)));

    // Opens an active AX.25 stream through the fake gateway, returning the handle plus the
    // RAW openReply and status-push frames for text-level assertions.
    private static async Task<(int Handle, string OpenReplyJson, string StatusJson)> OpenRawAsync(RhpTestClient client, int id)
    {
        await client.SendAsync(new OpenMessage
        {
            Id = id,
            Pfam = ProtocolFamily.Ax25,
            Mode = SocketMode.Stream,
            Remote = "GB7RDG",
            Flags = (int)OpenFlags.Active,
        });
        var (openType, openJson) = await client.ReadRawAsync();
        Assert.Equal("openReply", openType);
        var (statusType, statusJson) = await client.ReadRawAsync();
        Assert.Equal("status", statusType);
        return (Parse<OpenReplyMessage>(openJson).Handle, openJson, statusJson);
    }
}
