using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Packet.Core;
using Packet.Node.Core.Auth;
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

    /// <summary>
    /// Maximum number of concurrent client TCP connections. A connection accepted
    /// beyond this is closed immediately (the OS backlog is not allowed to absorb
    /// the overflow). Bounds connection-exhaustion / slowloris fan-out. Default 64.
    /// </summary>
    public int MaxConnections { get; init; } = 64;

    /// <summary>
    /// Maximum number of live handles (open streams + listening/socket handles) a
    /// single client connection may hold at once. A request that would exceed it is
    /// refused with errCode 4 ("No memory"). Bounds per-client memory growth from a
    /// client that opens/sockets in a loop. Default 256.
    /// </summary>
    public int MaxHandlesPerClient { get; init; } = 256;

    /// <summary>
    /// How long the rest of a frame may take to arrive after its first byte. A client
    /// may sit idle between frames indefinitely, but a peer that starts a frame and
    /// then stalls (slowloris) is dropped after this window. Default 30 seconds;
    /// <see cref="System.Threading.Timeout.InfiniteTimeSpan"/> disables it.
    /// </summary>
    public TimeSpan InFrameTimeout { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Optional per-source-IP throttle on failed <c>auth</c> attempts (only consulted
    /// when <see cref="RequireAuth"/> is on). Once an IP accumulates the throttle's
    /// failure budget within its window, further <c>auth</c> attempts from it are
    /// refused without reaching the password verify — the same sliding-window defence
    /// the web panel uses, applied to the cleartext RHP <c>auth</c> message so a
    /// publicly-exposed port can't be brute-forced. Null disables throttling.
    /// </summary>
    public LoginThrottle? AuthThrottle { get; init; }
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
/// async notifications carry a <c>seqno</c> and never an <c>id</c> — the seqno counter is
/// <b>per RHP connection</b>, starts at 0, and is shared across all push types
/// (recv/accept/status/server-close), per RHPTEST and the live wire; <c>errCode</c>/<c>errText</c>
/// are capitalised; an unknown <c>type</c> is answered with <c>{type}Reply</c> + errCode 2;
/// an absent <c>handle</c>/<c>data</c> field is errCode 12 ("Missing handle"/"Missing data" —
/// 3 is reserved for well-formed-but-unknown handles), and parameter-error replies omit the
/// handle echo; handles are numbered from one server-wide counter (from 100, like the
/// reference).</para>
/// <para><b>Named deviations (docs/rhp2-server.md):</b> a handle is usable only by the client
/// connection that created it (D3); a bad <c>auth</c> fails that attempt without wedging the
/// connection (D2); oversize sends are honoured, not silently dropped (D1); <c>openReply</c>
/// is sent when the connect resolves, carrying a real success/failure (D4); an invalid
/// local/remote callsign (e.g. the alphabetic SSID <c>G9DUM-S</c>) is refused with 6/7 where
/// XRouter accepts it and wedges (D7); <c>send</c> on a listener is 16 per RHPTEST where the
/// pinned live build answers 17 (D9).</para>
/// </remarks>
public sealed partial class RhpServer : IAsyncDisposable
{
    private const int RecvChunk = 2048;          // session bytes per recv push (escaped JSON stays well under the frame cap)
    private const int FirstHandle = 100;         // match the reference's visible numbering
    // PID carriage for ax25 datagrams is settled (packet.net#647, resolved) with NO pdn-specific
    // wire field: `dgram` is a PURE datagram (the UI PID is implicit no-Layer-3 0xF0), and
    // `custom` carries the PID as the FIRST octet of `data` (PID-in-`data`, the XRouter/G8PZT
    // standard — data[0] = PID on TX, prepended to data on RX). See docs/rhp2-server.md (R-7).
    private const byte DefaultPid = 0xF0;        // no-Layer-3 PID: the implicit PID of every `dgram` (pure-datagram) UI frame

    // errCode 12 errText as the live wire spells it (XRouter answers "Missing handle" /
    // "Missing data", not the spec table's generic "Bad parameter").
    private const string MissingHandleText = "Missing handle";
    private const string MissingDataText = "Missing data";

    private readonly RhpServerOptions options;
    private readonly IRhpGateway gateway;
    private readonly ILogger logger;
    private readonly CancellationTokenSource lifecycle = new();
    private readonly ConcurrentDictionary<int, RhpHandle> handles = new();
    private readonly ConcurrentDictionary<ClientState, byte> clients = new();
    private int nextHandle = FirstHandle - 1;
    private int connectionCount;
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

                // Connection cap: refuse the overflow immediately rather than letting
                // the OS backlog (or unbounded client tasks) absorb it. The slot is
                // released in HandleClientAsync's finally.
                if (Interlocked.Increment(ref connectionCount) > options.MaxConnections)
                {
                    Interlocked.Decrement(ref connectionCount);
                    LogConnectionRejected(accepted.RemoteEndPoint?.ToString() ?? "?", options.MaxConnections);
                    try { accepted.Close(); } catch { /* already gone */ }
                    continue;
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
                    frame = await RhpFraming.ReadFrameAsync(client.Stream, options.InFrameTimeout, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (TimeoutException)
                {
                    LogClientStalled(client.Peer, options.InFrameTimeout);
                    break;   // slowloris — peer started a frame and stalled; drop it
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
            Interlocked.Decrement(ref connectionCount);
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
                // Per-IP brute-force throttle (only when auth is required): a locked-out
                // source is refused before the password verify, exactly like the web 429.
                if (options.RequireAuth && options.AuthThrottle?.IsLocked(client.PeerIp) == true)
                {
                    LogAuthThrottled(client.Peer);
                    await WriteAsync(client, new AuthReplyMessage
                    {
                        Id = auth.Id,
                        ErrCode = RhpErrorCode.Unauthorised,
                        ErrText = RhpErrorCode.Text(RhpErrorCode.Unauthorised),
                    }, ct).ConfigureAwait(false);
                    break;
                }
                bool ok = options.Authenticate?.Invoke(auth.User, auth.Pass) ?? !options.RequireAuth;
                if (ok)
                {
                    client.Authed = true;
                    options.AuthThrottle?.Reset(client.PeerIp);
                }
                else if (options.RequireAuth)
                {
                    options.AuthThrottle?.RecordFailure(client.PeerIp);
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
            // The deferred ops still validate their parameters first: an absent handle is
            // errCode 12 ("Missing handle") before the 16 deferral — exactly the live wire's
            // precedence (XRouter answers 12 on connect/status/sendto with no handle too).
            case ConnectMessage m:
                await WriteAsync(client, m.Handle is null
                    ? new ConnectReplyMessage { Id = m.Id, ErrCode = RhpErrorCode.BadParameter, ErrText = MissingHandleText }
                    : new ConnectReplyMessage { Id = m.Id, Handle = m.Handle, ErrCode = RhpErrorCode.OperationNotSupported, ErrText = "Operation not supported (use open with the Active flag)" }, ct).ConfigureAwait(false);
                break;
            case SendToMessage m:
                await HandleSendToAsync(client, m, ct).ConfigureAwait(false);
                break;
            case StatusMessage m:
                await WriteAsync(client, m.Handle is null
                    ? new StatusReplyMessage { Id = m.Id, ErrCode = RhpErrorCode.BadParameter, ErrText = MissingHandleText }
                    : new StatusReplyMessage { Id = m.Id, Handle = m.Handle, ErrCode = RhpErrorCode.OperationNotSupported, ErrText = RhpErrorCode.Text(RhpErrorCode.OperationNotSupported) }, ct).ConfigureAwait(false);
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
        else if (open.Mode is not (SocketMode.Stream or SocketMode.Dgram or SocketMode.Custom))
        {
            (err, text) = (RhpErrorCode.OperationNotSupported, $"mode '{open.Mode}' is not implemented yet (stream/dgram/custom only)");
        }
        else if (open.Local is { } local && !string.IsNullOrWhiteSpace(local) && !IsValidCallsign(local))
        {
            // Refused at the wire (deviation D7): XRouter accepts an alphabetic SSID like
            // G9DUM-S here and then wedges in background SABM retries. An ABSENT local is
            // fine — a stream open defaults to the node callsign (deviation D8), a dgram open
            // stays unbound-source until sendto names one.
            (err, text) = (RhpErrorCode.InvalidLocalAddress, RhpErrorCode.Text(RhpErrorCode.InvalidLocalAddress));
        }
        else if (open.Mode == SocketMode.Stream && (open.Flags & (int)OpenFlags.Active) == 0)
        {
            // Deferred by name (docs/rhp2-server.md §Scope): the passive form of `open` —
            // the BSD socket/bind/listen path covers every validated client's listener needs.
            // (Dgram is connectionless — Active/Passive doesn't apply.)
            (err, text) = (RhpErrorCode.OperationNotSupported, "passive open is not supported (use socket/bind/listen); only Active (0x80) opens");
        }
        else if (open.Mode == SocketMode.Stream && string.IsNullOrWhiteSpace(open.Remote))
        {
            (err, text) = (RhpErrorCode.InvalidRemoteAddress, RhpErrorCode.Text(RhpErrorCode.InvalidRemoteAddress));
        }
        else if (open.Mode == SocketMode.Stream && !IsValidCallsign(open.Remote!))
        {
            (err, text) = (RhpErrorCode.InvalidRemoteAddress, RhpErrorCode.Text(RhpErrorCode.InvalidRemoteAddress));
        }

        if (err != 0)
        {
            await WriteAsync(client, new OpenReplyMessage { Id = open.Id, Handle = 0, ErrCode = err, ErrText = text }, ct).ConfigureAwait(false);
            return;
        }

        // Dgram/custom open = the combined socket+bind form: create a bound datagram socket and
        // reply Ok. `dgram` is a pure UI datagram (implicit PID 0xF0); `custom` carries the PID as
        // data[0]. (connect / send-on-connected-dgram stay deferred — docs/rhp2-server.md R-6/R-7.)
        if (open.Mode is SocketMode.Dgram or SocketMode.Custom)
        {
            var kind = open.Mode == SocketMode.Custom ? DatagramKind.Custom : DatagramKind.Dgram;
            await CreateBoundDatagramHandleAsync(client, open.Id, open.Local, open.Port, kind, ct).ConfigureAwait(false);
            return;
        }

        // Per-client handle cap: refuse before committing to the (slow) connect, so a
        // client can't spin up unbounded in-flight opens. The reservation is released by
        // TearDownHandleAsync once the handle exists, or here if the connect never lands.
        if (!client.TryReserveHandle(options.MaxHandlesPerClient))
        {
            LogHandleCapReached(client.Peer, options.MaxHandlesPerClient);
            await WriteAsync(client, new OpenReplyMessage { Id = open.Id, Handle = 0, ErrCode = RhpErrorCode.NoMemory, ErrText = RhpErrorCode.Text(RhpErrorCode.NoMemory) }, ct).ConfigureAwait(false);
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
                client.ReleaseHandle();   // reserved slot never became a handle
                await WriteAsync(client, new OpenReplyMessage { Id = open.Id, Handle = 0, ErrCode = gex.ErrCode, ErrText = gex.Message }, ct).ConfigureAwait(false);
                return;
            }
            catch (OperationCanceledException)
            {
                client.ReleaseHandle();
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
                Seqno = client.NextSeqno(),
            }, ct).ConfigureAwait(false);

            handle.Pump = Task.Run(() => PumpHandleAsync(handle, ct), CancellationToken.None);
        }, CancellationToken.None);
    }

    // Create a datagram socket bound to (local, port) in one step — the `open`(dgram|custom) form,
    // and the shared body the socket→bind path reaches at bind. Registers the promiscuous UI RX
    // subscription (torn down with the handle) and replies Ok + handle. A bad bind port lands on
    // the openReply. `local` may be null (an unbound-source datagram socket: RX is promiscuous, and
    // sendto names the source per datagram). `kind` selects the PID carriage (Dgram = implicit
    // 0xF0; Custom = PID in data[0]).
    private async Task CreateBoundDatagramHandleAsync(ClientState client, int? id, string? local, string? port, DatagramKind kind, CancellationToken ct)
    {
        if (!client.TryReserveHandle(options.MaxHandlesPerClient))
        {
            LogHandleCapReached(client.Peer, options.MaxHandlesPerClient);
            await WriteAsync(client, new OpenReplyMessage { Id = id, Handle = 0, ErrCode = RhpErrorCode.NoMemory, ErrText = RhpErrorCode.Text(RhpErrorCode.NoMemory) }, ct).ConfigureAwait(false);
            return;
        }

        var handle = new RhpHandle(Interlocked.Increment(ref nextHandle), client, connection: null) { Kind = kind };
        var boundPort = port?.Trim();
        handle.BoundPort = string.IsNullOrEmpty(boundPort) || boundPort == "0" ? null : boundPort;
        handle.BoundLocal = string.IsNullOrWhiteSpace(local) ? null : local.Trim();
        handles[handle.Id] = handle;

        try
        {
            SubscribeDgramRx(handle);
        }
        catch (RhpGatewayException gex)
        {
            handle.Owner.ReleaseHandle();
            handles.TryRemove(handle.Id, out _);
            await WriteAsync(client, new OpenReplyMessage { Id = id, Handle = 0, ErrCode = gex.ErrCode, ErrText = gex.Message }, ct).ConfigureAwait(false);
            return;
        }

        LogOpenedDgram(handle.Id, handle.BoundLocal ?? "(unbound)", client.Peer);
        await WriteAsync(client, new OpenReplyMessage
        {
            Id = id,
            Handle = handle.Id,
            ErrCode = RhpErrorCode.Ok,
            ErrText = RhpErrorCode.Text(RhpErrorCode.Ok),
        }, ct).ConfigureAwait(false);
    }

    // (Re)attach a dgram handle's promiscuous UI RX subscription for its current BoundPort — the
    // event-driven analogue of a stream listener's Registration (no Pump task). Every received UI
    // frame on the scoped port(s) becomes a `recv` push. Torn down in TearDownHandleAsync. Throws
    // RhpGatewayException on a bad port (→ the caller's reply).
    private void SubscribeDgramRx(RhpHandle handle)
    {
        handle.UiSubscription?.Dispose();
        handle.UiSubscription = gateway.RegisterUiListener(handle.BoundPort, dg => OnUiReceivedAsync(handle, dg));
    }

    // An inbound UI datagram on a bound datagram handle → an async recv push. Promiscuous: the
    // frame's true source (→ remote) / destination (→ local) / info are surfaced verbatim; the
    // client filters by recv.local. Carries a per-connection seqno and no id (mirrors
    // PumpHandleAsync). PID carriage differs by socket kind and adds no separate wire field: a
    // `dgram` socket delivers info as-is (its PID is implicitly 0xF0); a `custom` socket prepends
    // the frame's PID as the first octet of `data` (data = [pid] ++ info — the G8PZT standard).
    private static async Task OnUiReceivedAsync(RhpHandle handle, UiDatagram dg)
    {
        if (handle.Closed)
        {
            return;
        }

        string data;
        if (handle.IsCustom)
        {
            var info = dg.Info.Span;
            var payload = new byte[info.Length + 1];
            payload[0] = dg.Pid;
            info.CopyTo(payload.AsSpan(1));
            data = RhpDataEncoding.ToWireString(payload);
        }
        else
        {
            data = RhpDataEncoding.ToWireString(dg.Info.Span);
        }

        await WriteAsync(handle.Owner, new RecvMessage
        {
            Handle = handle.Id,
            Data = data,
            Port = dg.PortLabel,
            Local = dg.Dest,
            Remote = dg.Source,
            Seqno = handle.Owner.NextSeqno(),
        }, CancellationToken.None).ConfigureAwait(false);
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
        else if (msg.Mode is not (SocketMode.Stream or SocketMode.Dgram or SocketMode.Custom))
        {
            (err, text) = (RhpErrorCode.OperationNotSupported, $"mode '{msg.Mode}' is not implemented yet (stream/dgram/custom only)");
        }

        if (err != 0)
        {
            await WriteAsync(client, new SocketReplyMessage { Id = msg.Id, Handle = null, ErrCode = err, ErrText = text }, ct).ConfigureAwait(false);
            return;
        }

        // Per-client handle cap — a socket handle counts like any other.
        if (!client.TryReserveHandle(options.MaxHandlesPerClient))
        {
            LogHandleCapReached(client.Peer, options.MaxHandlesPerClient);
            await WriteAsync(client, new SocketReplyMessage { Id = msg.Id, Handle = null, ErrCode = RhpErrorCode.NoMemory, ErrText = RhpErrorCode.Text(RhpErrorCode.NoMemory) }, ct).ConfigureAwait(false);
            return;
        }

        // A datagram socket (dgram or custom) is unbound until `bind` (which sets
        // BoundLocal/BoundPort and starts the promiscuous UI RX subscription); a stream socket goes
        // on to bind→listen for accepts.
        var handle = new RhpHandle(Interlocked.Increment(ref nextHandle), client, connection: null)
        {
            Kind = msg.Mode switch
            {
                SocketMode.Dgram => DatagramKind.Dgram,
                SocketMode.Custom => DatagramKind.Custom,
                _ => DatagramKind.None,
            },
        };
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
        if (msg.Handle is not { } bindHandle)
        {
            // Absent handle is a missing PARAMETER (12), not an invalid handle (3) — RHPTEST:
            // "3 is for handles that are well-formed but unknown". No handle echo: there is
            // nothing truthful to echo (matching the live wire's shape).
            await WriteAsync(client, new BindReplyMessage { Id = msg.Id, ErrCode = RhpErrorCode.BadParameter, ErrText = MissingHandleText }, ct).ConfigureAwait(false);
            return;
        }
        if (!handles.TryGetValue(bindHandle, out var handle) || handle.Owner != client || handle.Connection is not null)
        {
            await WriteAsync(client, new BindReplyMessage { Id = msg.Id, Handle = bindHandle, ErrCode = RhpErrorCode.InvalidHandle, ErrText = RhpErrorCode.Text(RhpErrorCode.InvalidHandle) }, ct).ConfigureAwait(false);
            return;
        }
        if (string.IsNullOrWhiteSpace(msg.Local) || !IsValidCallsign(msg.Local))
        {
            // Absent, empty or malformed local is 6 on the live wire — and an invalid
            // callsign (e.g. the alphabetic SSID G9DUM-S) is refused HERE, deterministically,
            // where XRouter accepts the bind and later wedges (deviation D7).
            await WriteAsync(client, new BindReplyMessage { Id = msg.Id, Handle = bindHandle, ErrCode = RhpErrorCode.InvalidLocalAddress, ErrText = RhpErrorCode.Text(RhpErrorCode.InvalidLocalAddress) }, ct).ConfigureAwait(false);
            return;
        }

        // Re-bind before listen is legal (XRouter allows a second bind); a null port means all
        // ports — exactly what DAPPS sends — and the string "0" is its wire synonym (XRouter
        // convention, rhp2lib field notes §12).
        var boundPort = msg.Port?.Trim();
        handle.BoundLocal = msg.Local.Trim();
        handle.BoundPort = string.IsNullOrEmpty(boundPort) || boundPort == "0" ? null : boundPort;

        // A datagram socket (dgram or custom) starts (or re-points, on a second bind) its
        // promiscuous UI RX subscription here — there is no listen step for datagrams. A bad bind
        // port lands on the bindReply.
        if (handle.IsDatagram)
        {
            try
            {
                SubscribeDgramRx(handle);
            }
            catch (RhpGatewayException gex)
            {
                await WriteAsync(client, new BindReplyMessage { Id = msg.Id, Handle = bindHandle, ErrCode = gex.ErrCode, ErrText = gex.Message }, ct).ConfigureAwait(false);
                return;
            }
        }

        await WriteAsync(client, new BindReplyMessage { Id = msg.Id, Handle = bindHandle, ErrCode = RhpErrorCode.Ok, ErrText = RhpErrorCode.Text(RhpErrorCode.Ok) }, ct).ConfigureAwait(false);
    }

    private async Task HandleListenAsync(ClientState client, ListenMessage msg, CancellationToken ct)
    {
        if (msg.Handle is not { } listenHandle)
        {
            await WriteAsync(client, new ListenReplyMessage { Id = msg.Id, ErrCode = RhpErrorCode.BadParameter, ErrText = MissingHandleText }, ct).ConfigureAwait(false);
            return;
        }
        if (!handles.TryGetValue(listenHandle, out var handle) || handle.Owner != client || handle.Connection is not null)
        {
            await WriteAsync(client, new ListenReplyMessage { Id = msg.Id, Handle = listenHandle, ErrCode = RhpErrorCode.InvalidHandle, ErrText = RhpErrorCode.Text(RhpErrorCode.InvalidHandle) }, ct).ConfigureAwait(false);
            return;
        }
        if (handle.IsDatagram)
        {
            // A datagram socket (dgram or custom) has no listening state — its recv is the
            // promiscuous UI tap, already active from bind. Listen on it is not supported
            // (docs/rhp2-server.md R-6/R-7).
            await WriteAsync(client, new ListenReplyMessage { Id = msg.Id, Handle = msg.Handle, ErrCode = RhpErrorCode.OperationNotSupported, ErrText = RhpErrorCode.Text(RhpErrorCode.OperationNotSupported) }, ct).ConfigureAwait(false);
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

        // The child handle counts against the listener's owner — refuse (and drop the
        // inbound) rather than let accepts grow a client's handle set without bound.
        if (!listenerHandle.Owner.TryReserveHandle(options.MaxHandlesPerClient))
        {
            LogHandleCapReached(listenerHandle.Owner.Peer, options.MaxHandlesPerClient);
            await connection.DisposeAsync().ConfigureAwait(false);
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
            Seqno = listenerHandle.Owner.NextSeqno(),
        }, CancellationToken.None).ConfigureAwait(false);

        // The protocol lifecycle (PWP-0222 / rhp2lib protocol primer): the accept push is
        // followed by a status push announcing the CHILD's link is up — same flags as the
        // active-open path's announcement, before any recv can flow.
        await WriteAsync(listenerHandle.Owner, new StatusMessage
        {
            Handle = child.Id,
            Flags = (int)(StatusFlags.Connected | StatusFlags.ConOk),
            Seqno = listenerHandle.Owner.NextSeqno(),
        }, CancellationToken.None).ConfigureAwait(false);

        child.Pump = Task.Run(() => PumpHandleAsync(child, lifecycle.Token), CancellationToken.None);
    }

    private async Task HandleSendAsync(ClientState client, SendMessage send, CancellationToken ct)
    {
        if (send.Handle is not { } sendHandle)
        {
            // Absent handle is a missing parameter (12), not an invalid handle (3); the
            // live wire validates the handle's PRESENCE first and never echoes one back.
            await WriteAsync(client, new SendReplyMessage { Id = send.Id, ErrCode = RhpErrorCode.BadParameter, ErrText = MissingHandleText }, ct).ConfigureAwait(false);
            return;
        }

        // Deviation D3: a handle is only usable by the connection that created it — anyone
        // else sees the same "invalid handle" an unknown id gets (no existence oracle).
        if (!handles.TryGetValue(sendHandle, out var handle) || handle.Owner != client)
        {
            await WriteAsync(client, new SendReplyMessage { Id = send.Id, Handle = sendHandle, ErrCode = RhpErrorCode.InvalidHandle, ErrText = RhpErrorCode.Text(RhpErrorCode.InvalidHandle) }, ct).ConfigureAwait(false);
            return;
        }
        if (handle.IsDatagram)
        {
            // A datagram socket (dgram or custom) sends via `sendto` (explicit dest per datagram);
            // plain `send` on it is send-on-connected-dgram, which is deferred (docs/rhp2-server.md R-6).
            await WriteAsync(client, new SendReplyMessage { Id = send.Id, Handle = sendHandle, ErrCode = RhpErrorCode.OperationNotSupported, ErrText = RhpErrorCode.Text(RhpErrorCode.OperationNotSupported) }, ct).ConfigureAwait(false);
            return;
        }
        if (send.Data is null)
        {
            // `data` is mandatory even when empty (RHPTEST): absence is errCode 12
            // ("Missing data", no handle echo — the live wire's exact shape), while
            // "data":"" is a legal zero-byte send that proceeds like any other.
            await WriteAsync(client, new SendReplyMessage { Id = send.Id, ErrCode = RhpErrorCode.BadParameter, ErrText = MissingDataText }, ct).ConfigureAwait(false);
            return;
        }
        if (handle.Listening)
        {
            // A LISTENER rejects everything but accept/close with 16 (RHPTEST, against
            // XRouter v505d) — distinct from 17 for a non-listening unconnected stream.
            // Deviation D9: the pinned live container (505c) still answers 17 here.
            await WriteAsync(client, new SendReplyMessage { Id = send.Id, Handle = sendHandle, ErrCode = RhpErrorCode.OperationNotSupported, ErrText = RhpErrorCode.Text(RhpErrorCode.OperationNotSupported) }, ct).ConfigureAwait(false);
            return;
        }
        if (handle.Connection is null)
        {
            // A non-listening socket handle has no link to send on — the wire's 17.
            await WriteAsync(client, new SendReplyMessage { Id = send.Id, Handle = sendHandle, ErrCode = RhpErrorCode.NotConnected, ErrText = RhpErrorCode.Text(RhpErrorCode.NotConnected) }, ct).ConfigureAwait(false);
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

    // sendto: emit one connectionless AX.25 UI datagram (dgram or custom mode). Source is the
    // explicit sendto.local, else the bound local; destination is sendto.remote. PID carriage
    // depends on the socket kind (no separate wire field): a `dgram` socket always emits PID 0xF0
    // (pure datagram) with info = the whole `data`; a `custom` socket takes the PID from data[0]
    // and the info from data[1..] (the G8PZT standard, PID-in-`data`). An empty `data` is refused
    // (errCode 1) — a dgram UI frame needs an info field, and a custom datagram needs at least the
    // PID octet. TX runs through the gateway (RF or loopback).
    private async Task HandleSendToAsync(ClientState client, SendToMessage msg, CancellationToken ct)
    {
        if (msg.Handle is not { } sendHandle)
        {
            // Absent handle → 12 "Missing handle" (never 3), no handle echo — the live wire's shape.
            await WriteAsync(client, new SendToReplyMessage { Id = msg.Id, ErrCode = RhpErrorCode.BadParameter, ErrText = MissingHandleText }, ct).ConfigureAwait(false);
            return;
        }
        // Deviation D3: usable only by the connection that created it.
        if (!handles.TryGetValue(sendHandle, out var handle) || handle.Owner != client)
        {
            await WriteAsync(client, new SendToReplyMessage { Id = msg.Id, Handle = sendHandle, ErrCode = RhpErrorCode.InvalidHandle, ErrText = RhpErrorCode.Text(RhpErrorCode.InvalidHandle) }, ct).ConfigureAwait(false);
            return;
        }
        if (!handle.IsDatagram)
        {
            // sendto is a datagram operation; a stream/socket handle doesn't support it.
            await WriteAsync(client, new SendToReplyMessage { Id = msg.Id, Handle = sendHandle, ErrCode = RhpErrorCode.OperationNotSupported, ErrText = RhpErrorCode.Text(RhpErrorCode.OperationNotSupported) }, ct).ConfigureAwait(false);
            return;
        }
        if (msg.Data is null)
        {
            // `data` mandatory even when empty (absence is 12 "Missing data", mirroring send).
            await WriteAsync(client, new SendToReplyMessage { Id = msg.Id, Handle = sendHandle, ErrCode = RhpErrorCode.BadParameter, ErrText = MissingDataText }, ct).ConfigureAwait(false);
            return;
        }
        if (msg.Data.Length == 0)
        {
            // An EMPTY payload is refused (errCode 1) — distinct from UDP's legal zero-byte
            // datagram (protocol.md): a `dgram` UI frame must carry an information field, and a
            // `custom` datagram must carry at least the PID octet (data[0]).
            var why = handle.IsCustom
                ? "custom datagram must carry at least the PID octet"
                : "AX.25 datagram payload must not be empty";
            await WriteAsync(client, new SendToReplyMessage { Id = msg.Id, Handle = sendHandle, ErrCode = RhpErrorCode.Unspecified, ErrText = why }, ct).ConfigureAwait(false);
            return;
        }

        // Source: explicit sendto.local overrides; else the bound local. A UI frame needs one,
        // refused at the wire like open/bind (deviation D7) if absent or malformed.
        var source = !string.IsNullOrWhiteSpace(msg.Local) ? msg.Local!.Trim() : handle.BoundLocal;
        if (string.IsNullOrWhiteSpace(source) || !IsValidCallsign(source))
        {
            await WriteAsync(client, new SendToReplyMessage { Id = msg.Id, Handle = sendHandle, ErrCode = RhpErrorCode.InvalidLocalAddress, ErrText = RhpErrorCode.Text(RhpErrorCode.InvalidLocalAddress) }, ct).ConfigureAwait(false);
            return;
        }
        if (string.IsNullOrWhiteSpace(msg.Remote) || !IsValidCallsign(msg.Remote!))
        {
            await WriteAsync(client, new SendToReplyMessage { Id = msg.Id, Handle = sendHandle, ErrCode = RhpErrorCode.InvalidRemoteAddress, ErrText = RhpErrorCode.Text(RhpErrorCode.InvalidRemoteAddress) }, ct).ConfigureAwait(false);
            return;
        }

        var portLabel = string.IsNullOrWhiteSpace(msg.Port) ? handle.BoundPort : msg.Port!.Trim();
        if (portLabel == "0")
        {
            portLabel = null;   // XRouter convention: "0" = the default port (like bind's all-ports)
        }

        try
        {
            // PID carriage (no separate wire field): a `custom` socket takes the PID from the first
            // octet of `data` and the info from the rest; a `dgram` socket is a pure datagram — the
            // whole `data` is info and the PID is the implicit no-Layer-3 0xF0.
            var bytes = RhpDataEncoding.FromWireString(msg.Data);   // non-empty (checked above)
            byte pid;
            ReadOnlyMemory<byte> info;
            if (handle.IsCustom)
            {
                pid = bytes[0];
                info = bytes.AsMemory(1);
            }
            else
            {
                pid = DefaultPid;
                info = bytes;
            }
            await gateway.SendUiAsync(portLabel, source!, msg.Remote!.Trim(), info, pid, ct).ConfigureAwait(false);
            await WriteAsync(client, new SendToReplyMessage { Id = msg.Id, Handle = sendHandle, ErrCode = RhpErrorCode.Ok, ErrText = RhpErrorCode.Text(RhpErrorCode.Ok) }, ct).ConfigureAwait(false);
        }
        catch (RhpGatewayException gex)
        {
            // The gateway owns the "why" (no such port, no route) — copy its code/text verbatim.
            await WriteAsync(client, new SendToReplyMessage { Id = msg.Id, Handle = sendHandle, ErrCode = gex.ErrCode, ErrText = gex.Message }, ct).ConfigureAwait(false);
        }
    }

    private async Task HandleCloseAsync(ClientState client, CloseMessage close, CancellationToken ct)
    {
        if (close.Handle is not { } closeHandle)
        {
            // RHPTEST: a MISSING handle is 12 ("Missing handle"), not 3 — "3 is for handles
            // that are well-formed but unknown". No handle echo, matching the live wire.
            await WriteAsync(client, new CloseReplyMessage { Id = close.Id, ErrCode = RhpErrorCode.BadParameter, ErrText = MissingHandleText }, ct).ConfigureAwait(false);
            return;
        }
        if (!handles.TryGetValue(closeHandle, out var handle) || handle.Owner != client)
        {
            await WriteAsync(client, new CloseReplyMessage { Id = close.Id, Handle = closeHandle, ErrCode = RhpErrorCode.InvalidHandle, ErrText = RhpErrorCode.Text(RhpErrorCode.InvalidHandle) }, ct).ConfigureAwait(false);
            return;
        }

        await TearDownHandleAsync(handle, notifyOwner: false).ConfigureAwait(false);
        await WriteAsync(client, new CloseReplyMessage { Id = close.Id, Handle = closeHandle, ErrCode = RhpErrorCode.Ok, ErrText = RhpErrorCode.Text(RhpErrorCode.Ok) }, ct).ConfigureAwait(false);
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
                        Seqno = handle.Owner.NextSeqno(),
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
        handle.Owner.ReleaseHandle();   // free the per-client reservation
        handles.TryRemove(handle.Id, out _);
        handle.Registration?.Dispose();   // a listener stops answering for its callsign
        handle.UiSubscription?.Dispose();   // a dgram socket stops hearing UI (analogous to Registration)
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
                Seqno = handle.Owner.NextSeqno(),
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

    // The strict outbound-construction callsign rule applied at the RHP wire: AX.25 SSIDs
    // are numeric 0–15, so e.g. G9DUM-S is refused (errCode 6/7) rather than handed to the
    // engine. XRouter accepts these and wedges in background SABM retries (deviation D7).
    private static bool IsValidCallsign(string text)
        => Callsign.TryParse(text.Trim().ToUpperInvariant(), out _);

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
        private int seqno = -1;   // first push must carry seqno 0 (RHPTEST; live wire)
        private int handleCount;  // live handles owned by this connection (capped)

        public ClientState(Socket socket, bool authed)
        {
            Socket = socket;
            Stream = new NetworkStream(socket, ownsSocket: false);
            Authed = authed;
            Peer = socket.RemoteEndPoint?.ToString() ?? "?";
            PeerIp = (socket.RemoteEndPoint as IPEndPoint)?.Address.ToString() ?? "unknown";
        }

        public Socket Socket { get; }
        public NetworkStream Stream { get; }
        public SemaphoreSlim WriteGate { get; } = new(1, 1);
        public string Peer { get; }

        /// <summary>The source IP alone (no port) — the throttle key, stable across the
        /// many short-lived connections a brute-forcer would open.</summary>
        public string PeerIp { get; }
        public bool Authed { get; set; }

        /// <summary>Next push seqno: one counter per RHP connection, starting at 0, shared
        /// across all push types (recv/accept/status/server-close) — RHPTEST-verified.</summary>
        public int NextSeqno() => Interlocked.Increment(ref seqno);

        /// <summary>Atomically reserve one handle slot if the client is under
        /// <paramref name="max"/>; returns false (no reservation) when at the cap.</summary>
        public bool TryReserveHandle(int max)
        {
            while (true)
            {
                int cur = Volatile.Read(ref handleCount);
                if (cur >= max)
                {
                    return false;
                }
                if (Interlocked.CompareExchange(ref handleCount, cur + 1, cur) == cur)
                {
                    return true;
                }
            }
        }

        /// <summary>Release one reserved handle slot (on teardown, or a reserved
        /// open whose connect failed before a handle existed).</summary>
        public void ReleaseHandle() => Interlocked.Decrement(ref handleCount);

        public void Dispose()
        {
            try { Stream.Dispose(); } catch { /* ignore */ }
            try { Socket.Dispose(); } catch { /* ignore */ }
            WriteGate.Dispose();
        }
    }

    // The connectionless-datagram flavour of a socket handle. None = a stream handle. Dgram and
    // Custom share the whole datagram lifecycle (sendto TX + promiscuous UI recv, no connect/listen)
    // and differ only in PID carriage: Dgram is a pure datagram (implicit no-Layer-3 0xF0), Custom
    // carries the AX.25 PID as the first octet of the sendto/recv `data` payload (the G8PZT standard).
    private enum DatagramKind
    {
        None = 0,
        Dgram,
        Custom,
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

        /// <summary>The connectionless-datagram flavour of this socket: None for a stream handle,
        /// Dgram for a pure UI datagram (implicit PID 0xF0), Custom for a PID-in-<c>data</c> UI
        /// datagram (the first payload octet is the AX.25 PID). Both datagram flavours share the
        /// sendto-TX + promiscuous-UI-recv lifecycle; they differ only in PID carriage.</summary>
        public DatagramKind Kind { get; init; }

        /// <summary>True for either datagram flavour (dgram or custom): sendto TX + promiscuous UI
        /// recv, no connect/listen. False for the stream socket/connection handles.</summary>
        public bool IsDatagram => Kind is DatagramKind.Dgram or DatagramKind.Custom;

        /// <summary>True for a CUSTOM datagram socket: the AX.25 PID is the first octet of the
        /// sendto/recv <c>data</c> payload rather than the implicit 0xF0 a plain dgram uses.</summary>
        public bool IsCustom => Kind == DatagramKind.Custom;

        // Socket-lifecycle state (null/false on stream handles).
        public string? BoundLocal { get; set; }
        public string? BoundPort { get; set; }
        public IDisposable? Registration { get; set; }
        public bool Listening => Registration is not null;

        /// <summary>A dgram socket's promiscuous UI RX subscription (the event-driven analogue of a
        /// stream listener's <see cref="Registration"/>); null until bound. Disposed on teardown.</summary>
        public IDisposable? UiSubscription { get; set; }

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

    [LoggerMessage(Level = LogLevel.Warning, Message = "RHP connection from {Peer} refused: at the {Max}-connection cap.")]
    private partial void LogConnectionRejected(string peer, int max);

    [LoggerMessage(Level = LogLevel.Warning, Message = "RHP client {Peer} stalled part-way through a frame (exceeded {Timeout}); dropping the connection.")]
    private partial void LogClientStalled(string peer, TimeSpan timeout);

    [LoggerMessage(Level = LogLevel.Warning, Message = "RHP client {Peer} refused a new handle: at the {Max}-handle per-connection cap.")]
    private partial void LogHandleCapReached(string peer, int max);

    [LoggerMessage(Level = LogLevel.Information, Message = "RHP auth from {Peer} as '{User}': {Ok}.")]
    private partial void LogAuth(string peer, string user, bool ok);

    [LoggerMessage(Level = LogLevel.Warning, Message = "RHP auth from {Peer} refused: source IP is throttled (too many recent failures).")]
    private partial void LogAuthThrottled(string peer);

    [LoggerMessage(Level = LogLevel.Information, Message = "RHP handle {Handle} opened to {Remote} for {Peer}.")]
    private partial void LogOpened(int handle, string remote, string peer);

    [LoggerMessage(Level = LogLevel.Information, Message = "RHP datagram handle {Handle} bound to {Local} for {Peer}.")]
    private partial void LogOpenedDgram(int handle, string local, string peer);

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
