using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading.Channels;
using Packet.Node.Core.Console;
using Packet.Rhp2;
using Packet.Rhp2.Server;
using Xunit;

namespace Packet.Rhp2.Tests.Server;

/// <summary>
/// Wire-level tests for <see cref="RhpServer"/>: a real TCP loopback client speaking framed
/// RHPv2 JSON against the server, with the packet engine replaced by a fake
/// <see cref="IRhpGateway"/> handing back in-memory connections. Pins the request/reply id
/// echo, the seqno-no-id push discrimination, the validation error ladder, and the named
/// deviations D2 (no bad-auth wedge) and D3 (per-connection handle ownership) from
/// <c>docs/rhp2-server.md</c>.
/// </summary>
public sealed class RhpServerTests : IAsyncDisposable
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);
    private readonly List<IAsyncDisposable> cleanup = [];

    public async ValueTask DisposeAsync()
    {
        foreach (var d in cleanup)
        {
            await d.DisposeAsync();
        }
    }

    private async Task<(RhpServer server, FakeGateway gateway)> StartServerAsync(bool requireAuth = false, Func<string, string, bool>? auth = null)
    {
        var gateway = new FakeGateway();
        var server = new RhpServer(new RhpServerOptions
        {
            Bind = IPAddress.Loopback,
            Port = 0,                      // ephemeral - no clashes across parallel test classes
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

    // ── The happy path: open(Active) → send → recv → close ───────────────

    [Fact]
    public async Task Open_active_returns_a_handle_and_pushes_connected_status()
    {
        var (server, gateway) = await StartServerAsync();
        var client = await ConnectAsync(server);

        await client.SendAsync(new OpenMessage
        {
            Id = 7,
            Pfam = ProtocolFamily.Ax25,
            Mode = SocketMode.Stream,
            Port = "1",
            Local = "M0LTE-1",
            Remote = "GB7RDG",
            Flags = (int)OpenFlags.Active,
        });

        var reply = await client.ExpectAsync<OpenReplyMessage>();
        reply.Id.Should().Be(7);                         // reply echoes the request id
        reply.ErrCode.Should().Be(RhpErrorCode.Ok);
        reply.ErrText.Should().Be("Ok");
        (reply.Handle >= 100).Should().BeTrue();                  // reference-style numbering

        var status = await client.ExpectAsync<StatusMessage>();
        status.Id.Should().BeNull();                            // a push, not a reply
        status.Seqno.Should().NotBeNull();
        status.Handle.Should().Be(reply.Handle);
        ((StatusFlags)(status.Flags ?? 0)).HasFlag(StatusFlags.Connected).Should().BeTrue();

        (gateway.LastPort, gateway.LastLocal, gateway.LastRemote).Should().Be(("1", "M0LTE-1", "GB7RDG"));
    }

    [Fact]
    public async Task Send_writes_decoded_bytes_to_the_session_and_replies_ok()
    {
        var (server, gateway) = await StartServerAsync();
        var client = await ConnectAsync(server);
        var handle = await OpenAsync(client);

        await client.SendAsync(new SendMessage { Id = 9, Handle = handle, Data = "N\r" });

        var reply = await client.ExpectAsync<SendReplyMessage>();
        reply.Id.Should().Be(9);
        reply.ErrCode.Should().Be(RhpErrorCode.Ok);
        var written = await gateway.Connection.WrittenAsync();
        written.Should().Equal("N\r"u8.ToArray());
    }

    [Fact]
    public async Task Session_bytes_are_pushed_as_recv_with_seqno_and_no_id()
    {
        var (server, gateway) = await StartServerAsync();
        var client = await ConnectAsync(server);
        var handle = await OpenAsync(client);

        gateway.Connection.Inject("GB7RDG:GLOSTR} hello\r"u8.ToArray());

        var recv = await client.ExpectAsync<RecvMessage>();
        recv.Handle.Should().Be(handle);
        recv.Id.Should().BeNull();
        recv.Seqno.Should().NotBeNull();
        recv.Data.Should().Be("GB7RDG:GLOSTR} hello\r");
    }

    [Fact]
    public async Task Client_close_tears_down_the_session_and_replies_ok()
    {
        var (server, gateway) = await StartServerAsync();
        var client = await ConnectAsync(server);
        var handle = await OpenAsync(client);

        await client.SendAsync(new CloseMessage { Id = 3, Handle = handle });

        var reply = await client.ExpectAsync<CloseReplyMessage>();
        reply.ErrCode.Should().Be(RhpErrorCode.Ok);
        await gateway.Connection.DisposedTask.WaitAsync(Timeout);   // DISC posted

        // The handle is gone: a second close is an invalid handle.
        await client.SendAsync(new CloseMessage { Id = 4, Handle = handle });
        var again = await client.ExpectAsync<CloseReplyMessage>();
        again.ErrCode.Should().Be(RhpErrorCode.InvalidHandle);
    }

    [Fact]
    public async Task Peer_drop_pushes_a_server_initiated_close()
    {
        var (server, gateway) = await StartServerAsync();
        var client = await ConnectAsync(server);
        var handle = await OpenAsync(client);

        gateway.Connection.Drop();   // the far station disconnected

        var close = await client.ExpectAsync<CloseMessage>();
        close.Handle.Should().Be(handle);
        close.Id.Should().BeNull();
        close.Seqno.Should().NotBeNull();
    }

    // ── seqno: per-RHP-connection, starting at 0, shared across push types ─
    //    (RHPTEST-verified: first push is seqno 0; live XRouter confirms - a fresh
    //    connection's first notification carries "seqno":0.)

    [Fact]
    public async Task First_push_on_a_fresh_connection_carries_seqno_zero()
    {
        var (server, _) = await StartServerAsync();
        var client = await ConnectAsync(server);

        await client.SendAsync(new OpenMessage
        { Id = 1, Pfam = ProtocolFamily.Ax25, Mode = SocketMode.Stream, Remote = "GB7RDG", Flags = (int)OpenFlags.Active });
        _ = await client.ExpectAsync<OpenReplyMessage>();

        var status = await client.ExpectAsync<StatusMessage>();
        status.Seqno.Should().Be(0);                     // not 1, the counter starts at 0
    }

    [Fact]
    public async Task Seqno_increments_across_push_types_within_a_connection()
    {
        var (server, gateway) = await StartServerAsync();
        var client = await ConnectAsync(server);

        await client.SendAsync(new OpenMessage
        { Id = 1, Pfam = ProtocolFamily.Ax25, Mode = SocketMode.Stream, Remote = "GB7RDG", Flags = (int)OpenFlags.Active });
        _ = await client.ExpectAsync<OpenReplyMessage>();

        // One counter across all push types: status, recv, recv, server-close.
        var status = await client.ExpectAsync<StatusMessage>();
        status.Seqno.Should().Be(0);

        gateway.Connection.Inject("one\r"u8.ToArray());
        (await client.ExpectAsync<RecvMessage>()).Seqno.Should().Be(1);

        gateway.Connection.Inject("two\r"u8.ToArray());
        (await client.ExpectAsync<RecvMessage>()).Seqno.Should().Be(2);

        gateway.Connection.Drop();
        (await client.ExpectAsync<CloseMessage>()).Seqno.Should().Be(3);
    }

    [Fact]
    public async Task Two_concurrent_connections_each_count_seqno_from_zero_independently()
    {
        var (server, _) = await StartServerAsync();
        var alice = await ConnectAsync(server);
        var bob = await ConnectAsync(server);

        // Alice's pushes advance HER counter only...
        await alice.SendAsync(new OpenMessage
        { Id = 1, Pfam = ProtocolFamily.Ax25, Mode = SocketMode.Stream, Remote = "GB7RDG", Flags = (int)OpenFlags.Active });
        _ = await alice.ExpectAsync<OpenReplyMessage>();
        (await alice.ExpectAsync<StatusMessage>()).Seqno.Should().Be(0);

        // ...so Bob's first push is still seqno 0 (a server-wide counter would give 1 here).
        await bob.SendAsync(new OpenMessage
        { Id = 1, Pfam = ProtocolFamily.Ax25, Mode = SocketMode.Stream, Remote = "GB7RDG", Flags = (int)OpenFlags.Active });
        _ = await bob.ExpectAsync<OpenReplyMessage>();
        (await bob.ExpectAsync<StatusMessage>()).Seqno.Should().Be(0);
    }

    // ── send.data: mandatory even when empty (RHPTEST) ───────────────────

    [Fact]
    public async Task Send_with_the_data_field_absent_is_bad_parameter_12()
    {
        var (server, _) = await StartServerAsync();
        var client = await ConnectAsync(server);
        var handle = await OpenAsync(client);

        // Raw JSON so the field is genuinely ABSENT on the wire (not empty).
        await client.SendRawAsync($$"""{"type":"send","id":5,"handle":{{handle}}}""");

        var reply = await client.ExpectAsync<SendReplyMessage>();
        reply.ErrCode.Should().Be(RhpErrorCode.BadParameter);
        reply.ErrText.Should().Be("Missing data");       // the live wire's exact errText
        reply.Id.Should().Be(5);
    }

    [Fact]
    public async Task Send_with_empty_data_is_a_legal_zero_byte_send()
    {
        var (server, gateway) = await StartServerAsync();
        var client = await ConnectAsync(server);
        var handle = await OpenAsync(client);

        await client.SendAsync(new SendMessage { Id = 6, Handle = handle, Data = "" });

        var reply = await client.ExpectAsync<SendReplyMessage>();
        reply.ErrCode.Should().Be(RhpErrorCode.Ok);      // "" ≠ absent, RHPTEST's zero-byte send
        (await gateway.Connection.WrittenAsync()).Should().BeEmpty();
    }

    // ── absent handle: errCode 12 ("Missing handle"), never 3 ────────────
    //    RHPTEST: "3 is for handles that are well-formed but unknown"; verified per-op
    //    against live XRouter (every op answers 12 with errText "Missing handle").

    [Theory]
    [InlineData("""{"type":"close","id":3}""", "closeReply")]
    [InlineData("""{"type":"send","id":3,"data":"x"}""", "sendReply")]
    [InlineData("""{"type":"bind","id":3,"local":"M0LTE-7"}""", "bindReply")]
    [InlineData("""{"type":"listen","id":3,"flags":0}""", "listenReply")]
    [InlineData("""{"type":"connect","id":3,"remote":"GB7RDG"}""", "connectReply")]
    [InlineData("""{"type":"status","id":3}""", "statusReply")]
    [InlineData("""{"type":"sendto","id":3,"data":"x"}""", "sendtoReply")]
    public async Task A_request_with_an_absent_handle_is_bad_parameter_12(string request, string expectedReplyType)
    {
        var (server, _) = await StartServerAsync();
        var client = await ConnectAsync(server);

        await client.SendRawAsync(request);

        var (type, json) = await client.ReadRawAsync();
        type.Should().Be(expectedReplyType);
        json.Should().Contain("\"errCode\":12");
        json.Should().Contain("\"errText\":\"Missing handle\"");
        json.Should().Contain("\"id\":3");
        json.Should().NotContain("\"handle\"");   // nothing truthful to echo
    }

    // ── callsign validation at the wire: 6 / 7, never a wedge (deviation D7) ─

    [Fact]
    public async Task Open_with_an_alphabetic_SSID_local_is_refused_with_6()
    {
        // XRouter accepts G9DUM-S here, "Ok"s a connect from it, then wedges in background
        // SABM retries (rhp2lib field notes). pdn refuses at the wire, deterministically.
        var (server, _) = await StartServerAsync();
        var client = await ConnectAsync(server);

        await client.SendAsync(new OpenMessage
        { Id = 1, Pfam = ProtocolFamily.Ax25, Mode = SocketMode.Stream, Local = "G9DUM-S", Remote = "GB7RDG", Flags = (int)OpenFlags.Active });

        var reply = await client.ExpectAsync<OpenReplyMessage>();
        reply.ErrCode.Should().Be(RhpErrorCode.InvalidLocalAddress);
        reply.ErrText.Should().Be("Invalid local address");   // a clean 6, not a generic failure
    }

    [Fact]
    public async Task Open_with_an_alphabetic_SSID_remote_is_refused_with_7()
    {
        var (server, _) = await StartServerAsync();
        var client = await ConnectAsync(server);

        await client.SendAsync(new OpenMessage
        { Id = 1, Pfam = ProtocolFamily.Ax25, Mode = SocketMode.Stream, Remote = "G9DUM-S", Flags = (int)OpenFlags.Active });

        var reply = await client.ExpectAsync<OpenReplyMessage>();
        reply.ErrCode.Should().Be(RhpErrorCode.InvalidRemoteAddress);
    }

    [Fact]
    public async Task Open_without_local_is_accepted_and_defaults_to_the_node_callsign()
    {
        // Deviation D8: requiring `local` on open is an XRouter-ism, not an RHP rule
        // (RHPTEST quotes the author saying exactly that). pdn stays permissive - a null
        // local reaches the gateway, which dials as the node's own callsign.
        var (server, gateway) = await StartServerAsync();
        var client = await ConnectAsync(server);

        await client.SendAsync(new OpenMessage
        { Id = 1, Pfam = ProtocolFamily.Ax25, Mode = SocketMode.Stream, Remote = "GB7RDG", Flags = (int)OpenFlags.Active });

        var reply = await client.ExpectAsync<OpenReplyMessage>();
        reply.ErrCode.Should().Be(RhpErrorCode.Ok);
        gateway.LastLocal.Should().BeNull();
    }

    // ── The validation ladder + gateway error passthrough ────────────────

    [Theory]
    [InlineData("nonsense", "stream", 0x80, "GB7RDG", RhpErrorCode.BadOrMissingFamily)]
    [InlineData("inet", "stream", 0x80, "GB7RDG", RhpErrorCode.OperationNotSupported)]     // valid family, not implemented
    [InlineData("ax25", "warble", 0x80, "GB7RDG", RhpErrorCode.BadOrMissingMode)]
    [InlineData("ax25", "dgram", 0x80, "GB7RDG", RhpErrorCode.Ok)]                          // dgram open = the combined socket+bind form (R-6)
    [InlineData("ax25", "custom", 0x80, "GB7RDG", RhpErrorCode.Ok)]                         // custom open = the combined socket+bind form (R-7)
    [InlineData("ax25", "stream", 0x00, "GB7RDG", RhpErrorCode.OperationNotSupported)]     // passive = R-3
    [InlineData("ax25", "stream", 0x80, null, RhpErrorCode.InvalidRemoteAddress)]
    public async Task Open_validation_ladder(string pfam, string mode, int flags, string? remote, int expected)
    {
        var (server, _) = await StartServerAsync();
        var client = await ConnectAsync(server);

        await client.SendAsync(new OpenMessage { Id = 1, Pfam = pfam, Mode = mode, Flags = flags, Remote = remote });

        var reply = await client.ExpectAsync<OpenReplyMessage>();
        reply.ErrCode.Should().Be(expected);
        reply.Id.Should().Be(1);
    }

    [Fact]
    public async Task Gateway_failure_lands_on_the_open_reply_verbatim()
    {
        var (server, gateway) = await StartServerAsync();
        gateway.Fail = new RhpGatewayException(RhpErrorCode.NoRoute, "Connect to GB7RDG timed out.");
        var client = await ConnectAsync(server);

        await client.SendAsync(new OpenMessage
        { Id = 2, Pfam = ProtocolFamily.Ax25, Mode = SocketMode.Stream, Remote = "GB7RDG", Flags = (int)OpenFlags.Active });

        var reply = await client.ExpectAsync<OpenReplyMessage>();
        reply.ErrCode.Should().Be(RhpErrorCode.NoRoute);
        reply.ErrText.Should().Be("Connect to GB7RDG timed out.");
    }

    [Fact]
    public async Task Send_on_an_unknown_handle_is_invalid_handle()
    {
        var (server, _) = await StartServerAsync();
        var client = await ConnectAsync(server);

        await client.SendAsync(new SendMessage { Id = 1, Handle = 12345, Data = "x" });

        var reply = await client.ExpectAsync<SendReplyMessage>();
        reply.ErrCode.Should().Be(RhpErrorCode.InvalidHandle);
    }

    // ── XRouter wire contracts: unknown type, R-3 deferrals ───────────────

    [Fact]
    public async Task Unknown_type_is_answered_with_typeReply_errCode_2()
    {
        var (server, _) = await StartServerAsync();
        var client = await ConnectAsync(server);

        await client.SendRawAsync("""{"type":"thisIsNotReal","id":42}""");

        var (type, json) = await client.ReadRawAsync();
        type.Should().Be("thisIsNotRealReply");
        json.Should().Contain("\"errCode\":2");
        json.Should().Contain("\"id\":42");
    }

    [Fact]
    public async Task Unknown_type_with_a_non_integer_id_is_still_answered_and_keeps_the_session()
    {
        // The malformed id used to throw InvalidOperationException out of the codec, past the
        // read loop's bad-frame filter, and the whole session was dropped instead of getting
        // its graceful errCode 2 (packet.net#698 RM-1).
        var (server, _) = await StartServerAsync();
        var client = await ConnectAsync(server);

        await client.SendRawAsync("""{"type":"thisIsNotReal","id":"not-an-int","seqno":1.5}""");

        var (type, json) = await client.ReadRawAsync();
        type.Should().Be("thisIsNotRealReply");
        json.Should().Contain("\"errCode\":2");
        json.Should().NotContain("\"id\":");   // nothing truthful to echo

        // The session survives: a normal request still works on the same connection.
        await client.SendAsync(new SocketMessage { Id = 2, Pfam = ProtocolFamily.Ax25, Mode = SocketMode.Stream });
        (await client.ExpectAsync<SocketReplyMessage>()).ErrCode.Should().Be(RhpErrorCode.Ok);
    }

    [Fact]
    public async Task An_error_openReply_carries_no_handle_key_on_the_wire()
    {
        // spec/rhp2.cddl: "? handle: uint  ; present on success; absent on parameter errors".
        // pdn used to emit "handle":0 on every failure (packet.net#698 RM-3).
        var (server, _) = await StartServerAsync();
        var client = await ConnectAsync(server);

        await client.SendAsync(new OpenMessage
        { Id = 1, Pfam = ProtocolFamily.Ax25, Mode = SocketMode.Stream, Remote = "G9DUM-S", Flags = (int)OpenFlags.Active });

        var (type, json) = await client.ReadRawAsync();
        type.Should().Be("openReply");
        json.Should().Contain("\"errCode\":7");
        json.Should().NotContain("handle");
    }

    [Fact]
    public async Task A_successful_openReply_still_carries_its_handle()
    {
        var (server, _) = await StartServerAsync();
        var client = await ConnectAsync(server);

        await client.SendAsync(new OpenMessage
        { Id = 1, Pfam = ProtocolFamily.Ax25, Mode = SocketMode.Stream, Remote = "GB7RDG", Flags = (int)OpenFlags.Active });

        var (type, json) = await client.ReadRawAsync();
        type.Should().Be("openReply");
        json.Should().Contain("\"handle\":");
    }

    // ── Auth: the gate + the D2 no-wedge deviation ────────────────────────

    [Fact]
    public async Task Auth_gate_refuses_pre_auth_requests_then_admits_after_good_auth()
    {
        var (server, _) = await StartServerAsync(requireAuth: true, auth: (u, p) => u == "tom" && p == "pw");
        var client = await ConnectAsync(server);

        // Pre-auth request → refused with 14 on the matching reply type.
        await client.SendAsync(new OpenMessage
        { Id = 1, Pfam = ProtocolFamily.Ax25, Mode = SocketMode.Stream, Remote = "GB7RDG", Flags = (int)OpenFlags.Active });
        var refused = await client.ExpectAsync<OpenReplyMessage>();
        refused.ErrCode.Should().Be(RhpErrorCode.Unauthorised);

        // Bad auth fails THAT attempt only (D2: no connection wedge)...
        await client.SendAsync(new AuthMessage { Id = 2, User = "tom", Pass = "wrong" });
        var bad = await client.ExpectAsync<AuthReplyMessage>();
        bad.ErrCode.Should().Be(RhpErrorCode.Unauthorised);

        // ...a subsequent good auth on the SAME connection succeeds and unlocks it.
        await client.SendAsync(new AuthMessage { Id = 3, User = "tom", Pass = "pw" });
        var good = await client.ExpectAsync<AuthReplyMessage>();
        good.ErrCode.Should().Be(RhpErrorCode.Ok);

        await client.SendAsync(new OpenMessage
        { Id = 4, Pfam = ProtocolFamily.Ax25, Mode = SocketMode.Stream, Remote = "GB7RDG", Flags = (int)OpenFlags.Active });
        var opened = await client.ExpectAsync<OpenReplyMessage>();
        opened.ErrCode.Should().Be(RhpErrorCode.Ok);
    }

    // ── D3: handles are owned by the connection that created them ─────────

    [Fact]
    public async Task A_handle_is_not_usable_from_another_client_connection()
    {
        var (server, _) = await StartServerAsync();
        var alice = await ConnectAsync(server);
        var bob = await ConnectAsync(server);
        var handle = await OpenAsync(alice);

        await bob.SendAsync(new SendMessage { Id = 1, Handle = handle, Data = "stolen" });

        var reply = await bob.ExpectAsync<SendReplyMessage>();
        reply.ErrCode.Should().Be(RhpErrorCode.InvalidHandle);   // same as unknown, no oracle
    }

    // ── dgram (pure UI datagram): socket → bind → sendto (TX) + async recv (RX) - R-6 ───

    [Fact]
    public async Task Socket_bind_sendto_emits_a_pure_ui_datagram_with_the_right_source_dest_and_data()
    {
        var (server, gateway) = await StartServerAsync();
        var client = await ConnectAsync(server);
        var handle = await BindDgramAsync(client, "M0LTE-1", port: "1");

        // Pure datagram: bind the station, sendto the whole `data` as the UI info - no PID field on
        // the wire; a `dgram` frame's PID is the implicit no-Layer-3 0xF0.
        await client.SendAsync(new SendToMessage { Id = 9, Handle = handle, Remote = "GB7RDG", Data = "hi\r" });

        var reply = await client.ExpectAsync<SendToReplyMessage>();
        reply.Id.Should().Be(9);
        reply.ErrCode.Should().Be(RhpErrorCode.Ok);
        (gateway.UiPort, gateway.UiLocal, gateway.UiRemote).Should().Be(("1", "M0LTE-1", "GB7RDG"));
        gateway.UiPid.Should().Be((byte)0xF0);                 // implicit no-Layer-3 PID
        gateway.UiInfo.Should().Equal("hi\r"u8.ToArray());        // the whole `data` is the info
    }

    [Fact]
    public async Task Sendto_without_a_pid_defaults_to_0xF0_no_layer_3()
    {
        // Native beacon / APRS: bind an app callsign, sendto with no pid → 0xF0 (the pdn default).
        var (server, gateway) = await StartServerAsync();
        var client = await ConnectAsync(server);
        var handle = await BindDgramAsync(client, "M0LTE-9");

        await client.SendAsync(new SendToMessage { Id = 1, Handle = handle, Remote = "APRS", Data = "!beacon" });

        var reply = await client.ExpectAsync<SendToReplyMessage>();
        reply.ErrCode.Should().Be(RhpErrorCode.Ok);
        gateway.UiPid.Should().Be((byte)0xF0);
        gateway.UiRemote.Should().Be("APRS");
    }

    [Fact]
    public async Task Sendto_source_falls_back_to_the_bound_local_when_omitted()
    {
        var (server, gateway) = await StartServerAsync();
        var client = await ConnectAsync(server);
        var handle = await BindDgramAsync(client, "M0LTE-1");

        await client.SendAsync(new SendToMessage { Id = 1, Handle = handle, Remote = "GB7RDG", Data = "x" });

        var reply = await client.ExpectAsync<SendToReplyMessage>();
        reply.ErrCode.Should().Be(RhpErrorCode.Ok);
        gateway.UiLocal.Should().Be("M0LTE-1");   // the bound local becomes the frame source
    }

    [Fact]
    public async Task Sendto_with_an_empty_payload_is_rejected_errCode_1()
    {
        // AX.25 UI, unlike UDP, carries no zero-byte datagram - errCode 1 (protocol.md).
        var (server, _) = await StartServerAsync();
        var client = await ConnectAsync(server);
        var handle = await BindDgramAsync(client, "M0LTE-1");

        await client.SendAsync(new SendToMessage { Id = 4, Handle = handle, Remote = "GB7RDG", Data = "" });

        var reply = await client.ExpectAsync<SendToReplyMessage>();
        reply.ErrCode.Should().Be(RhpErrorCode.Unspecified);   // 1, distinct from 12 (absent data)
        reply.Id.Should().Be(4);
    }

    [Fact]
    public async Task Listen_on_a_dgram_socket_is_operation_not_supported_16()
    {
        var (server, _) = await StartServerAsync();
        var client = await ConnectAsync(server);

        await client.SendAsync(new SocketMessage { Id = 1, Pfam = ProtocolFamily.Ax25, Mode = SocketMode.Dgram });
        var sock = await client.ExpectAsync<SocketReplyMessage>();
        sock.ErrCode.Should().Be(RhpErrorCode.Ok);

        await client.SendAsync(new ListenMessage { Id = 2, Handle = sock.Handle!.Value, Flags = 0 });

        var listen = await client.ExpectAsync<ListenReplyMessage>();
        listen.ErrCode.Should().Be(RhpErrorCode.OperationNotSupported);   // a datagram has no listening state
    }

    [Fact]
    public async Task A_bound_dgram_handle_receives_an_inbound_ui_frame_as_a_recv_push()
    {
        var (server, gateway) = await StartServerAsync();
        var client = await ConnectAsync(server);
        var handle = await BindDgramAsync(client, "M0LTE-1", port: "1");

        // A UI frame arrives off the air (the listener would tap it) — the gateway injects it.
        await gateway.InjectUiAsync(new UiDatagram("2E0XYZ", "M0LTE-1", 0xCC, "ping"u8.ToArray(), "1"));

        var recv = await client.ExpectAsync<RecvMessage>();
        recv.Handle.Should().Be(handle);
        recv.Id.Should().BeNull();                       // a push, not a reply
        recv.Seqno.Should().NotBeNull();                 // carries a seqno, not an id
        recv.Remote.Should().Be("2E0XYZ");        // the frame's true source → recv.remote
        recv.Local.Should().Be("M0LTE-1");        // the frame's destination → recv.local
        recv.Port.Should().Be("1");               // the arrival port label
        recv.Pid.Should().BeNull();                      // pure dgram carries NO pid field (PID is implicit 0xF0)
        recv.Data.Should().Be("ping");            // the info verbatim (no PID prepended)
    }

    [Fact]
    public async Task Open_dgram_creates_a_datagram_socket_that_can_sendto()
    {
        // The combined open form (R-6): open(dgram, local, port) = socket+bind in one step.
        var (server, gateway) = await StartServerAsync();
        var client = await ConnectAsync(server);

        await client.SendAsync(new OpenMessage
        {
            Id = 1, Pfam = ProtocolFamily.Ax25, Mode = SocketMode.Dgram, Local = "M0LTE-1", Port = "1",
        });
        var open = await client.ExpectAsync<OpenReplyMessage>();
        open.ErrCode.Should().Be(RhpErrorCode.Ok);
        (open.Handle >= 100).Should().BeTrue();

        await client.SendAsync(new SendToMessage { Id = 2, Handle = open.Handle, Remote = "GB7RDG", Data = "y" });
        var reply = await client.ExpectAsync<SendToReplyMessage>();
        reply.ErrCode.Should().Be(RhpErrorCode.Ok);
        (gateway.UiPort, gateway.UiLocal, gateway.UiRemote).Should().Be(("1", "M0LTE-1", "GB7RDG"));
    }

    // ── custom (PID-in-data UI): socket → bind → sendto (TX) + async recv (RX) — R-7 ───
    // PWP-0222 §1.2 defines `custom` only as "user specified protocol"; the AX.25 convention that
    // the first octet of `data` is the PID is per G8PZT's clarification (2026-07, resolving #647).
    // Same message flow + gateway seams as dgram; the ONLY difference is where the PID sits —
    // data[0] on TX, prepended to data on RX. No `pid` field anywhere.

    [Fact]
    public async Task Socket_custom_bind_sendto_takes_the_pid_from_the_first_data_octet()
    {
        var (server, gateway) = await StartServerAsync();
        var client = await ConnectAsync(server);
        var handle = await BindCustomAsync(client, "M0LTE-1", port: "1");

        // IP-over-AX.25 the standard way: PID 0xCC is the first payload octet, the IP datagram follows.
        await client.SendAsync(new SendToMessage
        {
            Id = 9, Handle = handle, Remote = "GB7RDG", Data = CustomData(0xCC, "hi\r"u8),
        });

        var reply = await client.ExpectAsync<SendToReplyMessage>();
        reply.Id.Should().Be(9);
        reply.ErrCode.Should().Be(RhpErrorCode.Ok);
        (gateway.UiPort, gateway.UiLocal, gateway.UiRemote).Should().Be(("1", "M0LTE-1", "GB7RDG"));
        gateway.UiPid.Should().Be((byte)0xCC);            // data[0] → the UI frame PID
        gateway.UiInfo.Should().Equal("hi\r"u8.ToArray());   // data[1..] → the UI info
    }

    [Fact]
    public async Task A_custom_datagram_carrying_only_the_pid_octet_sends_empty_info()
    {
        // "at least the PID byte": a 1-byte `data` is a valid custom datagram — PID only, empty info.
        var (server, gateway) = await StartServerAsync();
        var client = await ConnectAsync(server);
        var handle = await BindCustomAsync(client, "M0LTE-1");

        await client.SendAsync(new SendToMessage { Id = 1, Handle = handle, Remote = "GB7RDG", Data = CustomData(0xF0, []) });

        var reply = await client.ExpectAsync<SendToReplyMessage>();
        reply.ErrCode.Should().Be(RhpErrorCode.Ok);
        gateway.UiPid.Should().Be((byte)0xF0);
        gateway.UiInfo!.Should().BeEmpty();
    }

    [Fact]
    public async Task Sendto_on_a_custom_socket_with_empty_data_is_rejected_errCode_1()
    {
        // An empty `data` carries no PID octet — invalid (errCode 1), like the empty-dgram rule.
        var (server, _) = await StartServerAsync();
        var client = await ConnectAsync(server);
        var handle = await BindCustomAsync(client, "M0LTE-1");

        await client.SendAsync(new SendToMessage { Id = 4, Handle = handle, Remote = "GB7RDG", Data = "" });

        var reply = await client.ExpectAsync<SendToReplyMessage>();
        reply.ErrCode.Should().Be(RhpErrorCode.Unspecified);   // 1, distinct from 12 (absent data)
        reply.Id.Should().Be(4);
    }

    [Fact]
    public async Task Listen_on_a_custom_socket_is_operation_not_supported_16()
    {
        var (server, _) = await StartServerAsync();
        var client = await ConnectAsync(server);

        await client.SendAsync(new SocketMessage { Id = 1, Pfam = ProtocolFamily.Ax25, Mode = SocketMode.Custom });
        var sock = await client.ExpectAsync<SocketReplyMessage>();
        sock.ErrCode.Should().Be(RhpErrorCode.Ok);

        await client.SendAsync(new ListenMessage { Id = 2, Handle = sock.Handle!.Value, Flags = 0 });

        var listen = await client.ExpectAsync<ListenReplyMessage>();
        listen.ErrCode.Should().Be(RhpErrorCode.OperationNotSupported);   // a datagram has no listening state
    }

    [Fact]
    public async Task A_bound_custom_handle_receives_an_inbound_ui_frame_with_the_pid_prepended_to_data()
    {
        var (server, gateway) = await StartServerAsync();
        var client = await ConnectAsync(server);
        var handle = await BindCustomAsync(client, "M0LTE-1", port: "1");

        // Same inbound UI frame the dgram test injects — but a custom socket prepends the PID.
        await gateway.InjectUiAsync(new UiDatagram("2E0XYZ", "M0LTE-1", 0xCC, "ping"u8.ToArray(), "1"));

        var recv = await client.ExpectAsync<RecvMessage>();
        recv.Handle.Should().Be(handle);
        recv.Id.Should().BeNull();                                    // a push, not a reply
        recv.Seqno.Should().NotBeNull();
        recv.Remote.Should().Be("2E0XYZ");                     // the frame's true source → recv.remote
        recv.Local.Should().Be("M0LTE-1");                     // the frame's destination → recv.local
        recv.Port.Should().Be("1");
        recv.Pid.Should().BeNull();                                   // no pid field, the PID is in data[0]
        recv.Data.Should().Be(CustomData(0xCC, "ping"u8));     // data = [frame.pid] ++ info
    }

    [Fact]
    public async Task Open_custom_creates_a_datagram_socket_that_can_sendto()
    {
        // The combined open form (R-7): open(custom, local, port) = socket+bind in one step.
        var (server, gateway) = await StartServerAsync();
        var client = await ConnectAsync(server);

        await client.SendAsync(new OpenMessage
        {
            Id = 1, Pfam = ProtocolFamily.Ax25, Mode = SocketMode.Custom, Local = "M0LTE-1", Port = "1",
        });
        var open = await client.ExpectAsync<OpenReplyMessage>();
        open.ErrCode.Should().Be(RhpErrorCode.Ok);
        (open.Handle >= 100).Should().BeTrue();

        await client.SendAsync(new SendToMessage { Id = 2, Handle = open.Handle, Remote = "GB7RDG", Data = CustomData(0xCC, "y"u8) });
        var reply = await client.ExpectAsync<SendToReplyMessage>();
        reply.ErrCode.Should().Be(RhpErrorCode.Ok);
        (gateway.UiPort, gateway.UiLocal, gateway.UiRemote).Should().Be(("1", "M0LTE-1", "GB7RDG"));
        gateway.UiPid.Should().Be((byte)0xCC);
    }

    // ── helpers ───────────────────────────────────────────────────────────

    // socket(dgram) → bind(callsign[, port]) → returns the bound dgram handle.
    private static async Task<int> BindDgramAsync(RhpTestClient client, string callsign, string? port = null)
    {
        await client.SendAsync(new SocketMessage { Id = 1, Pfam = ProtocolFamily.Ax25, Mode = SocketMode.Dgram });
        var sock = await client.ExpectAsync<SocketReplyMessage>();
        sock.ErrCode.Should().Be(RhpErrorCode.Ok);

        await client.SendAsync(new BindMessage { Id = 2, Handle = sock.Handle!.Value, Local = callsign, Port = port });
        var bind = await client.ExpectAsync<BindReplyMessage>();
        bind.ErrCode.Should().Be(RhpErrorCode.Ok);
        return sock.Handle!.Value;
    }

    // socket(custom) → bind(callsign[, port]) → returns the bound custom handle.
    private static async Task<int> BindCustomAsync(RhpTestClient client, string callsign, string? port = null)
    {
        await client.SendAsync(new SocketMessage { Id = 1, Pfam = ProtocolFamily.Ax25, Mode = SocketMode.Custom });
        var sock = await client.ExpectAsync<SocketReplyMessage>();
        sock.ErrCode.Should().Be(RhpErrorCode.Ok);

        await client.SendAsync(new BindMessage { Id = 2, Handle = sock.Handle!.Value, Local = callsign, Port = port });
        var bind = await client.ExpectAsync<BindReplyMessage>();
        bind.ErrCode.Should().Be(RhpErrorCode.Ok);
        return sock.Handle!.Value;
    }

    // Build a custom-mode `data` wire string: PID as the first octet, then the info — the exact
    // shape the server decodes on TX (data[0]=pid, data[1..]=info) and produces on RX.
    private static string CustomData(byte pid, ReadOnlySpan<byte> info)
    {
        var payload = new byte[info.Length + 1];
        payload[0] = pid;
        info.CopyTo(payload.AsSpan(1));
        return RhpDataEncoding.ToWireString(payload);
    }

    private static async Task<int> OpenAsync(RhpTestClient client)
    {
        await client.SendAsync(new OpenMessage
        {
            Id = 1,
            Pfam = ProtocolFamily.Ax25,
            Mode = SocketMode.Stream,
            Remote = "GB7RDG",
            Flags = (int)OpenFlags.Active,
        });
        var reply = await client.ExpectAsync<OpenReplyMessage>();
        reply.ErrCode.Should().Be(RhpErrorCode.Ok);
        _ = await client.ExpectAsync<StatusMessage>();   // swallow the connected push
        return reply.Handle!.Value;
    }

    // The packet engine stand-in: returns one scripted in-memory connection (or throws), and
    // records listener registrations so the passive tests can drive accepts by hand.
    internal sealed class FakeGateway : IRhpGateway
    {
        public FakeConnection Connection { get; } = new();
        public RhpGatewayException? Fail { get; set; }
        public string? LastPort, LastLocal, LastRemote;

        public RhpGatewayException? ListenFail { get; set; }
        public string? ListenerLocal, ListenerPort;
        public Func<INodeConnection, string, Task>? AcceptHandler;
        public int Registrations, Disposals;

        // ── dgram (UI) recording ──
        // Last sendto that reached the gateway (the UI TX recorder).
        public string? UiPort, UiLocal, UiRemote;
        public byte? UiPid;
        public byte[]? UiInfo;
        public RhpGatewayException? UiSendFail { get; set; }

        // The inbound-UI injector: the server's RegisterUiListener callback + the port it scoped.
        public Func<UiDatagram, Task>? UiListener;
        public string? UiListenerPort;
        public int UiRegistrations, UiDisposals;

        /// <summary>Signals that the server has entered the connect (an open is in flight).</summary>
        public TaskCompletionSource OpenEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>When set, the connect blocks until it is completed: the seam for driving
        /// what happens to a client while its open is still on the air.</summary>
        public TaskCompletionSource? OpenGate { get; set; }

        public async Task<INodeConnection> OpenAx25StreamAsync(string? portLabel, string? local, string remote, CancellationToken ct = default)
        {
            (LastPort, LastLocal, LastRemote) = (portLabel, local, remote);
            OpenEntered.TrySetResult();
            if (OpenGate is { } gate)
            {
                await gate.Task.WaitAsync(Timeout, ct).ConfigureAwait(false);
            }
            if (Fail is { } f)
            {
                throw f;
            }
            return Connection;
        }

        public IDisposable RegisterListener(string? portLabel, string local, Func<INodeConnection, string, Task> onAccepted)
        {
            if (ListenFail is { } f)
            {
                throw f;
            }
            Registrations++;
            (ListenerPort, ListenerLocal, AcceptHandler) = (portLabel, local, onAccepted);
            return new Unsub(this);
        }

        public Task SendUiAsync(string? portLabel, string local, string remote, ReadOnlyMemory<byte> info, byte pid, CancellationToken ct = default)
        {
            if (UiSendFail is { } f)
            {
                throw f;
            }
            (UiPort, UiLocal, UiRemote, UiPid, UiInfo) = (portLabel, local, remote, pid, info.ToArray());
            return Task.CompletedTask;
        }

        public IDisposable RegisterUiListener(string? portLabel, Func<UiDatagram, Task> onReceived)
        {
            UiRegistrations++;
            (UiListenerPort, UiListener) = (portLabel, onReceived);
            return new UiUnsub(this);
        }

        /// <summary>Drive an inbound UI datagram to a bound dgram handle (stands in for an
        /// over-the-air UI frame the listener would tap).</summary>
        public Task InjectUiAsync(UiDatagram dg) => UiListener?.Invoke(dg) ?? Task.CompletedTask;

        private sealed class Unsub(FakeGateway owner) : IDisposable
        {
            public void Dispose() => owner.Disposals++;
        }

        private sealed class UiUnsub(FakeGateway owner) : IDisposable
        {
            public void Dispose()
            {
                owner.UiDisposals++;
                owner.UiListener = null;
            }
        }
    }

    // An in-memory INodeConnection the tests drive: Inject = bytes "from the peer",
    // WrittenAsync = what the server wrote toward the peer, Drop = the peer vanished.
    internal sealed class FakeConnection : INodeConnection
    {
        private readonly Channel<byte[]> inbound = Channel.CreateUnbounded<byte[]>();
        private readonly Channel<byte[]> written = Channel.CreateUnbounded<byte[]>();
        private readonly TaskCompletionSource completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource disposed = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public string PeerId => "GB7RDG";
        public NodeTransportKind TransportKind => NodeTransportKind.Ax25;
        public Task Completion => completion.Task;
        public Task DisposedTask => disposed.Task;

        public void Inject(byte[] bytes) => inbound.Writer.TryWrite(bytes);

        public void Drop()
        {
            inbound.Writer.TryComplete();
            completion.TrySetResult();
        }

        public async Task<byte[]> WrittenAsync()
            => await written.Reader.ReadAsync(new CancellationTokenSource(Timeout).Token);

        public async ValueTask<ReadOnlyMemory<byte>> ReadAsync(CancellationToken ct = default)
        {
            try
            {
                if (await inbound.Reader.WaitToReadAsync(ct) && inbound.Reader.TryRead(out var chunk))
                {
                    return chunk;
                }
            }
            catch (OperationCanceledException)
            {
                // teardown → EOF
            }
            return ReadOnlyMemory<byte>.Empty;
        }

        public ValueTask WriteAsync(ReadOnlyMemory<byte> bytes, CancellationToken ct = default)
        {
            written.Writer.TryWrite(bytes.ToArray());
            return ValueTask.CompletedTask;
        }

        public ValueTask DisposeAsync()
        {
            Drop();
            disposed.TrySetResult();
            return ValueTask.CompletedTask;
        }
    }

    // A minimal wire-true RHP client: frames via the codec, with typed expectations.
    internal sealed class RhpTestClient : IAsyncDisposable
    {
        private readonly TcpClient tcp;
        private readonly NetworkStream stream;

        private RhpTestClient(TcpClient tcp)
        {
            this.tcp = tcp;
            stream = tcp.GetStream();
        }

        public static async Task<RhpTestClient> ConnectAsync(IPEndPoint endpoint)
        {
            var tcp = new TcpClient();
            await tcp.ConnectAsync(endpoint);
            return new RhpTestClient(tcp);
        }

        public Task SendAsync(RhpMessage msg)
            => RhpFraming.WriteFrameAsync(stream, RhpJson.Serialize(msg));

        public Task SendRawAsync(string json)
            => RhpFraming.WriteFrameAsync(stream, Encoding.UTF8.GetBytes(json));

        public async Task<T> ExpectAsync<T>() where T : RhpMessage
        {
            using var cts = new CancellationTokenSource(Timeout);
            var frame = await RhpFraming.ReadFrameAsync(stream, cts.Token)
                ?? throw new InvalidOperationException("Server closed the connection.");
            var msg = RhpJson.Deserialize(frame);
            return msg.Should().BeOfType<T>().Subject;
        }

        public async Task<(string type, string json)> ReadRawAsync()
        {
            using var cts = new CancellationTokenSource(Timeout);
            var frame = await RhpFraming.ReadFrameAsync(stream, cts.Token)
                ?? throw new InvalidOperationException("Server closed the connection.");
            var json = Encoding.UTF8.GetString(frame);
            var msg = RhpJson.Deserialize(frame);
            return (msg.Type, json);
        }

        public ValueTask DisposeAsync()
        {
            stream.Dispose();
            tcp.Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
