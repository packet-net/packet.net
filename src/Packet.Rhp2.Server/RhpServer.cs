using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Packet.Node.Core.Console;

namespace Packet.Rhp2.Server;

/// <summary>Options for one <see cref="RhpServer"/> instance.</summary>
public sealed class RhpServerOptions
{
    /// <summary>Bind address (loopback is the trust boundary — RHP has no TLS).</summary>
    public required IPAddress Bind { get; init; }

    /// <summary>TCP port (9000 conventional; 0 = ephemeral, for tests).</summary>
    public int Port { get; init; } = 9000;

    /// <summary>When true, a client must <c>auth</c> successfully before any other
    /// request is honoured (each pre-auth request is refused with errCode 14).</summary>
    public bool RequireAuth { get; init; }

    /// <summary>Credential validator for the wire's <c>auth</c> message. Null with
    /// <see cref="RequireAuth"/> on means every auth fails (no users ⇒ no access).</summary>
    public Func<string, string, bool>? Authenticate { get; init; }
}

/// <summary>
/// The RHPv2 server (PWP-0222/0245, XRouter-compatible — see <c>docs/rhp2-server.md</c> for
/// scope and the named-deviations table). A JSON-over-TCP front-end on the node's packet
/// engine: requests are dispatched per client connection, an outbound <c>open</c> (Active)
/// becomes an <see cref="INodeConnection"/> via <see cref="IRhpGateway"/>, and each open
/// handle gets a pump forwarding session bytes back as async <c>recv</c> notifications.
/// </summary>
/// <remarks>
/// <para><b>Wire contracts matched to live XRouter:</b> replies echo the request <c>id</c>;
/// async notifications carry a <c>seqno</c> and never an <c>id</c>; <c>errCode</c>/<c>errText</c>
/// are capitalised; an unknown <c>type</c> is answered with <c>{type}Reply</c> + errCode 2;
/// handles are numbered from one server-wide counter (from 100, like the reference).</para>
/// <para><b>Named deviations (docs/rhp2-server.md):</b> a handle is usable only by the client
/// connection that created it (D3); a bad <c>auth</c> fails that attempt without wedging the
/// connection (D2); oversize sends are honoured, not silently dropped (D1); <c>openReply</c>
/// is sent when the connect resolves, carrying a real success/failure (D4).</para>
/// </remarks>
public sealed partial class RhpServer : IAsyncDisposable
{
    private const int RecvChunk = 2048;          // session bytes per recv push (escaped JSON stays well under the frame cap)
    private const int FirstHandle = 100;         // match the reference's visible numbering

    private readonly RhpServerOptions options;
    private readonly IRhpGateway gateway;
    private readonly ILogger logger;
    private readonly CancellationTokenSource lifecycle = new();
    private readonly ConcurrentDictionary<int, RhpHandle> handles = new();
    private readonly ConcurrentDictionary<ClientState, byte> clients = new();
    private int nextHandle = FirstHandle - 1;
    private int nextSeqno;
    private Socket? listenSocket;
    private Task? acceptLoop;
    private int started;
    private int disposed;

    public RhpServer(RhpServerOptions options, IRhpGateway gateway, ILogger? logger = null)
    {
        this.options = options ?? throw new ArgumentNullException(nameof(options));
        this.gateway = gateway ?? throw new ArgumentNullException(nameof(gateway));
        this.logger = logger ?? NullLogger.Instance;
    }

    /// <summary>The bound endpoint once started (reports the real port when 0 was asked).</summary>
    public IPEndPoint? BoundEndpoint { get; private set; }

    /// <summary>Bind and start accepting. Idempotent.</summary>
    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        if (Interlocked.Exchange(ref started, 1) != 0)
        {
            return Task.CompletedTask;
        }

        var sock = new Socket(options.Bind.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
        sock.Bind(new IPEndPoint(options.Bind, options.Port));
        sock.Listen(backlog: 16);
        listenSocket = sock;
        BoundEndpoint = (IPEndPoint)sock.LocalEndPoint!;
        LogListening(BoundEndpoint);

        acceptLoop = Task.Run(() => AcceptLoopAsync(lifecycle.Token), CancellationToken.None);
        cancellationToken.ThrowIfCancellationRequested();
        return Task.CompletedTask;
    }

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        var sock = listenSocket!;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                Socket accepted;
                try
                {
                    accepted = await sock.AcceptAsync(ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (SocketException) when (ct.IsCancellationRequested)
                {
                    break;
                }
                _ = Task.Run(() => HandleClientAsync(accepted, ct), CancellationToken.None);
            }
        }
        catch (ObjectDisposedException)
        {
            // listener disposed during shutdown — normal.
        }
    }

    // One RHP client TCP connection: read frames sequentially, dispatch each. Slow
    // operations (the AX.25 connect behind `open`) run on background tasks so one
    // in-flight open never blocks another handle's traffic — replies correlate by id,
    // so out-of-order completion is fine (and matches the async reference).
    private async Task HandleClientAsync(Socket socket, CancellationToken ct)
    {
        var client = new ClientState(socket, authed: !options.RequireAuth);
        clients.TryAdd(client, 0);
        LogClientConnected(client.Peer);
        try
        {
            while (!ct.IsCancellationRequested)
            {
                byte[]? frame;
                try
                {
                    frame = await RhpFraming.ReadFrameAsync(client.Stream, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex) when (ex is IOException or ObjectDisposedException or EndOfStreamException)
                {
                    break;   // client gone / stream desynced — drop the connection
                }

                if (frame is null)
                {
                    break;   // clean EOF
                }
                if (frame.Length == 0)
                {
                    continue;   // zero-length frame is legal noise
                }

                RhpMessage msg;
                try
                {
                    msg = RhpJson.Deserialize(frame);
                }
                catch (Exception ex) when (ex is RhpProtocolException or JsonException)
                {
                    // Not a JSON object / no type: we can't even shape a reply. The stream
                    // is suspect (framing desync) — close the connection rather than guess.
                    LogBadFrame(client.Peer, ex.Message);
                    break;
                }

                await DispatchAsync(client, msg, ct).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogClientFaulted(ex, client.Peer);
        }
        finally
        {
            clients.TryRemove(client, out _);
            await CloseClientHandlesAsync(client).ConfigureAwait(false);
            client.Dispose();
            LogClientClosed(client.Peer);
        }
    }

    private async Task DispatchAsync(ClientState client, RhpMessage msg, CancellationToken ct)
    {
        // The auth gate: when required and not yet satisfied, every non-auth request is
        // refused with 14 — per request. (Deviation D2: a bad auth does NOT wedge the
        // connection; a later good auth recovers. See docs/rhp2-server.md.)
        if (options.RequireAuth && !client.Authed && msg is not AuthMessage)
        {
            await ReplyErrorAsync(client, msg, RhpErrorCode.Unauthorised, ct).ConfigureAwait(false);
            return;
        }

        switch (msg)
        {
            case AuthMessage auth:
                bool ok = options.Authenticate?.Invoke(auth.User, auth.Pass) ?? !options.RequireAuth;
                if (ok)
                {
                    client.Authed = true;
                }
                LogAuth(client.Peer, auth.User, ok);
                await WriteAsync(client, new AuthReplyMessage
                {
                    Id = auth.Id,
                    ErrCode = ok ? RhpErrorCode.Ok : RhpErrorCode.Unauthorised,
                    ErrText = RhpErrorCode.Text(ok ? RhpErrorCode.Ok : RhpErrorCode.Unauthorised),
                }, ct).ConfigureAwait(false);
                break;

            case OpenMessage open:
                await HandleOpenAsync(client, open, ct).ConfigureAwait(false);
                break;

            case SendMessage send:
                await HandleSendAsync(client, send, ct).ConfigureAwait(false);
                break;

            case CloseMessage close:
                await HandleCloseAsync(client, close, ct).ConfigureAwait(false);
                break;

            // The BSD-style passive lifecycle: socket → bind(callsign) → listen → async accept
            // pushes per inbound connection (the DAPPS listener path).
            case SocketMessage m:
                await HandleSocketAsync(client, m, ct).ConfigureAwait(false);
                break;
            case BindMessage m:
                await HandleBindAsync(client, m, ct).ConfigureAwait(false);
                break;
            case ListenMessage m:
                await HandleListenAsync(client, m, ct).ConfigureAwait(false);
                break;
            case ConnectMessage m:
                await WriteAsync(client, new ConnectReplyMessage { Id = m.Id, Handle = m.Handle, ErrCode = RhpErrorCode.OperationNotSupported, ErrText = "Operation not supported (use open with the Active flag)" }, ct).ConfigureAwait(false);
                break;
            case SendToMessage m:
                await WriteAsync(client, new SendToReplyMessage { Id = m.Id, Handle = m.Handle, ErrCode = RhpErrorCode.OperationNotSupported, ErrText = RhpErrorCode.Text(RhpErrorCode.OperationNotSupported) }, ct).ConfigureAwait(false);
                break;
            case StatusMessage m:
                await WriteAsync(client, new StatusReplyMessage { Id = m.Id, Handle = m.Handle, ErrCode = RhpErrorCode.OperationNotSupported, ErrText = RhpErrorCode.Text(RhpErrorCode.OperationNotSupported) }, ct).ConfigureAwait(false);
                break;

            case UnknownMessage unknown:
                // Match live XRouter: an unrecognised type is answered with `{type}Reply`,
                // errCode 2. Built as raw JSON — there is no DTO for a manufactured type.
                var reply = new JsonObject
                {
                    ["type"] = unknown.Type + "Reply",
                    ["errCode"] = RhpErrorCode.BadOrMissingType,
                    ["errText"] = RhpErrorCode.Text(RhpErrorCode.BadOrMissingType),
                };
                if (unknown.Id is { } id)
                {
                    reply["id"] = id;
                }
                await WriteRawAsync(client, reply, ct).ConfigureAwait(false);
                break;

            default:
                // A reply/notification shape sent BY a client (authReply, recv, accept, ...)
                // is nonsensical — log and ignore rather than invent semantics.
                LogIgnoredMessage(client.Peer, msg.Type);
                break;
        }
    }

    private async Task HandleOpenAsync(ClientState client, OpenMessage open, CancellationToken ct)
    {
        // Validation ladder, most-specific error first (codes per PWP-0222).
        int err = 0;
        string? text = null;
        bool knownFamily = open.Pfam is ProtocolFamily.Ax25 or ProtocolFamily.NetRom or ProtocolFamily.Inet or ProtocolFamily.Unix;
        bool knownMode = open.Mode is SocketMode.Stream or SocketMode.Dgram or SocketMode.Seqpkt
            or SocketMode.Custom or SocketMode.SemiRaw or SocketMode.Trace or SocketMode.Raw;
        if (!knownFamily)
        {
            (err, text) = (RhpErrorCode.BadOrMissingFamily, RhpErrorCode.Text(RhpErrorCode.BadOrMissingFamily));
        }
        else if (open.Pfam != ProtocolFamily.Ax25)
        {
            (err, text) = (RhpErrorCode.OperationNotSupported, $"family '{open.Pfam}' is not implemented yet (ax25 only)");
        }
        else if (!knownMode)
        {
            (err, text) = (RhpErrorCode.BadOrMissingMode, RhpErrorCode.Text(RhpErrorCode.BadOrMissingMode));
        }
        else if (open.Mode != SocketMode.Stream)
        {
            (err, text) = (RhpErrorCode.OperationNotSupported, $"mode '{open.Mode}' is not implemented yet (stream only)");
        }
        else if ((open.Flags & (int)OpenFlags.Active) == 0)
        {
            (err, text) = (RhpErrorCode.OperationNotSupported, "passive open is not implemented yet (R-3); only Active (0x80) opens");
        }
        else if (string.IsNullOrWhiteSpace(open.Remote))
        {
            (err, text) = (RhpErrorCode.InvalidRemoteAddress, RhpErrorCode.Text(RhpErrorCode.InvalidRemoteAddress));
        }

        if (err != 0)
        {
            await WriteAsync(client, new OpenReplyMessage { Id = open.Id, Handle = 0, ErrCode = err, ErrText = text }, ct).ConfigureAwait(false);
            return;
        }

        // The connect can take seconds of air time — run it off the dispatch loop so this
        // client's other handles stay live; the reply correlates by id whenever it lands.
        // (Deviation D4: the reply carries the RESOLVED outcome, not an early optimistic one.)
        _ = Task.Run(async () =>
        {
            INodeConnection conn;
            try
            {
                conn = await gateway.OpenAx25StreamAsync(open.Port, open.Local, open.Remote!, ct).ConfigureAwait(false);
            }
            catch (RhpGatewayException gex)
            {
                await WriteAsync(client, new OpenReplyMessage { Id = open.Id, Handle = 0, ErrCode = gex.ErrCode, ErrText = gex.Message }, ct).ConfigureAwait(false);
                return;
            }
            catch (OperationCanceledException)
            {
                return;   // server/client shutting down
            }

            var handle = new RhpHandle(Interlocked.Increment(ref nextHandle), client, conn);
            handles[handle.Id] = handle;
            LogOpened(handle.Id, open.Remote!, client.Peer);

            await WriteAsync(client, new OpenReplyMessage
            {
                Id = open.Id,
                Handle = handle.Id,
                ErrCode = RhpErrorCode.Ok,
                ErrText = RhpErrorCode.Text(RhpErrorCode.Ok),
            }, ct).ConfigureAwait(false);

            // Shape-compatibility with the async reference: a status push announcing the link
            // is up follows the successful reply (clients that watch status see XRouter's
            // signal; clients that don't — DAPPS — ignore it).
            await WriteAsync(client, new StatusMessage
            {
                Handle = handle.Id,
                Flags = (int)(StatusFlags.Connected | StatusFlags.ConOk),
                Seqno = Interlocked.Increment(ref nextSeqno),
            }, ct).ConfigureAwait(false);

            handle.Pump = Task.Run(() => PumpHandleAsync(handle, ct), CancellationToken.None);
        }, CancellationToken.None);
    }

    private async Task HandleSocketAsync(ClientState client, SocketMessage msg, CancellationToken ct)
    {
        // Same family/mode ladder as open — ax25 + stream only in v1 (docs/rhp2-server.md §Scope).
        int err = 0;
        string? text = null;
        bool knownFamily = msg.Pfam is ProtocolFamily.Ax25 or ProtocolFamily.NetRom or ProtocolFamily.Inet or ProtocolFamily.Unix;
        bool knownMode = msg.Mode is SocketMode.Stream or SocketMode.Dgram or SocketMode.Seqpkt
            or SocketMode.Custom or SocketMode.SemiRaw or SocketMode.Trace or SocketMode.Raw;
        if (!knownFamily)
        {
            (err, text) = (RhpErrorCode.BadOrMissingFamily, RhpErrorCode.Text(RhpErrorCode.BadOrMissingFamily));
        }
        else if (msg.Pfam != ProtocolFamily.Ax25)
        {
            (err, text) = (RhpErrorCode.OperationNotSupported, $"family '{msg.Pfam}' is not implemented yet (ax25 only)");
        }
        else if (!knownMode)
        {
            (err, text) = (RhpErrorCode.BadOrMissingMode, RhpErrorCode.Text(RhpErrorCode.BadOrMissingMode));
        }
        else if (msg.Mode != SocketMode.Stream)
        {
            (err, text) = (RhpErrorCode.OperationNotSupported, $"mode '{msg.Mode}' is not implemented yet (stream only)");
        }

        if (err != 0)
        {
            await WriteAsync(client, new SocketReplyMessage { Id = msg.Id, Handle = null, ErrCode = err, ErrText = text }, ct).ConfigureAwait(false);
            return;
        }

        var handle = new RhpHandle(Interlocked.Increment(ref nextHandle), client, connection: null);
        handles[handle.Id] = handle;
        await WriteAsync(client, new SocketReplyMessage
        {
            Id = msg.Id,
            Handle = handle.Id,
            ErrCode = RhpErrorCode.Ok,
            ErrText = RhpErrorCode.Text(RhpErrorCode.Ok),
        }, ct).ConfigureAwait(false);
    }

    private async Task HandleBindAsync(ClientState client, BindMessage msg, CancellationToken ct)
    {
        if (!handles.TryGetValue(msg.Handle, out var handle) || handle.Owner != client || handle.Connection is not null)
        {
            await WriteAsync(client, new BindReplyMessage { Id = msg.Id, Handle = msg.Handle, ErrCode = RhpErrorCode.InvalidHandle, ErrText = RhpErrorCode.Text(RhpErrorCode.InvalidHandle) }, ct).ConfigureAwait(false);
            return;
        }
        if (string.IsNullOrWhiteSpace(msg.Local))
        {
            await WriteAsync(client, new BindReplyMessage { Id = msg.Id, Handle = msg.Handle, ErrCode = RhpErrorCode.InvalidLocalAddress, ErrText = RhpErrorCode.Text(RhpErrorCode.InvalidLocalAddress) }, ct).ConfigureAwait(false);
            return;
        }

        // Re-bind before listen is legal (XRouter allows a second bind); a null port means all
        // ports — exactly what DAPPS sends.
        handle.BoundLocal = msg.Local.Trim();
        handle.BoundPort = msg.Port;
        await WriteAsync(client, new BindReplyMessage { Id = msg.Id, Handle = msg.Handle, ErrCode = RhpErrorCode.Ok, ErrText = RhpErrorCode.Text(RhpErrorCode.Ok) }, ct).ConfigureAwait(false);
    }

    private async Task HandleListenAsync(ClientState client, ListenMessage msg, CancellationToken ct)
    {
        if (!handles.TryGetValue(msg.Handle, out var handle) || handle.Owner != client || handle.Connection is not null)
        {
            await WriteAsync(client, new ListenReplyMessage { Id = msg.Id, Handle = msg.Handle, ErrCode = RhpErrorCode.InvalidHandle, ErrText = RhpErrorCode.Text(RhpErrorCode.InvalidHandle) }, ct).ConfigureAwait(false);
            return;
        }
        if (handle.BoundLocal is null)
        {
            await WriteAsync(client, new ListenReplyMessage { Id = msg.Id, Handle = msg.Handle, ErrCode = RhpErrorCode.BadParameter, ErrText = "socket is not bound (bind a callsign first)" }, ct).ConfigureAwait(false);
            return;
        }
        if (handle.Listening)
        {
            // Re-listen on an already-listening socket is idempotent Ok — OBSERVED live XRouter
            // behaviour (the R-4 wire-diff oracle caught this: errCode 9 "Duplicate socket" is
            // only for a SECOND socket claiming the same callsign, which the gateway raises).
            await WriteAsync(client, new ListenReplyMessage { Id = msg.Id, Handle = msg.Handle, ErrCode = RhpErrorCode.Ok, ErrText = RhpErrorCode.Text(RhpErrorCode.Ok) }, ct).ConfigureAwait(false);
            return;
        }

        try
        {
            handle.Registration = gateway.RegisterListener(
                handle.BoundPort,
                handle.BoundLocal,
                (connection, portLabel) => OnInboundAcceptedAsync(handle, connection, portLabel));
        }
        catch (RhpGatewayException gex)
        {
            await WriteAsync(client, new ListenReplyMessage { Id = msg.Id, Handle = msg.Handle, ErrCode = gex.ErrCode, ErrText = gex.Message }, ct).ConfigureAwait(false);
            return;
        }

        LogListening2(handle.Id, handle.BoundLocal, client.Peer);
        await WriteAsync(client, new ListenReplyMessage { Id = msg.Id, Handle = msg.Handle, ErrCode = RhpErrorCode.Ok, ErrText = RhpErrorCode.Text(RhpErrorCode.Ok) }, ct).ConfigureAwait(false);
    }

    // An inbound station connected to a listening callsign: allocate the CHILD handle (owned by
    // the listener's client), announce it with an async accept push (seqno, no id —
    // accept.port is a STRING, the XRouter wire shape), then pump its session like any stream.
    private async Task OnInboundAcceptedAsync(RhpHandle listenerHandle, INodeConnection connection, string portLabel)
    {
        if (listenerHandle.Closed)
        {
            await connection.DisposeAsync().ConfigureAwait(false);   // listener torn down mid-accept
            return;
        }

        var child = new RhpHandle(Interlocked.Increment(ref nextHandle), listenerHandle.Owner, connection);
        handles[child.Id] = child;
        LogAccepted(child.Id, connection.PeerId, listenerHandle.Id);

        await WriteAsync(listenerHandle.Owner, new AcceptMessage
        {
            Handle = listenerHandle.Id,
            Child = child.Id,
            Remote = connection.PeerId,
            Local = listenerHandle.BoundLocal,
            Port = portLabel,
            Seqno = Interlocked.Increment(ref nextSeqno),
        }, CancellationToken.None).ConfigureAwait(false);

        child.Pump = Task.Run(() => PumpHandleAsync(child, lifecycle.Token), CancellationToken.None);
    }

    private async Task HandleSendAsync(ClientState client, SendMessage send, CancellationToken ct)
    {
        // Deviation D3: a handle is only usable by the connection that created it — anyone
        // else sees the same "invalid handle" an unknown id gets (no existence oracle).
        if (!handles.TryGetValue(send.Handle, out var handle) || handle.Owner != client)
        {
            await WriteAsync(client, new SendReplyMessage { Id = send.Id, Handle = send.Handle, ErrCode = RhpErrorCode.InvalidHandle, ErrText = RhpErrorCode.Text(RhpErrorCode.InvalidHandle) }, ct).ConfigureAwait(false);
            return;
        }
        if (handle.Connection is null)
        {
            // A socket/listener handle has no link to send on — the wire's 17.
            await WriteAsync(client, new SendReplyMessage { Id = send.Id, Handle = send.Handle, ErrCode = RhpErrorCode.NotConnected, ErrText = RhpErrorCode.Text(RhpErrorCode.NotConnected) }, ct).ConfigureAwait(false);
            return;
        }

        try
        {
            var bytes = RhpDataEncoding.FromWireString(send.Data);
            await handle.Connection.WriteAsync(bytes, ct).ConfigureAwait(false);
            await WriteAsync(client, new SendReplyMessage
            {
                Id = send.Id,
                Handle = send.Handle,
                ErrCode = RhpErrorCode.Ok,
                ErrText = RhpErrorCode.Text(RhpErrorCode.Ok),
                Status = (int)StatusFlags.Connected,
            }, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
        {
            // The link under the handle is gone — the wire's errCode 17 (XRouter-observed).
            await WriteAsync(client, new SendReplyMessage { Id = send.Id, Handle = send.Handle, ErrCode = RhpErrorCode.NotConnected, ErrText = RhpErrorCode.Text(RhpErrorCode.NotConnected) }, ct).ConfigureAwait(false);
        }
    }

    private async Task HandleCloseAsync(ClientState client, CloseMessage close, CancellationToken ct)
    {
        if (!handles.TryGetValue(close.Handle, out var handle) || handle.Owner != client)
        {
            await WriteAsync(client, new CloseReplyMessage { Id = close.Id, Handle = close.Handle, ErrCode = RhpErrorCode.InvalidHandle, ErrText = RhpErrorCode.Text(RhpErrorCode.InvalidHandle) }, ct).ConfigureAwait(false);
            return;
        }

        await TearDownHandleAsync(handle, notifyOwner: false).ConfigureAwait(false);
        await WriteAsync(client, new CloseReplyMessage { Id = close.Id, Handle = close.Handle, ErrCode = RhpErrorCode.Ok, ErrText = RhpErrorCode.Text(RhpErrorCode.Ok) }, ct).ConfigureAwait(false);
    }

    // Session → client: forward the link's bytes as async recv pushes until it ends, then
    // announce the end with a server-initiated close push (seqno, no id) — the wire's
    // "the peer hung up" signal.
    private async Task PumpHandleAsync(RhpHandle handle, CancellationToken ct)
    {
        var conn = handle.Connection!;   // pumps only run on stream handles
        try
        {
            while (!ct.IsCancellationRequested && !handle.Closed)
            {
                ReadOnlyMemory<byte> chunk;
                var readTask = conn.ReadAsync(ct).AsTask();
                var done = await Task.WhenAny(readTask, conn.Completion).ConfigureAwait(false);
                if (done != readTask)
                {
                    if (readTask.IsCompletedSuccessfully)
                    {
                        chunk = readTask.Result;
                    }
                    else
                    {
                        break;
                    }
                }
                else
                {
                    chunk = await readTask.ConfigureAwait(false);
                }

                if (chunk.IsEmpty)
                {
                    break;   // EOF — peer disconnected
                }

                for (int off = 0; off < chunk.Length; off += RecvChunk)
                {
                    var slice = chunk.Slice(off, Math.Min(RecvChunk, chunk.Length - off));
                    await WriteAsync(handle.Owner, new RecvMessage
                    {
                        Handle = handle.Id,
                        Data = RhpDataEncoding.ToWireString(slice.Span),
                        Seqno = Interlocked.Increment(ref nextSeqno),
                    }, ct).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // teardown — normal.
        }
        catch (Exception ex)
        {
            LogPumpFaulted(ex, handle.Id);
        }
        finally
        {
            if (!handle.Closed)
            {
                await TearDownHandleAsync(handle, notifyOwner: true).ConfigureAwait(false);
            }
        }
    }

    // Remove + dispose a handle. notifyOwner pushes the server-initiated close (the peer/link
    // ended it); a client-requested close gets its closeReply from the dispatch path instead.
    private async Task TearDownHandleAsync(RhpHandle handle, bool notifyOwner)
    {
        if (handle.MarkClosed())
        {
            return;   // already torn down
        }
        handles.TryRemove(handle.Id, out _);
        handle.Registration?.Dispose();   // a listener stops answering for its callsign
        if (handle.Connection is { } conn)
        {
            try
            {
                await conn.DisposeAsync().ConfigureAwait(false);
            }
            catch
            {
                // best-effort teardown
            }
        }
        LogHandleClosed(handle.Id);

        if (notifyOwner)
        {
            await WriteAsync(handle.Owner, new CloseMessage
            {
                Handle = handle.Id,
                Seqno = Interlocked.Increment(ref nextSeqno),
            }, CancellationToken.None).ConfigureAwait(false);
        }
    }

    // All sockets/handles die with their RHP TCP connection (PWP-0222).
    private async Task CloseClientHandlesAsync(ClientState client)
    {
        foreach (var handle in handles.Values.Where(h => h.Owner == client))
        {
            await TearDownHandleAsync(handle, notifyOwner: false).ConfigureAwait(false);
        }
    }

    // Refuse a (typed) request with an error reply of its matching reply type — used by the
    // pre-auth gate, where the request itself is otherwise valid.
    private static Task ReplyErrorAsync(ClientState client, RhpMessage msg, int errCode, CancellationToken ct)
    {
        var text = RhpErrorCode.Text(errCode);
        RhpMessage? reply = msg switch
        {
            OpenMessage m => new OpenReplyMessage { Id = m.Id, Handle = 0, ErrCode = errCode, ErrText = text },
            SocketMessage m => new SocketReplyMessage { Id = m.Id, Handle = null, ErrCode = errCode, ErrText = text },
            BindMessage m => new BindReplyMessage { Id = m.Id, Handle = m.Handle, ErrCode = errCode, ErrText = text },
            ListenMessage m => new ListenReplyMessage { Id = m.Id, Handle = m.Handle, ErrCode = errCode, ErrText = text },
            ConnectMessage m => new ConnectReplyMessage { Id = m.Id, Handle = m.Handle, ErrCode = errCode, ErrText = text },
            SendMessage m => new SendReplyMessage { Id = m.Id, Handle = m.Handle, ErrCode = errCode, ErrText = text },
            SendToMessage m => new SendToReplyMessage { Id = m.Id, Handle = m.Handle, ErrCode = errCode, ErrText = text },
            StatusMessage m => new StatusReplyMessage { Id = m.Id, Handle = m.Handle, ErrCode = errCode, ErrText = text },
            CloseMessage m => new CloseReplyMessage { Id = m.Id, Handle = m.Handle, ErrCode = errCode, ErrText = text },
            UnknownMessage m => null,   // handled by the generic {type}Reply path below
            _ => null,
        };
        if (reply is not null)
        {
            return WriteAsync(client, reply, ct);
        }
        var raw = new JsonObject
        {
            ["type"] = msg.Type + "Reply",
            ["errCode"] = errCode,
            ["errText"] = text,
        };
        if (msg.Id is { } id)
        {
            raw["id"] = id;
        }
        return WriteRawAsync(client, raw, ct);
    }

    // Writes are serialized per client (replies from the dispatch loop interleave with pushes
    // from handle pumps). A write failure means the client is gone — swallowed; the read loop
    // notices and tears the client down.
    private static async Task WriteAsync(ClientState client, RhpMessage msg, CancellationToken ct)
    {
        var payload = RhpJson.Serialize(msg);
        await client.WriteGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await RhpFraming.WriteFrameAsync(client.Stream, payload, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            // client gone — the read loop will clean up.
        }
        finally
        {
            client.WriteGate.Release();
        }
    }

    private static async Task WriteRawAsync(ClientState client, JsonObject obj, CancellationToken ct)
    {
        var payload = System.Text.Encoding.UTF8.GetBytes(obj.ToJsonString());
        await client.WriteGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await RhpFraming.WriteFrameAsync(client.Stream, payload, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            // client gone.
        }
        finally
        {
            client.WriteGate.Release();
        }
    }

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0)
        {
            return;
        }
        await lifecycle.CancelAsync().ConfigureAwait(false);
        try { listenSocket?.Close(); } catch { /* ignore */ }
        if (acceptLoop is { } loop)
        {
            try { await loop.ConfigureAwait(false); } catch { /* winding down */ }
        }
        foreach (var handle in handles.Values)
        {
            await TearDownHandleAsync(handle, notifyOwner: false).ConfigureAwait(false);
        }
        foreach (var client in clients.Keys)
        {
            client.Dispose();
        }
        listenSocket?.Dispose();
        lifecycle.Dispose();
    }

    // One connected RHP client (a TCP connection multiplexing many handles).
    private sealed class ClientState : IDisposable
    {
        public ClientState(Socket socket, bool authed)
        {
            Socket = socket;
            Stream = new NetworkStream(socket, ownsSocket: false);
            Authed = authed;
            Peer = socket.RemoteEndPoint?.ToString() ?? "?";
        }

        public Socket Socket { get; }
        public NetworkStream Stream { get; }
        public SemaphoreSlim WriteGate { get; } = new(1, 1);
        public string Peer { get; }
        public bool Authed { get; set; }

        public void Dispose()
        {
            try { Stream.Dispose(); } catch { /* ignore */ }
            try { Socket.Dispose(); } catch { /* ignore */ }
            WriteGate.Dispose();
        }
    }

    // One handle, owned by exactly one client (deviation D3). Two shapes: a STREAM handle
    // wraps a live packet connection (from an active open, or a listener's accepted child);
    // a SOCKET handle is the BSD-style lifecycle state (socket → bind → listen) whose
    // Registration, once listening, is the engine-side callsign registration.
    private sealed class RhpHandle
    {
        private int closed;

        public RhpHandle(int id, ClientState owner, INodeConnection? connection)
        {
            Id = id;
            Owner = owner;
            Connection = connection;
        }

        public int Id { get; }
        public ClientState Owner { get; }

        /// <summary>The live packet connection (stream handles); null for a socket handle.</summary>
        public INodeConnection? Connection { get; }
        public Task? Pump { get; set; }

        // Socket-lifecycle state (null/false on stream handles).
        public string? BoundLocal { get; set; }
        public string? BoundPort { get; set; }
        public IDisposable? Registration { get; set; }
        public bool Listening => Registration is not null;

        public bool Closed => Volatile.Read(ref closed) != 0;

        /// <summary>Mark closed; true if it already was (idempotent teardown).</summary>
        public bool MarkClosed() => Interlocked.Exchange(ref closed, 1) != 0;
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "RHPv2 server listening on {Endpoint}.")]
    private partial void LogListening(IPEndPoint endpoint);

    [LoggerMessage(Level = LogLevel.Information, Message = "RHP client connected from {Peer}.")]
    private partial void LogClientConnected(string peer);

    [LoggerMessage(Level = LogLevel.Information, Message = "RHP client {Peer} disconnected.")]
    private partial void LogClientClosed(string peer);

    [LoggerMessage(Level = LogLevel.Warning, Message = "RHP client {Peer} faulted.")]
    private partial void LogClientFaulted(Exception ex, string peer);

    [LoggerMessage(Level = LogLevel.Warning, Message = "RHP client {Peer} sent an unparseable frame ({Reason}); closing the connection.")]
    private partial void LogBadFrame(string peer, string reason);

    [LoggerMessage(Level = LogLevel.Information, Message = "RHP auth from {Peer} as '{User}': {Ok}.")]
    private partial void LogAuth(string peer, string user, bool ok);

    [LoggerMessage(Level = LogLevel.Information, Message = "RHP handle {Handle} opened to {Remote} for {Peer}.")]
    private partial void LogOpened(int handle, string remote, string peer);

    [LoggerMessage(Level = LogLevel.Information, Message = "RHP handle {Handle} closed.")]
    private partial void LogHandleClosed(int handle);

    [LoggerMessage(Level = LogLevel.Warning, Message = "RHP handle {Handle} pump faulted.")]
    private partial void LogPumpFaulted(Exception ex, int handle);

    [LoggerMessage(Level = LogLevel.Debug, Message = "RHP client {Peer} sent a non-request message type '{Type}'; ignored.")]
    private partial void LogIgnoredMessage(string peer, string type);

    [LoggerMessage(Level = LogLevel.Information, Message = "RHP handle {Handle} listening on {Callsign} for {Peer}.")]
    private partial void LogListening2(int handle, string callsign, string peer);

    [LoggerMessage(Level = LogLevel.Information, Message = "RHP child handle {Child} accepted from {Remote} (listener {Listener}).")]
    private partial void LogAccepted(int child, string remote, int listener);
}
