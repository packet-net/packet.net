using System.Net;
using Packet.Rhp2;
using Packet.Rhp2.Server;
using Xunit;

namespace Packet.Rhp2.Tests.Server;

/// <summary>
/// The passive half of the RHPv2 server (R-3): the BSD-style <c>socket</c> → <c>bind</c> →
/// <c>listen</c> lifecycle, the async <c>accept</c> push (child handle, string <c>port</c> —
/// the XRouter wire shape), child-handle traffic, and the close/teardown semantics — driven
/// over real TCP with the recording <see cref="RhpServerTests.FakeGateway"/>, mirroring the
/// exact sequence DAPPS's inbound service sends (bind with a NULL port = all ports).
/// </summary>
public sealed class RhpServerPassiveTests : IAsyncDisposable
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

    private async Task<(RhpServer server, RhpServerTests.FakeGateway gateway)> StartServerAsync()
    {
        var gateway = new RhpServerTests.FakeGateway();
        var server = new RhpServer(new RhpServerOptions { Bind = IPAddress.Loopback, Port = 0 }, gateway);
        cleanup.Add(server);
        await server.StartAsync();
        return (server, gateway);
    }

    private async Task<RhpServerTests.RhpTestClient> ConnectAsync(RhpServer server)
    {
        var client = await RhpServerTests.RhpTestClient.ConnectAsync(server.BoundEndpoint!);
        cleanup.Add(client);
        return client;
    }

    // The DAPPS inbound sequence: socket → bind(callsign, port:null) → listen.
    private static async Task<int> ListenAsync(RhpServerTests.RhpTestClient client, RhpServerTests.FakeGateway gateway, string callsign = "M0LTE-7")
    {
        await client.SendAsync(new SocketMessage { Id = 1, Pfam = ProtocolFamily.Ax25, Mode = SocketMode.Stream });
        var sock = await client.ExpectAsync<SocketReplyMessage>();
        Assert.Equal(RhpErrorCode.Ok, sock.ErrCode);
        var handle = sock.Handle!.Value;

        await client.SendAsync(new BindMessage { Id = 2, Handle = handle, Local = callsign, Port = null });
        var bind = await client.ExpectAsync<BindReplyMessage>();
        Assert.Equal(RhpErrorCode.Ok, bind.ErrCode);

        await client.SendAsync(new ListenMessage { Id = 3, Handle = handle, Flags = 0 });
        var listen = await client.ExpectAsync<ListenReplyMessage>();
        Assert.Equal(RhpErrorCode.Ok, listen.ErrCode);
        Assert.Equal((callsign, null as string), (gateway.ListenerLocal, gateway.ListenerPort));   // null port = all ports
        return handle;
    }

    [Fact]
    public async Task Socket_bind_listen_registers_the_callsign_with_the_engine()
    {
        var (server, gateway) = await StartServerAsync();
        var client = await ConnectAsync(server);

        var handle = await ListenAsync(client, gateway);

        Assert.True(handle >= 100);
        Assert.Equal(1, gateway.Registrations);
    }

    [Fact]
    public async Task An_inbound_connection_is_announced_with_an_accept_push_and_a_live_child()
    {
        var (server, gateway) = await StartServerAsync();
        var client = await ConnectAsync(server);
        var listener = await ListenAsync(client, gateway);

        // A station connects to the bound callsign on port "2" — drive the engine callback.
        await gateway.AcceptHandler!(gateway.Connection, "2");

        var accept = await client.ExpectAsync<AcceptMessage>();
        Assert.Equal(listener, accept.Handle);
        Assert.True(accept.Child > listener);
        Assert.Equal("GB7RDG", accept.Remote);          // the FakeConnection's PeerId
        Assert.Equal("M0LTE-7", accept.Local);
        Assert.Equal("2", accept.Port);                 // the wire's STRING port (XRouter shape)
        Assert.Null(accept.Id);
        Assert.NotNull(accept.Seqno);

        // The child handle is live both ways: peer bytes push as recv; send reaches the peer.
        gateway.Connection.Inject("hi\r"u8.ToArray());
        var recv = await client.ExpectAsync<RecvMessage>();
        Assert.Equal(accept.Child, recv.Handle);
        Assert.Equal("hi\r", recv.Data);

        await client.SendAsync(new SendMessage { Id = 9, Handle = accept.Child, Data = "DAPPSv1>\r" });
        var sendReply = await client.ExpectAsync<SendReplyMessage>();
        Assert.Equal(RhpErrorCode.Ok, sendReply.ErrCode);
        Assert.Equal("DAPPSv1>\r"u8.ToArray(), await gateway.Connection.WrittenAsync());
    }

    [Fact]
    public async Task A_second_listen_on_the_same_socket_is_idempotent_ok()
    {
        // Observed live XRouter behaviour (the R-4 wire-diff oracle): re-listen on the SAME
        // already-listening socket answers Ok, not 9 — and must not double-register.
        var (server, gateway) = await StartServerAsync();
        var client = await ConnectAsync(server);
        var handle = await ListenAsync(client, gateway);

        await client.SendAsync(new ListenMessage { Id = 4, Handle = handle, Flags = 0 });
        var again = await client.ExpectAsync<ListenReplyMessage>();
        Assert.Equal(RhpErrorCode.Ok, again.ErrCode);
        Assert.Equal(1, gateway.Registrations);   // no second engine registration
    }

    [Fact]
    public async Task An_engine_duplicate_lands_on_the_listen_reply_verbatim()
    {
        var (server, gateway) = await StartServerAsync();
        gateway.ListenFail = new RhpGatewayException(RhpErrorCode.DuplicateSocket, "callsign M0LTE-7 is already registered.");
        var client = await ConnectAsync(server);

        await client.SendAsync(new SocketMessage { Id = 1, Pfam = ProtocolFamily.Ax25, Mode = SocketMode.Stream });
        var sock = await client.ExpectAsync<SocketReplyMessage>();
        await client.SendAsync(new BindMessage { Id = 2, Handle = sock.Handle!.Value, Local = "M0LTE-7" });
        _ = await client.ExpectAsync<BindReplyMessage>();
        await client.SendAsync(new ListenMessage { Id = 3, Handle = sock.Handle!.Value, Flags = 0 });

        var listen = await client.ExpectAsync<ListenReplyMessage>();
        Assert.Equal(RhpErrorCode.DuplicateSocket, listen.ErrCode);
        Assert.Equal("callsign M0LTE-7 is already registered.", listen.ErrText);
    }

    [Theory]
    [InlineData("nonsense", "stream", RhpErrorCode.BadOrMissingFamily)]
    [InlineData("inet", "stream", RhpErrorCode.OperationNotSupported)]
    [InlineData("ax25", "dgram", RhpErrorCode.OperationNotSupported)]
    public async Task Socket_validation_ladder(string pfam, string mode, int expected)
    {
        var (server, _) = await StartServerAsync();
        var client = await ConnectAsync(server);

        await client.SendAsync(new SocketMessage { Id = 1, Pfam = pfam, Mode = mode });
        var reply = await client.ExpectAsync<SocketReplyMessage>();
        Assert.Equal(expected, reply.ErrCode);
    }

    [Fact]
    public async Task Listen_before_bind_is_a_bad_parameter()
    {
        var (server, _) = await StartServerAsync();
        var client = await ConnectAsync(server);

        await client.SendAsync(new SocketMessage { Id = 1, Pfam = ProtocolFamily.Ax25, Mode = SocketMode.Stream });
        var sock = await client.ExpectAsync<SocketReplyMessage>();
        await client.SendAsync(new ListenMessage { Id = 2, Handle = sock.Handle!.Value, Flags = 0 });

        var listen = await client.ExpectAsync<ListenReplyMessage>();
        Assert.Equal(RhpErrorCode.BadParameter, listen.ErrCode);
    }

    [Fact]
    public async Task Closing_the_listener_stops_listening_but_leaves_children_alive()
    {
        var (server, gateway) = await StartServerAsync();
        var client = await ConnectAsync(server);
        var listener = await ListenAsync(client, gateway);

        await gateway.AcceptHandler!(gateway.Connection, "1");
        var accept = await client.ExpectAsync<AcceptMessage>();

        await client.SendAsync(new CloseMessage { Id = 5, Handle = listener });
        var closed = await client.ExpectAsync<CloseReplyMessage>();
        Assert.Equal(RhpErrorCode.Ok, closed.ErrCode);
        Assert.Equal(1, gateway.Disposals);             // the engine registration is gone

        // The accepted child is an independent handle — still live.
        await client.SendAsync(new SendMessage { Id = 6, Handle = accept.Child, Data = "x" });
        var sendReply = await client.ExpectAsync<SendReplyMessage>();
        Assert.Equal(RhpErrorCode.Ok, sendReply.ErrCode);
    }

    [Fact]
    public async Task A_dropped_client_connection_releases_its_listener_registration()
    {
        var (server, gateway) = await StartServerAsync();
        var client = await ConnectAsync(server);
        _ = await ListenAsync(client, gateway);

        await client.DisposeAsync();   // the RHP TCP connection drops

        // All sockets/handles die with their connection (PWP-0222) — the registration too.
        var deadline = DateTime.UtcNow + Timeout;
        while (gateway.Disposals == 0 && DateTime.UtcNow < deadline)
        {
            await Task.Delay(20);
        }
        Assert.Equal(1, gateway.Disposals);
    }
}
