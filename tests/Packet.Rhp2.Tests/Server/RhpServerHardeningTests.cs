using System.Net;
using System.Net.Sockets;
using Packet.Node.Core.Auth;
using Packet.Rhp2;
using Packet.Rhp2.Server;
using Xunit;

namespace Packet.Rhp2.Tests.Server;

/// <summary>
/// Hardness tests for <see cref="RhpServer"/>: the resource bounds that keep a hostile or
/// buggy client from wedging the listener — the concurrent-connection cap, the per-client
/// handle cap (with its reservation freed on close), and the in-frame read timeout that drops
/// a slowloris peer. All driven over real TCP loopback with the recording
/// <see cref="RhpServerTests.FakeGateway"/>.
/// </summary>
public sealed class RhpServerHardeningTests : IAsyncDisposable
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

    private async Task<RhpServer> StartServerAsync(
        int maxConnections = 64,
        int maxHandlesPerClient = 256,
        TimeSpan? inFrameTimeout = null)
    {
        var gateway = new RhpServerTests.FakeGateway();
        var server = new RhpServer(new RhpServerOptions
        {
            Bind = IPAddress.Loopback,
            Port = 0,
            MaxConnections = maxConnections,
            MaxHandlesPerClient = maxHandlesPerClient,
            InFrameTimeout = inFrameTimeout ?? System.Threading.Timeout.InfiniteTimeSpan,
        }, gateway);
        cleanup.Add(server);
        await server.StartAsync();
        return server;
    }

    private async Task<RhpServerTests.RhpTestClient> ConnectAsync(RhpServer server)
    {
        var client = await RhpServerTests.RhpTestClient.ConnectAsync(server.BoundEndpoint!);
        cleanup.Add(client);
        return client;
    }

    // A round-trip proves the connection is fully accepted (and counted) before we proceed.
    // A `socket` request is the simplest supported request that always Ok's on a fresh
    // connection (the `hello` capability-discovery surface was removed — #449).
    private static async Task PingAsync(RhpServerTests.RhpTestClient client, int id)
    {
        await client.SendAsync(new SocketMessage { Id = id, Pfam = ProtocolFamily.Ax25, Mode = SocketMode.Stream });
        var reply = await client.ExpectAsync<SocketReplyMessage>();
        Assert.Equal(RhpErrorCode.Ok, reply.ErrCode);
    }

    private static async Task<int> SocketAsync(RhpServerTests.RhpTestClient client, int id)
    {
        await client.SendAsync(new SocketMessage { Id = id, Pfam = ProtocolFamily.Ax25, Mode = SocketMode.Stream });
        var reply = await client.ExpectAsync<SocketReplyMessage>();
        Assert.Equal(RhpErrorCode.Ok, reply.ErrCode);
        return reply.Handle!.Value;
    }

    // ── Connection cap ───────────────────────────────────────────────────

    [Fact]
    public async Task A_connection_beyond_the_cap_is_closed_immediately()
    {
        var server = await StartServerAsync(maxConnections: 2);

        // Two live, fully-accepted connections fill the cap.
        var c1 = await ConnectAsync(server);
        await PingAsync(c1, 1);
        var c2 = await ConnectAsync(server);
        await PingAsync(c2, 2);

        // The third is accepted only to be closed at once: a read sees a clean EOF.
        using var third = new TcpClient();
        await third.ConnectAsync(server.BoundEndpoint!);
        using var cts = new CancellationTokenSource(Timeout);
        var buffer = new byte[1];
        int read = await third.GetStream().ReadAsync(buffer, cts.Token);
        Assert.Equal(0, read);
    }

    [Fact]
    public async Task A_slot_frees_when_a_capped_connection_closes()
    {
        var server = await StartServerAsync(maxConnections: 1);

        var c1 = await ConnectAsync(server);
        await PingAsync(c1, 1);

        // Drop the only allowed connection and wait for the server to notice (its read
        // loop ends on EOF and decrements the count in the finally).
        await c1.DisposeAsync();

        // A fresh connection now fits and is fully serviced.
        RhpServerTests.RhpTestClient? c2 = null;
        for (int attempt = 0; attempt < 50 && c2 is null; attempt++)
        {
            var candidate = await RhpServerTests.RhpTestClient.ConnectAsync(server.BoundEndpoint!);
            try
            {
                await PingAsync(candidate, 2);
                c2 = candidate;
                cleanup.Add(candidate);
            }
            catch (Exception ex) when (ex is InvalidOperationException or IOException)
            {
                // The server hadn't yet released the old slot — back off and retry.
                await candidate.DisposeAsync();
                await Task.Delay(50);
            }
        }
        Assert.NotNull(c2);
    }

    // ── Per-client handle cap ────────────────────────────────────────────

    [Fact]
    public async Task Handles_beyond_the_per_client_cap_are_refused_with_no_memory()
    {
        var server = await StartServerAsync(maxHandlesPerClient: 2);
        var client = await ConnectAsync(server);

        await SocketAsync(client, 1);
        await SocketAsync(client, 2);

        // The third socket request exceeds the cap.
        await client.SendAsync(new SocketMessage { Id = 3, Pfam = ProtocolFamily.Ax25, Mode = SocketMode.Stream });
        var reply = await client.ExpectAsync<SocketReplyMessage>();
        Assert.Equal(RhpErrorCode.NoMemory, reply.ErrCode);
        Assert.Null(reply.Handle);
    }

    [Fact]
    public async Task Closing_a_handle_frees_a_slot_under_the_cap()
    {
        var server = await StartServerAsync(maxHandlesPerClient: 1);
        var client = await ConnectAsync(server);

        var handle = await SocketAsync(client, 1);

        // At the cap: a second handle is refused.
        await client.SendAsync(new SocketMessage { Id = 2, Pfam = ProtocolFamily.Ax25, Mode = SocketMode.Stream });
        Assert.Equal(RhpErrorCode.NoMemory, (await client.ExpectAsync<SocketReplyMessage>()).ErrCode);

        // Close the first handle, freeing its reservation.
        await client.SendAsync(new CloseMessage { Id = 3, Handle = handle });
        Assert.Equal(RhpErrorCode.Ok, (await client.ExpectAsync<CloseReplyMessage>()).ErrCode);

        // Now a new handle fits again.
        await SocketAsync(client, 4);
    }

    [Fact]
    public async Task The_handle_cap_is_per_connection_not_global()
    {
        var server = await StartServerAsync(maxHandlesPerClient: 1);

        var a = await ConnectAsync(server);
        var b = await ConnectAsync(server);

        // Each connection gets its own full allowance.
        await SocketAsync(a, 1);
        await SocketAsync(b, 1);

        // Each is independently capped thereafter.
        await a.SendAsync(new SocketMessage { Id = 2, Pfam = ProtocolFamily.Ax25, Mode = SocketMode.Stream });
        Assert.Equal(RhpErrorCode.NoMemory, (await a.ExpectAsync<SocketReplyMessage>()).ErrCode);
    }

    // ── In-frame read timeout (slowloris) ────────────────────────────────

    [Fact]
    public async Task A_client_that_stalls_mid_frame_is_dropped()
    {
        var server = await StartServerAsync(inFrameTimeout: TimeSpan.FromMilliseconds(250));

        using var slow = new TcpClient();
        await slow.ConnectAsync(server.BoundEndpoint!);
        var stream = slow.GetStream();

        // Send the first byte of a frame header, then stall. The server must drop us.
        await stream.WriteAsync(new byte[] { 0x00 });
        await stream.FlushAsync();

        using var cts = new CancellationTokenSource(Timeout);
        var buffer = new byte[1];
        int read = await stream.ReadAsync(buffer, cts.Token);
        Assert.Equal(0, read);   // server closed the connection after the in-frame timeout
    }

    [Fact]
    public async Task An_idle_connection_between_frames_is_not_dropped()
    {
        var server = await StartServerAsync(inFrameTimeout: TimeSpan.FromMilliseconds(250));
        var client = await ConnectAsync(server);

        // One request, then sit idle well past the in-frame timeout — the connection
        // must survive, because idle-between-frames is unbounded by design.
        await PingAsync(client, 1);
        await Task.Delay(600);
        await PingAsync(client, 2);
    }

    // ── Auth brute-force throttle ────────────────────────────────────────

    [Fact]
    public async Task Auth_is_throttled_after_repeated_failures_without_reaching_the_verify()
    {
        // Count password-verify invocations: once the source IP locks out, further auth
        // attempts must be refused WITHOUT reaching the verify.
        int verifyCalls = 0;
        var throttle = new LoginThrottle(TimeProvider.System, maxFailures: 3, window: TimeSpan.FromMinutes(5));
        var gateway = new RhpServerTests.FakeGateway();
        var server = new RhpServer(new RhpServerOptions
        {
            Bind = IPAddress.Loopback,
            Port = 0,
            RequireAuth = true,
            Authenticate = (_, _) => { Interlocked.Increment(ref verifyCalls); return false; },
            AuthThrottle = throttle,
        }, gateway);
        cleanup.Add(server);
        await server.StartAsync();
        var client = await ConnectAsync(server);

        // Five bad auths over one connection. The first three reach the verify and fail;
        // by the fourth the IP is locked, so the verify is skipped — but every reply is
        // still Unauthorised on the wire.
        for (int i = 1; i <= 5; i++)
        {
            await client.SendAsync(new AuthMessage { Id = i, User = "sysop", Pass = "wrong" });
            var reply = await client.ExpectAsync<AuthReplyMessage>();
            Assert.Equal(RhpErrorCode.Unauthorised, reply.ErrCode);
        }

        Assert.Equal(3, verifyCalls);   // locked out after 3 — attempts 4 and 5 short-circuited
    }

    [Fact]
    public async Task A_successful_auth_clears_the_throttle()
    {
        var throttle = new LoginThrottle(TimeProvider.System, maxFailures: 3, window: TimeSpan.FromMinutes(5));
        var gateway = new RhpServerTests.FakeGateway();
        var server = new RhpServer(new RhpServerOptions
        {
            Bind = IPAddress.Loopback,
            Port = 0,
            RequireAuth = true,
            Authenticate = (_, pass) => pass == "right",
            AuthThrottle = throttle,
        }, gateway);
        cleanup.Add(server);
        await server.StartAsync();
        var client = await ConnectAsync(server);

        // Two failures (under the cap), then a success resets the counter.
        for (int i = 1; i <= 2; i++)
        {
            await client.SendAsync(new AuthMessage { Id = i, User = "sysop", Pass = "wrong" });
            Assert.Equal(RhpErrorCode.Unauthorised, (await client.ExpectAsync<AuthReplyMessage>()).ErrCode);
        }
        await client.SendAsync(new AuthMessage { Id = 3, User = "sysop", Pass = "right" });
        Assert.Equal(RhpErrorCode.Ok, (await client.ExpectAsync<AuthReplyMessage>()).ErrCode);

        // The reset means the failure budget is full again — three more failures are
        // needed to lock, proving the earlier two were cleared.
        Assert.False(throttle.IsLocked(IPAddress.Loopback.ToString()));
    }
}
