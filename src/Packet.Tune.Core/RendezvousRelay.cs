using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;

namespace Packet.Tune.Core;

/// <summary>
/// A minimal PIN-rendezvous WebSocket relay for remote tuning sessions,
/// designed to run on localhost today and a public host later, with zero
/// shared state beyond the session. Clients
/// <c>GET /ws?pin=NNNNNN&amp;role=tuned|meter</c>: the first client for a
/// PIN parks, the second pairs, and from then on the relay forwards frames
/// verbatim both ways. The session dies with either socket; PINs are
/// single-use; there is no persistence and no auth beyond the PIN (put TLS
/// in front and HMAC the PIN before exposing one publicly).
/// </summary>
/// <remarks>
/// Implemented directly over <see cref="TcpListener"/> +
/// <see cref="WebSocket.CreateFromStream(Stream, bool, string?, TimeSpan)"/>
/// (the RFC 6455 upgrade handshake is ~30 lines), so the class embeds in any
/// process — no ASP.NET Core host, no HttpListener platform caveats. Usable
/// embedded (tests park it on port 0) and from the CLI
/// (<c>packet-tune rendezvous --listen 8735</c>).
/// </remarks>
public sealed class RendezvousRelay : IAsyncDisposable
{
    private readonly TcpListener listener;
    private readonly CancellationTokenSource stopCts = new();
    private readonly Task acceptLoop;
    private readonly ConcurrentDictionary<string, ParkedClient> parked = new();
    private readonly ConcurrentDictionary<string, bool> usedPins = new();
    private int disposed;

    private RendezvousRelay(TcpListener listener)
    {
        this.listener = listener;
        acceptLoop = Task.Run(() => AcceptLoopAsync(stopCts.Token));
    }

    /// <summary>The port the relay is listening on (useful with port 0).</summary>
    public int Port => ((IPEndPoint)listener.LocalEndpoint).Port;

    /// <summary>Diagnostic sink (join/pair/close lines). Null = silent.</summary>
    public Action<string>? Log { get; set; }

    /// <summary>Start listening on all interfaces at <paramref name="port"/>
    /// (0 = ephemeral; read <see cref="Port"/>).</summary>
    public static RendezvousRelay Start(int port)
    {
        var listener = new TcpListener(IPAddress.Any, port);
        listener.Start();
        return new RendezvousRelay(listener);
    }

    /// <summary>Generate a random 6-digit session PIN.</summary>
    public static string GeneratePin() =>
        RandomNumberGenerator.GetInt32(0, 1_000_000).ToString("000000", CultureInfo.InvariantCulture);

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0)
        {
            return;
        }
        await stopCts.CancelAsync().ConfigureAwait(false);
        listener.Stop();
        foreach (var client in parked.Values)
        {
            client.Abort();
        }
        try
        {
            await acceptLoop.ConfigureAwait(false);
        }
        catch
        {
        }
        stopCts.Dispose();
    }

    private async Task AcceptLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await listener.AcceptTcpClientAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (SocketException)
            {
                continue;
            }
            _ = Task.Run(() => HandleClientAsync(client, cancellationToken), CancellationToken.None);
        }
    }

    private async Task HandleClientAsync(TcpClient client, CancellationToken cancellationToken)
    {
        client.NoDelay = true;
        var stream = client.GetStream();
        try
        {
            using var handshakeTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            handshakeTimeout.CancelAfter(TimeSpan.FromSeconds(10));

            var request = await ReadRequestHeadAsync(stream, handshakeTimeout.Token).ConfigureAwait(false);
            if (request is null)
            {
                await RejectAsync(stream, "400 Bad Request", "malformed HTTP request").ConfigureAwait(false);
                return;
            }
            var (target, headers) = request.Value;

            if (!TryParseTarget(target, out string? pin, out string? role) || pin is null)
            {
                await RejectAsync(stream, "400 Bad Request", "expected GET /ws?pin=NNNNNN&role=tuned|meter")
                    .ConfigureAwait(false);
                return;
            }
            if (!headers.TryGetValue("sec-websocket-key", out string? key) ||
                !headers.TryGetValue("upgrade", out string? upgrade) ||
                !upgrade.Contains("websocket", StringComparison.OrdinalIgnoreCase))
            {
                await RejectAsync(stream, "400 Bad Request", "not a WebSocket upgrade").ConfigureAwait(false);
                return;
            }
            if (usedPins.ContainsKey(pin))
            {
                await RejectAsync(stream, "409 Conflict", "PIN already used").ConfigureAwait(false);
                return;
            }

            await AcceptUpgradeAsync(stream, key, cancellationToken).ConfigureAwait(false);
            var socket = WebSocket.CreateFromStream(stream, isServer: true, subProtocol: null, TimeSpan.FromSeconds(30));
            var joined = new ParkedClient(client, socket, role ?? "?");

            while (true)
            {
                if (parked.TryRemove(pin, out var waiting))
                {
                    if (!waiting.TryClaim())
                    {
                        continue; // the parked client died in the meantime — park afresh
                    }
                    usedPins.TryAdd(pin, true);
                    Log?.Invoke($"relay: session {pin} paired ({waiting.Role} + {joined.Role})");
                    await RunSessionAsync(pin, waiting, joined, cancellationToken).ConfigureAwait(false);
                    return;
                }

                if (!parked.TryAdd(pin, joined))
                {
                    continue; // lost a park race — loop and pair with the winner
                }
                Log?.Invoke($"relay: session {pin}: {joined.Role} parked, waiting for partner");
                await joined.ParkAsync(cancellationToken).ConfigureAwait(false);
                if (joined.WasClaimed)
                {
                    return; // a partner picked it up; the pairing thread runs the session
                }
                parked.TryRemove(new KeyValuePair<string, ParkedClient>(pin, joined));
                Log?.Invoke($"relay: session {pin}: parked {joined.Role} left before a partner arrived");
                joined.Abort();
                return;
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Log?.Invoke($"relay: client error: {ex.Message}");
            try
            {
                client.Dispose();
            }
            catch
            {
            }
        }
    }

    private async Task RunSessionAsync(string pin, ParkedClient a, ParkedClient b, CancellationToken cancellationToken)
    {
        using var sessionCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        Task pumpAb = PumpAsync(a, b, sessionCts.Token);
        Task pumpBa = PumpAsync(b, a, sessionCts.Token);
        await Task.WhenAny(pumpAb, pumpBa).ConfigureAwait(false);
        await sessionCts.CancelAsync().ConfigureAwait(false);
        a.Abort();
        b.Abort();
        try
        {
            await Task.WhenAll(pumpAb, pumpBa).ConfigureAwait(false);
        }
        catch
        {
        }
        Log?.Invoke($"relay: session {pin} closed");
    }

    private static async Task PumpAsync(ParkedClient from, ParkedClient to, CancellationToken cancellationToken)
    {
        byte[] buffer = new byte[16 * 1024];
        try
        {
            while (true)
            {
                // A parked client may already have a receive in flight from its
                // parking watchdog — consume that first, then read normally.
                WebSocketReceiveResult result;
                byte[] sourceBuffer;
                if (from.TakePendingReceive() is { } pending)
                {
                    result = await pending.Receive.ConfigureAwait(false);
                    sourceBuffer = pending.Buffer;
                }
                else
                {
                    result = await from.Socket.ReceiveAsync(new ArraySegment<byte>(buffer), cancellationToken)
                        .ConfigureAwait(false);
                    sourceBuffer = buffer;
                }

                if (result.MessageType == WebSocketMessageType.Close)
                {
                    return;
                }
                await to.Socket.SendAsync(
                        new ArraySegment<byte>(sourceBuffer, 0, result.Count),
                        result.MessageType, result.EndOfMessage, cancellationToken)
                    .ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is WebSocketException or IOException or OperationCanceledException or ObjectDisposedException)
        {
            // Either socket died — the session dies with it.
        }
    }

    private static async Task<(string Target, Dictionary<string, string> Headers)?> ReadRequestHeadAsync(
        NetworkStream stream, CancellationToken cancellationToken)
    {
        var head = new MemoryStream();
        byte[] one = new byte[1];
        while (head.Length < 8192)
        {
            int n = await stream.ReadAsync(one.AsMemory(0, 1), cancellationToken).ConfigureAwait(false);
            if (n == 0)
            {
                return null;
            }
            head.WriteByte(one[0]);
            if (EndsWithDoubleCrlf(head))
            {
                break;
            }
        }

        string text = Encoding.ASCII.GetString(head.GetBuffer(), 0, (int)head.Length);
        string[] lines = text.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        if (lines.Length == 0)
        {
            return null;
        }
        string[] requestLine = lines[0].Split(' ');
        if (requestLine.Length < 3 || requestLine[0] != "GET")
        {
            return null;
        }
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (string line in lines.Skip(1))
        {
            int colon = line.IndexOf(':', StringComparison.Ordinal);
            if (colon > 0)
            {
                headers[line[..colon].Trim().ToLowerInvariant()] = line[(colon + 1)..].Trim();
            }
        }
        return (requestLine[1], headers);
    }

    private static bool EndsWithDoubleCrlf(MemoryStream head)
    {
        if (head.Length < 4)
        {
            return false;
        }
        var buf = head.GetBuffer();
        long i = head.Length;
        return buf[i - 4] == '\r' && buf[i - 3] == '\n' && buf[i - 2] == '\r' && buf[i - 1] == '\n';
    }

    private static bool TryParseTarget(string target, out string? pin, out string? role)
    {
        pin = null;
        role = null;
        int q = target.IndexOf('?', StringComparison.Ordinal);
        string path = q < 0 ? target : target[..q];
        if (path != "/ws")
        {
            return false;
        }
        if (q >= 0)
        {
            foreach (string pair in target[(q + 1)..].Split('&', StringSplitOptions.RemoveEmptyEntries))
            {
                int eq = pair.IndexOf('=', StringComparison.Ordinal);
                if (eq <= 0)
                {
                    continue;
                }
                string name = pair[..eq];
                string value = Uri.UnescapeDataString(pair[(eq + 1)..]);
                if (name == "pin")
                {
                    pin = value;
                }
                else if (name == "role")
                {
                    role = value;
                }
            }
        }
        return pin is { Length: 6 } && pin.All(char.IsAsciiDigit);
    }

    private static async Task AcceptUpgradeAsync(NetworkStream stream, string key, CancellationToken cancellationToken)
    {
        // RFC 6455 §4.2.2 — the accept token is SHA-1 of key + magic GUID.
        // SHA-1 here is protocol framing, not a security control.
#pragma warning disable CA5350
        byte[] hash = SHA1.HashData(Encoding.ASCII.GetBytes(key + "258EAFA5-E914-47DA-95CA-C5AB0DC85B11"));
#pragma warning restore CA5350
        string accept = Convert.ToBase64String(hash);
        string response =
            "HTTP/1.1 101 Switching Protocols\r\n" +
            "Upgrade: websocket\r\n" +
            "Connection: Upgrade\r\n" +
            $"Sec-WebSocket-Accept: {accept}\r\n" +
            "\r\n";
        byte[] bytes = Encoding.ASCII.GetBytes(response);
        await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
    }

    private static async Task RejectAsync(NetworkStream stream, string status, string reason)
    {
        try
        {
            byte[] body = Encoding.UTF8.GetBytes(reason);
            string response =
                $"HTTP/1.1 {status}\r\n" +
                "Content-Type: text/plain\r\n" +
                $"Content-Length: {body.Length.ToString(CultureInfo.InvariantCulture)}\r\n" +
                "Connection: close\r\n" +
                "\r\n";
            await stream.WriteAsync(Encoding.ASCII.GetBytes(response)).ConfigureAwait(false);
            await stream.WriteAsync(body).ConfigureAwait(false);
        }
        catch
        {
        }
        finally
        {
            stream.Dispose();
        }
    }

    /// <summary>One relay-side client: its socket plus the park/claim state
    /// that lets a waiting client's death be noticed without racing the
    /// partner's claim.</summary>
    private sealed class ParkedClient(TcpClient tcp, WebSocket socket, string role)
    {
        private const int StateParked = 0;
        private const int StateClaimed = 1;
        private const int StateDead = 2;

        private readonly TaskCompletionSource parkDone = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int state = StateParked;
        private PendingReceive? pendingReceive;

        public WebSocket Socket { get; } = socket;

        public string Role { get; } = role;

        public bool WasClaimed => Volatile.Read(ref state) == StateClaimed;

        /// <summary>Partner-side: atomically claim a parked client. False =
        /// it already died.</summary>
        public bool TryClaim()
        {
            bool claimed = Interlocked.CompareExchange(ref state, StateClaimed, StateParked) == StateParked;
            if (claimed)
            {
                parkDone.TrySetResult();
            }
            return claimed;
        }

        /// <summary>Own-thread side: wait while parked, watching the socket so
        /// a client that gives up (or dies) frees its PIN. Returns when the
        /// client is claimed by a partner or the socket closes.</summary>
        public async Task ParkAsync(CancellationToken cancellationToken)
        {
            // The pending receive is published BEFORE parking completes, so a
            // partner's pump always finds it and never starts a concurrent
            // second receive on this socket.
            byte[] buffer = new byte[16 * 1024];
            var receive = ReceiveIntoAsync(buffer, cancellationToken);
            pendingReceive = new PendingReceive(receive, buffer);

            var done = await Task.WhenAny(parkDone.Task, receive).ConfigureAwait(false);
            if (done == parkDone.Task)
            {
                return; // claimed — the session pump consumes the pending receive
            }

            // The socket spoke while parked. Early data (an eager HI) is kept
            // for the pump and parking continues; a close/fault frees the PIN.
            bool socketDead;
            try
            {
                var result = await receive.ConfigureAwait(false);
                socketDead = result.MessageType == WebSocketMessageType.Close;
            }
            catch
            {
                socketDead = true;
            }

            if (!socketDead)
            {
                // One receive in flight at a time: with a data frame held for
                // the pump, death detection pauses until a partner arrives
                // (further frames queue in the socket buffer meanwhile).
                await parkDone.Task.ConfigureAwait(false);
                return;
            }
            Interlocked.CompareExchange(ref state, StateDead, StateParked);
        }

        public PendingReceive? TakePendingReceive() =>
            Interlocked.Exchange(ref pendingReceive, null);

        public void Abort()
        {
            parkDone.TrySetResult(); // unblock a ParkAsync waiting on a claim
            try
            {
                Socket.Abort();
            }
            catch
            {
            }
            try
            {
                tcp.Dispose();
            }
            catch
            {
            }
        }

        private async Task<WebSocketReceiveResult> ReceiveIntoAsync(byte[] buffer, CancellationToken cancellationToken)
        {
            var result = await Socket.ReceiveAsync(new ArraySegment<byte>(buffer), cancellationToken)
                .ConfigureAwait(false);
            return result;
        }
    }

    /// <summary>A receive started while a client was parked, handed to the
    /// session pump together with the buffer it fills.</summary>
    private sealed record PendingReceive(Task<WebSocketReceiveResult> Receive, byte[] Buffer);
}
