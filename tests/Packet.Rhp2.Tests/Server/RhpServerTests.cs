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

    // ── The validation ladder + gateway error passthrough ────────────────

    [Theory]
    [InlineData("nonsense", "stream", 0x80, "GB7RDG", RhpErrorCode.BadOrMissingFamily)]
    [InlineData("inet", "stream", 0x80, "GB7RDG", RhpErrorCode.OperationNotSupported)]     // valid family, not implemented
    [InlineData("ax25", "warble", 0x80, "GB7RDG", RhpErrorCode.BadOrMissingMode)]
    [InlineData("ax25", "dgram", 0x80, "GB7RDG", RhpErrorCode.OperationNotSupported)]      // valid mode, not implemented
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

    // ── helpers ───────────────────────────────────────────────────────────

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

        private sealed class Unsub(FakeGateway owner) : IDisposable
        {
            public void Dispose() => owner.Disposals++;
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
