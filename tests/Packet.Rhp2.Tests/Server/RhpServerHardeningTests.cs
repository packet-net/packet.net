using System.Net;
using System.Net.Sockets;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using Packet.Node.Core.Auth;
using Packet.Rhp2;
using Packet.Rhp2.Server;
using Xunit;

namespace Packet.Rhp2.Tests.Server;

/// <summary>
/// Hardness tests for <see cref="RhpServer"/>: the resource bounds that keep a hostile or
/// buggy client from wedging the listener - the concurrent-connection cap, the per-client
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
        TimeSpan? inFrameTimeout = null,
        TimeProvider? timeProvider = null)
    {
        var gateway = new RhpServerTests.FakeGateway();
        var server = new RhpServer(new RhpServerOptions
        {
            Bind = IPAddress.Loopback,
            Port = 0,
            MaxConnections = maxConnections,
            MaxHandlesPerClient = maxHandlesPerClient,
            InFrameTimeout = inFrameTimeout ?? System.Threading.Timeout.InfiniteTimeSpan,
            TimeProvider = timeProvider ?? TimeProvider.System,
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
    // connection (the `hello` capability-discovery surface was removed - #449).
    private static async Task PingAsync(RhpServerTests.RhpTestClient client, int id)
    {
        await client.SendAsync(new SocketMessage { Id = id, Pfam = ProtocolFamily.Ax25, Mode = SocketMode.Stream });
        var reply = await client.ExpectAsync<SocketReplyMessage>();
        reply.ErrCode.Should().Be(RhpErrorCode.Ok);
    }

    private static async Task<int> SocketAsync(RhpServerTests.RhpTestClient client, int id)
    {
        await client.SendAsync(new SocketMessage { Id = id, Pfam = ProtocolFamily.Ax25, Mode = SocketMode.Stream });
        var reply = await client.ExpectAsync<SocketReplyMessage>();
        reply.ErrCode.Should().Be(RhpErrorCode.Ok);
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
        read.Should().Be(0);
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
                // The server hadn't yet released the old slot - back off and retry.
                await candidate.DisposeAsync();
                await Task.Delay(50);
            }
        }
        c2.Should().NotBeNull();
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
        reply.ErrCode.Should().Be(RhpErrorCode.NoMemory);
        reply.Handle.Should().BeNull();
    }

    [Fact]
    public async Task Closing_a_handle_frees_a_slot_under_the_cap()
    {
        var server = await StartServerAsync(maxHandlesPerClient: 1);
        var client = await ConnectAsync(server);

        var handle = await SocketAsync(client, 1);

        // At the cap: a second handle is refused.
        await client.SendAsync(new SocketMessage { Id = 2, Pfam = ProtocolFamily.Ax25, Mode = SocketMode.Stream });
        (await client.ExpectAsync<SocketReplyMessage>()).ErrCode.Should().Be(RhpErrorCode.NoMemory);

        // Close the first handle, freeing its reservation.
        await client.SendAsync(new CloseMessage { Id = 3, Handle = handle });
        (await client.ExpectAsync<CloseReplyMessage>()).ErrCode.Should().Be(RhpErrorCode.Ok);

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
        (await a.ExpectAsync<SocketReplyMessage>()).ErrCode.Should().Be(RhpErrorCode.NoMemory);
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
        read.Should().Be(0);   // server closed the connection after the in-frame timeout
    }

    [Fact]
    public async Task An_idle_connection_between_frames_is_not_dropped()
    {
        // One request, then let the clock run well past the in-frame timeout, and the connection
        // must survive, because idle-between-frames is unbounded by design (the deadline is
        // armed only once a frame has started). The in-frame deadline runs on the injected
        // clock, so this advances time rather than waiting for it (packet.net#698 RM-8).
        var time = new FakeTimeProvider();
        var server = await StartServerAsync(inFrameTimeout: TimeSpan.FromMilliseconds(250), timeProvider: time);
        var client = await ConnectAsync(server);

        await PingAsync(client, 1);
        time.Advance(TimeSpan.FromMilliseconds(600));
        await PingAsync(client, 2);
    }

    // ── A client that vanishes with an open in flight ────────────────────

    [Fact]
    public async Task A_client_that_disconnects_mid_open_does_not_leak_the_ax25_connection()
    {
        // The connect can take seconds of air time, and the client can hang up inside that
        // window. The read loop's teardown snapshots the handles it can SEE, so a handle
        // registered after that sweep owned a live AX.25 session with no owner until the
        // process exited (packet.net#698 RM-2). The background open now re-checks the client
        // and tears its own session down instead of registering it.
        var gateway = new RhpServerTests.FakeGateway { OpenGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously) };
        var log = new RecordingLogger();
        var server = new RhpServer(new RhpServerOptions { Bind = IPAddress.Loopback, Port = 0 }, gateway, log);
        cleanup.Add(server);
        await server.StartAsync();

        var client = await RhpServerTests.RhpTestClient.ConnectAsync(server.BoundEndpoint!);
        await client.SendAsync(new OpenMessage
        {
            Id = 1,
            Pfam = ProtocolFamily.Ax25,
            Mode = SocketMode.Stream,
            Remote = "GB7RDG",
            Flags = (int)OpenFlags.Active,
        });
        await gateway.OpenEntered.Task.WaitAsync(Timeout);   // the connect is on the air

        // Drop the socket mid-open and wait until the server's read loop has finished tearing
        // the client down (its last act is the "disconnected" log, after the handle sweep):
        // that is exactly the window the late-landing handle used to slip through.
        await client.DisposeAsync();
        await log.WaitForAsync("disconnected", Timeout);

        // Now let the connect resolve. The session must be disposed, not adopted...
        gateway.OpenGate!.SetResult();
        await gateway.Connection.DisposedTask.WaitAsync(Timeout);

        // ...and no handle may be left behind for the dead client: the orphan path never
        // registers one (pre-fix the server announced "handle 100 opened" and nothing ever
        // tore it down, so the session lived on for the life of the process).
        await log.WaitForAsync("the session was discarded", Timeout);
        log.Lines.Should().NotContain(line => line.Contains("opened to GB7RDG", StringComparison.Ordinal));

        // The server is still serving other clients.
        var fresh = await ConnectAsync(server);
        await SocketAsync(fresh, 1);
    }

    // ── Oversize unknown `type` ──────────────────────────────────────────

    [Fact]
    public async Task An_oversize_unknown_type_is_answered_with_errCode_2_and_keeps_the_session()
    {
        // `{type}Reply` echoed a client-controlled string of up to the 64 KB frame cap, so the
        // reply could not fit the 16-bit length prefix: RhpFraming.ThrowIfOversize threw
        // ArgumentException out of the write path, past its IOException/ObjectDisposedException
        // filter, and the connection was dropped with no reply at all (packet.net#698 RM-14).
        var server = await StartServerAsync();
        var client = await ConnectAsync(server);

        // Sized so the ECHO would not fit: the request frame is legal (65519 bytes), but
        // "{type}Reply" plus errCode/errText comes to 65568 - past the 16-bit length prefix.
        var huge = new string('x', 65500);
        await client.SendRawAsync($$"""{"type":"{{huge}}","id":42}""");

        var (type, json) = await client.ReadRawAsync();
        type.Should().StartWith("xxxx");
        type.Should().EndWith("Reply");
        (type.Length < 1024).Should().BeTrue($"the echoed type must be bounded, was {type.Length}");
        json.Should().Contain("\"errCode\":2");
        json.Should().Contain("\"id\":42");

        await PingAsync(client, 2);   // and the session is still usable
    }

    [Fact]
    public async Task An_oversize_unknown_type_pre_auth_is_answered_with_errCode_14_and_keeps_the_session()
    {
        // The same raw-reply path is reachable before auth (the pre-auth refusal builds
        // `{type}Reply` too), so it needs the same bound.
        var gateway = new RhpServerTests.FakeGateway();
        var server = new RhpServer(new RhpServerOptions
        {
            Bind = IPAddress.Loopback,
            Port = 0,
            RequireAuth = true,
            Authenticate = (_, pass) => pass == "right",
        }, gateway);
        cleanup.Add(server);
        await server.StartAsync();
        var client = await ConnectAsync(server);

        var huge = new string('y', 65500);
        await client.SendRawAsync($$"""{"type":"{{huge}}","id":7}""");

        var (type, json) = await client.ReadRawAsync();
        type.Should().EndWith("Reply");
        (type.Length < 1024).Should().BeTrue($"the echoed type must be bounded, was {type.Length}");
        json.Should().Contain("\"errCode\":14");

        // Still on the air: the auth that follows is answered normally.
        await client.SendAsync(new AuthMessage { Id = 8, User = "sysop", Pass = "right" });
        (await client.ExpectAsync<AuthReplyMessage>()).ErrCode.Should().Be(RhpErrorCode.Ok);
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
        // by the fourth the IP is locked, so the verify is skipped - but every reply is
        // still Unauthorised on the wire.
        for (int i = 1; i <= 5; i++)
        {
            await client.SendAsync(new AuthMessage { Id = i, User = "sysop", Pass = "wrong" });
            var reply = await client.ExpectAsync<AuthReplyMessage>();
            reply.ErrCode.Should().Be(RhpErrorCode.Unauthorised);
        }

        verifyCalls.Should().Be(3);   // locked out after 3, attempts 4 and 5 short-circuited
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
            (await client.ExpectAsync<AuthReplyMessage>()).ErrCode.Should().Be(RhpErrorCode.Unauthorised);
        }
        await client.SendAsync(new AuthMessage { Id = 3, User = "sysop", Pass = "right" });
        (await client.ExpectAsync<AuthReplyMessage>()).ErrCode.Should().Be(RhpErrorCode.Ok);

        // The reset means the failure budget is full again - three more failures are
        // needed to lock, proving the earlier two were cleared.
        throttle.IsLocked(IPAddress.Loopback.ToString()).Should().BeFalse();
    }

    /// <summary>
    /// Captures the server's rendered log lines and lets a test await one of them. The server's
    /// own lifecycle events (a client's read loop ending, a background open resolving) are
    /// otherwise unobservable from the wire, and waiting on them beats sleeping.
    /// </summary>
    private sealed class RecordingLogger : ILogger
    {
        private readonly List<string> lines = [];
        private readonly Channel<string> stream = Channel.CreateUnbounded<string>();

        /// <summary>Every line rendered so far.</summary>
        public IReadOnlyList<string> Lines
        {
            get { lock (lines) { return [.. lines]; } }
        }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            ArgumentNullException.ThrowIfNull(formatter);
            var line = formatter(state, exception);
            lock (lines)
            {
                lines.Add(line);
            }
            stream.Writer.TryWrite(line);
        }

        /// <summary>Completes once a line containing <paramref name="text"/> has been logged;
        /// throws if none arrives within <paramref name="timeout"/>. Lines are consumed in
        /// order, so successive waits must follow the order the server logs them in.</summary>
        public async Task WaitForAsync(string text, TimeSpan timeout)
        {
            using var cts = new CancellationTokenSource(timeout);
            await foreach (var line in stream.Reader.ReadAllAsync(cts.Token))
            {
                if (line.Contains(text, StringComparison.Ordinal))
                {
                    return;
                }
            }
        }
    }
}
