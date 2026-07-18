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
        Assert.Equal(7, reply.Id);                         // reply echoes the request id
        Assert.Equal(RhpErrorCode.Ok, reply.ErrCode);
        Assert.Equal("Ok", reply.ErrText);
        Assert.True(reply.Handle >= 100);                  // reference-style numbering

        var status = await client.ExpectAsync<StatusMessage>();
        Assert.Null(status.Id);                            // a push, not a reply
        Assert.NotNull(status.Seqno);
        Assert.Equal(reply.Handle, status.Handle);
        Assert.True(((StatusFlags)(status.Flags ?? 0)).HasFlag(StatusFlags.Connected));

        Assert.Equal(("1", "M0LTE-1", "GB7RDG"), (gateway.LastPort, gateway.LastLocal, gateway.LastRemote));
    }

    [Fact]
    public async Task Send_writes_decoded_bytes_to_the_session_and_replies_ok()
    {
        var (server, gateway) = await StartServerAsync();
        var client = await ConnectAsync(server);
        var handle = await OpenAsync(client);

        await client.SendAsync(new SendMessage { Id = 9, Handle = handle, Data = "N\r" });

        var reply = await client.ExpectAsync<SendReplyMessage>();
        Assert.Equal(9, reply.Id);
        Assert.Equal(RhpErrorCode.Ok, reply.ErrCode);
        var written = await gateway.Connection.WrittenAsync();
        Assert.Equal("N\r"u8.ToArray(), written);
    }

    [Fact]
    public async Task Session_bytes_are_pushed_as_recv_with_seqno_and_no_id()
    {
        var (server, gateway) = await StartServerAsync();
        var client = await ConnectAsync(server);
        var handle = await OpenAsync(client);

        gateway.Connection.Inject("GB7RDG:GLOSTR} hello\r"u8.ToArray());

        var recv = await client.ExpectAsync<RecvMessage>();
        Assert.Equal(handle, recv.Handle);
        Assert.Null(recv.Id);
        Assert.NotNull(recv.Seqno);
        Assert.Equal("GB7RDG:GLOSTR} hello\r", recv.Data);
    }

    [Fact]
    public async Task Client_close_tears_down_the_session_and_replies_ok()
    {
        var (server, gateway) = await StartServerAsync();
        var client = await ConnectAsync(server);
        var handle = await OpenAsync(client);

        await client.SendAsync(new CloseMessage { Id = 3, Handle = handle });

        var reply = await client.ExpectAsync<CloseReplyMessage>();
        Assert.Equal(RhpErrorCode.Ok, reply.ErrCode);
        await gateway.Connection.DisposedTask.WaitAsync(Timeout);   // DISC posted

        // The handle is gone: a second close is an invalid handle.
        await client.SendAsync(new CloseMessage { Id = 4, Handle = handle });
        var again = await client.ExpectAsync<CloseReplyMessage>();
        Assert.Equal(RhpErrorCode.InvalidHandle, again.ErrCode);
    }

    [Fact]
    public async Task Peer_drop_pushes_a_server_initiated_close()
    {
        var (server, gateway) = await StartServerAsync();
        var client = await ConnectAsync(server);
        var handle = await OpenAsync(client);

        gateway.Connection.Drop();   // the far station disconnected

        var close = await client.ExpectAsync<CloseMessage>();
        Assert.Equal(handle, close.Handle);
        Assert.Null(close.Id);
        Assert.NotNull(close.Seqno);
    }

    // ── seqno: per-RHP-connection, starting at 0, shared across push types ─
    //    (RHPTEST-verified: first push is seqno 0; live XRouter confirms — a fresh
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
        Assert.Equal(0, status.Seqno);                     // not 1 — the counter starts at 0
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
        Assert.Equal(0, status.Seqno);

        gateway.Connection.Inject("one\r"u8.ToArray());
        Assert.Equal(1, (await client.ExpectAsync<RecvMessage>()).Seqno);

        gateway.Connection.Inject("two\r"u8.ToArray());
        Assert.Equal(2, (await client.ExpectAsync<RecvMessage>()).Seqno);

        gateway.Connection.Drop();
        Assert.Equal(3, (await client.ExpectAsync<CloseMessage>()).Seqno);
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
        Assert.Equal(0, (await alice.ExpectAsync<StatusMessage>()).Seqno);

        // ...so Bob's first push is still seqno 0 (a server-wide counter would give 1 here).
        await bob.SendAsync(new OpenMessage
        { Id = 1, Pfam = ProtocolFamily.Ax25, Mode = SocketMode.Stream, Remote = "GB7RDG", Flags = (int)OpenFlags.Active });
        _ = await bob.ExpectAsync<OpenReplyMessage>();
        Assert.Equal(0, (await bob.ExpectAsync<StatusMessage>()).Seqno);
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
        Assert.Equal(RhpErrorCode.BadParameter, reply.ErrCode);
        Assert.Equal("Missing data", reply.ErrText);       // the live wire's exact errText
        Assert.Equal(5, reply.Id);
    }

    [Fact]
    public async Task Send_with_empty_data_is_a_legal_zero_byte_send()
    {
        var (server, gateway) = await StartServerAsync();
        var client = await ConnectAsync(server);
        var handle = await OpenAsync(client);

        await client.SendAsync(new SendMessage { Id = 6, Handle = handle, Data = "" });

        var reply = await client.ExpectAsync<SendReplyMessage>();
        Assert.Equal(RhpErrorCode.Ok, reply.ErrCode);      // "" ≠ absent — RHPTEST's zero-byte send
        Assert.Empty(await gateway.Connection.WrittenAsync());
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
        Assert.Equal(expectedReplyType, type);
        Assert.Contains("\"errCode\":12", json, StringComparison.Ordinal);
        Assert.Contains("\"errText\":\"Missing handle\"", json, StringComparison.Ordinal);
        Assert.Contains("\"id\":3", json, StringComparison.Ordinal);
        Assert.DoesNotContain("\"handle\"", json, StringComparison.Ordinal);   // nothing truthful to echo
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
        Assert.Equal(RhpErrorCode.InvalidLocalAddress, reply.ErrCode);
        Assert.Equal("Invalid local address", reply.ErrText);   // a clean 6, not a generic failure
    }

    [Fact]
    public async Task Open_with_an_alphabetic_SSID_remote_is_refused_with_7()
    {
        var (server, _) = await StartServerAsync();
        var client = await ConnectAsync(server);

        await client.SendAsync(new OpenMessage
        { Id = 1, Pfam = ProtocolFamily.Ax25, Mode = SocketMode.Stream, Remote = "G9DUM-S", Flags = (int)OpenFlags.Active });

        var reply = await client.ExpectAsync<OpenReplyMessage>();
        Assert.Equal(RhpErrorCode.InvalidRemoteAddress, reply.ErrCode);
    }

    [Fact]
    public async Task Open_without_local_is_accepted_and_defaults_to_the_node_callsign()
    {
        // Deviation D8: requiring `local` on open is an XRouter-ism, not an RHP rule
        // (RHPTEST quotes the author saying exactly that). pdn stays permissive — a null
        // local reaches the gateway, which dials as the node's own callsign.
        var (server, gateway) = await StartServerAsync();
        var client = await ConnectAsync(server);

        await client.SendAsync(new OpenMessage
        { Id = 1, Pfam = ProtocolFamily.Ax25, Mode = SocketMode.Stream, Remote = "GB7RDG", Flags = (int)OpenFlags.Active });

        var reply = await client.ExpectAsync<OpenReplyMessage>();
        Assert.Equal(RhpErrorCode.Ok, reply.ErrCode);
        Assert.Null(gateway.LastLocal);
    }

    // ── The validation ladder + gateway error passthrough ────────────────

    [Theory]
    [InlineData("nonsense", "stream", 0x80, "GB7RDG", RhpErrorCode.BadOrMissingFamily)]
    [InlineData("inet", "stream", 0x80, "GB7RDG", RhpErrorCode.OperationNotSupported)]     // valid family, not implemented
    [InlineData("ax25", "warble", 0x80, "GB7RDG", RhpErrorCode.BadOrMissingMode)]
    [InlineData("ax25", "dgram", 0x80, "GB7RDG", RhpErrorCode.Ok)]                          // dgram open = the combined socket+bind form (R-6)
    [InlineData("ax25", "stream", 0x00, "GB7RDG", RhpErrorCode.OperationNotSupported)]     // passive = R-3
    [InlineData("ax25", "stream", 0x80, null, RhpErrorCode.InvalidRemoteAddress)]
    public async Task Open_validation_ladder(string pfam, string mode, int flags, string? remote, int expected)
    {
        var (server, _) = await StartServerAsync();
        var client = await ConnectAsync(server);

        await client.SendAsync(new OpenMessage { Id = 1, Pfam = pfam, Mode = mode, Flags = flags, Remote = remote });

        var reply = await client.ExpectAsync<OpenReplyMessage>();
        Assert.Equal(expected, reply.ErrCode);
        Assert.Equal(1, reply.Id);
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
        Assert.Equal(RhpErrorCode.NoRoute, reply.ErrCode);
        Assert.Equal("Connect to GB7RDG timed out.", reply.ErrText);
    }

    [Fact]
    public async Task Send_on_an_unknown_handle_is_invalid_handle()
    {
        var (server, _) = await StartServerAsync();
        var client = await ConnectAsync(server);

        await client.SendAsync(new SendMessage { Id = 1, Handle = 12345, Data = "x" });

        var reply = await client.ExpectAsync<SendReplyMessage>();
        Assert.Equal(RhpErrorCode.InvalidHandle, reply.ErrCode);
    }

    // ── XRouter wire contracts: unknown type, R-3 deferrals ───────────────

    [Fact]
    public async Task Unknown_type_is_answered_with_typeReply_errCode_2()
    {
        var (server, _) = await StartServerAsync();
        var client = await ConnectAsync(server);

        await client.SendRawAsync("""{"type":"thisIsNotReal","id":42}""");

        var (type, json) = await client.ReadRawAsync();
        Assert.Equal("thisIsNotRealReply", type);
        Assert.Contains("\"errCode\":2", json, StringComparison.Ordinal);
        Assert.Contains("\"id\":42", json, StringComparison.Ordinal);
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
        Assert.Equal(RhpErrorCode.Unauthorised, refused.ErrCode);

        // Bad auth fails THAT attempt only (D2: no connection wedge)...
        await client.SendAsync(new AuthMessage { Id = 2, User = "tom", Pass = "wrong" });
        var bad = await client.ExpectAsync<AuthReplyMessage>();
        Assert.Equal(RhpErrorCode.Unauthorised, bad.ErrCode);

        // ...a subsequent good auth on the SAME connection succeeds and unlocks it.
        await client.SendAsync(new AuthMessage { Id = 3, User = "tom", Pass = "pw" });
        var good = await client.ExpectAsync<AuthReplyMessage>();
        Assert.Equal(RhpErrorCode.Ok, good.ErrCode);

        await client.SendAsync(new OpenMessage
        { Id = 4, Pfam = ProtocolFamily.Ax25, Mode = SocketMode.Stream, Remote = "GB7RDG", Flags = (int)OpenFlags.Active });
        var opened = await client.ExpectAsync<OpenReplyMessage>();
        Assert.Equal(RhpErrorCode.Ok, opened.ErrCode);
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
        Assert.Equal(RhpErrorCode.InvalidHandle, reply.ErrCode);   // same as unknown — no oracle
    }

    // ── dgram (UI): socket → bind → sendto (TX) + async recv (RX) — R-6 ───

    [Fact]
    public async Task Socket_bind_sendto_emits_a_ui_datagram_with_the_right_source_dest_pid_and_data()
    {
        var (server, gateway) = await StartServerAsync();
        var client = await ConnectAsync(server);
        var handle = await BindDgramAsync(client, "M0LTE-1", port: "1");

        // IP-over-AX.25: bind the station, sendto pid 0xCC to the destination.
        await client.SendAsync(new SendToMessage
        {
            Id = 9, Handle = handle, Remote = "GB7RDG", Data = "hi\r", Pid = 0xCC,
        });

        var reply = await client.ExpectAsync<SendToReplyMessage>();
        Assert.Equal(9, reply.Id);
        Assert.Equal(RhpErrorCode.Ok, reply.ErrCode);
        Assert.Equal(("1", "M0LTE-1", "GB7RDG"), (gateway.UiPort, gateway.UiLocal, gateway.UiRemote));
        Assert.Equal((byte)0xCC, gateway.UiPid);
        Assert.Equal("hi\r"u8.ToArray(), gateway.UiInfo);
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
        Assert.Equal(RhpErrorCode.Ok, reply.ErrCode);
        Assert.Equal((byte)0xF0, gateway.UiPid);
        Assert.Equal("APRS", gateway.UiRemote);
    }

    [Fact]
    public async Task Sendto_source_falls_back_to_the_bound_local_when_omitted()
    {
        var (server, gateway) = await StartServerAsync();
        var client = await ConnectAsync(server);
        var handle = await BindDgramAsync(client, "M0LTE-1");

        await client.SendAsync(new SendToMessage { Id = 1, Handle = handle, Remote = "GB7RDG", Data = "x" });

        var reply = await client.ExpectAsync<SendToReplyMessage>();
        Assert.Equal(RhpErrorCode.Ok, reply.ErrCode);
        Assert.Equal("M0LTE-1", gateway.UiLocal);   // the bound local becomes the frame source
    }

    [Fact]
    public async Task Sendto_with_an_empty_payload_is_rejected_errCode_1()
    {
        // AX.25 UI, unlike UDP, carries no zero-byte datagram — errCode 1 (protocol.md).
        var (server, _) = await StartServerAsync();
        var client = await ConnectAsync(server);
        var handle = await BindDgramAsync(client, "M0LTE-1");

        await client.SendAsync(new SendToMessage { Id = 4, Handle = handle, Remote = "GB7RDG", Data = "" });

        var reply = await client.ExpectAsync<SendToReplyMessage>();
        Assert.Equal(RhpErrorCode.Unspecified, reply.ErrCode);   // 1, distinct from 12 (absent data)
        Assert.Equal(4, reply.Id);
    }

    [Fact]
    public async Task Listen_on_a_dgram_socket_is_operation_not_supported_16()
    {
        var (server, _) = await StartServerAsync();
        var client = await ConnectAsync(server);

        await client.SendAsync(new SocketMessage { Id = 1, Pfam = ProtocolFamily.Ax25, Mode = SocketMode.Dgram });
        var sock = await client.ExpectAsync<SocketReplyMessage>();
        Assert.Equal(RhpErrorCode.Ok, sock.ErrCode);

        await client.SendAsync(new ListenMessage { Id = 2, Handle = sock.Handle!.Value, Flags = 0 });

        var listen = await client.ExpectAsync<ListenReplyMessage>();
        Assert.Equal(RhpErrorCode.OperationNotSupported, listen.ErrCode);   // a datagram has no listening state
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
        Assert.Equal(handle, recv.Handle);
        Assert.Null(recv.Id);                       // a push, not a reply
        Assert.NotNull(recv.Seqno);                 // carries a seqno, not an id
        Assert.Equal("2E0XYZ", recv.Remote);        // the frame's true source → recv.remote
        Assert.Equal("M0LTE-1", recv.Local);        // the frame's destination → recv.local
        Assert.Equal("1", recv.Port);               // the arrival port label
        Assert.Equal(0xCC, recv.Pid);               // the frame's PID surfaced
        Assert.Equal("ping", recv.Data);
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
        Assert.Equal(RhpErrorCode.Ok, open.ErrCode);
        Assert.True(open.Handle >= 100);

        await client.SendAsync(new SendToMessage { Id = 2, Handle = open.Handle, Remote = "GB7RDG", Data = "y" });
        var reply = await client.ExpectAsync<SendToReplyMessage>();
        Assert.Equal(RhpErrorCode.Ok, reply.ErrCode);
        Assert.Equal(("1", "M0LTE-1", "GB7RDG"), (gateway.UiPort, gateway.UiLocal, gateway.UiRemote));
    }

    // ── helpers ───────────────────────────────────────────────────────────

    // socket(dgram) → bind(callsign[, port]) → returns the bound dgram handle.
    private static async Task<int> BindDgramAsync(RhpTestClient client, string callsign, string? port = null)
    {
        await client.SendAsync(new SocketMessage { Id = 1, Pfam = ProtocolFamily.Ax25, Mode = SocketMode.Dgram });
        var sock = await client.ExpectAsync<SocketReplyMessage>();
        Assert.Equal(RhpErrorCode.Ok, sock.ErrCode);

        await client.SendAsync(new BindMessage { Id = 2, Handle = sock.Handle!.Value, Local = callsign, Port = port });
        var bind = await client.ExpectAsync<BindReplyMessage>();
        Assert.Equal(RhpErrorCode.Ok, bind.ErrCode);
        return sock.Handle!.Value;
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
        Assert.Equal(RhpErrorCode.Ok, reply.ErrCode);
        _ = await client.ExpectAsync<StatusMessage>();   // swallow the connected push
        return reply.Handle;
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

        public Task<INodeConnection> OpenAx25StreamAsync(string? portLabel, string? local, string remote, CancellationToken ct = default)
        {
            (LastPort, LastLocal, LastRemote) = (portLabel, local, remote);
            if (Fail is { } f)
            {
                throw f;
            }
            return Task.FromResult<INodeConnection>(Connection);
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
            return Assert.IsType<T>(msg);
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
