using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Packet.Ax25;
using Packet.Ax25.Session;
using Packet.Core;
using Packet.Node.Core.Api;
using Packet.Node.Core.Audit;
using Packet.Node.Core.Console;
using Packet.Node.Core.Hosting;

namespace Packet.Node.Api;

/// <summary>
/// The session-action + ping side of the pdn node control API (Slice 3, step 4): the
/// direct-supervisor actions the web Sessions screen and the ping tool drive —
/// connect-out (<c>POST /sessions</c>), disconnect (<c>DELETE /sessions/{id}</c>), send
/// one line into a connected-mode session (<c>POST /sessions/{id}/send</c>), and the
/// connectionless TEST ping (<c>POST /ping</c>).
/// </summary>
/// <remarks>
/// <para>
/// Every action that touches the live port/session set runs under the host's exclusive
/// gate (<see cref="NodeHostedService.RunExclusiveAsync{T}"/>) — the same gate the
/// reconcile worker holds — so a web action can never race a config reconcile (or another
/// action) mutating ports or sessions. The critical sections are kept short: the gate is
/// held only to <em>capture</em> a listener/session reference or to post a single event;
/// the one long-running operation, a connect-out dial that awaits SABM/UA, runs
/// <em>outside</em> the gate so it doesn't block reconciles for the dial's duration.
/// </para>
/// <para>
/// <b>Connect-out (v1 scope).</b> A web connect-out opens the session via the supervisor's
/// resolved connector — which already encodes "a callsign dials out over AX.25 on the
/// local channel, a NET/ROM alias routes across the network" (the same logic the console's
/// <c>Connect</c> command uses) — and surfaces the new session in <c>/sessions</c>. There
/// is <b>no</b> console-bridge / received-data streaming in v1: this endpoint does not run
/// a node-command service over the opened connection, and there is no per-session I/O
/// stream (the live monitor shows the frames). <c>portId</c> in the request body is
/// validated (it must name a running port when supplied) but the dial itself goes through
/// the supervisor's <em>default</em>-resolved connector — a per-<c>portId</c> dial selector
/// needs a per-port connector factory on the supervisor that is a named later step; v1
/// dials on the deterministic default port / best NET/ROM route.
/// </para>
/// <para>
/// <b>Ping (<c>POST /ping</c>).</b> A connectionless AX.25 TEST ping ("axping"): it sends N
/// TEST command frames to a station and correlates each echo off the listener's
/// <see cref="Ax25Listener.FrameTraced"/> stream to measure RTT. The correlation core lives
/// in <see cref="AxPinger"/> (web-free, in <c>Packet.Node.Core</c>), driven here through a
/// captured <see cref="Ax25Listener"/> wrapped in a <see cref="ListenerAxPingChannel"/>. The
/// pinger only READS frames + calls the public <see cref="Ax25Listener.SendTestAsync"/>, so —
/// unlike the connect/disconnect/send actions — it does NOT mutate the live port set and does
/// NOT run under the host's <c>RunExclusiveAsync</c> gate (the listener reference is captured
/// once, defensively; if the port is gone mid-run, sends throw and the run records loss). A
/// peer that doesn't implement TEST simply never answers → all timeouts → loss 100%, which is
/// a normal result returned as a 200, not an error.
/// </para>
/// <para>
/// Auth is a later step — like the read API, the SSE feed, the config write API, and the
/// port-management API, these are unauthenticated and the node binds 127.0.0.1 by default.
/// Ping RTT + per-ping timeout ride the injected <see cref="TimeProvider"/> /
/// <c>Stopwatch</c>, never <c>DateTime.Now</c> (repo rule §2.7).
/// </para>
/// </remarks>
public static class PdnSessionsApi
{
    // Bound a connect-out dial: the request token, linked with a ceiling so a wedged
    // SABM/UA exchange can't hold a server thread indefinitely. The listener's own
    // (N2+1)·T1V backstop is usually tighter, but this is a hard outer bound.
    private static readonly TimeSpan DialTimeout = TimeSpan.FromSeconds(30);

    // SSE heartbeat cadence for the per-session output stream — a `: ping` comment keeps the
    // stream warm through buffering proxies between (possibly infrequent) output chunks.
    private static readonly TimeSpan HeartbeatInterval = TimeSpan.FromSeconds(15);

    // Per-probe TEST-echo wait for the connectionless ping. ~5s comfortably covers a
    // round trip over a slow RF / net-sim path; a peer that doesn't answer TEST simply
    // times out per probe (loss), which is a normal result.
    private static readonly TimeSpan PerPingTimeout = TimeSpan.FromSeconds(5);

    // Clamp the requested probe count to a safe range so a single ping request can't
    // tie the link up indefinitely (worst case MaxPingCount × PerPingTimeout sequentially).
    private const int MinPingCount = 1;
    private const int MaxPingCount = 20;

    /// <summary>
    /// Map the session-action + ping endpoints under <c>/api/v1</c>. Called from the node
    /// composition root after the port-management API and before the SPA fallback (the
    /// specific routes win over the <c>/api/{**rest}</c> catch-all regardless of order).
    /// </summary>
    public static void MapPdnSessionsApi(this WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);

        // Every session action + ping is `operate` (a write/action). The gate is a no-op
        // when management.auth.enabled is off (ScopeRequirementHandler passes through).
        var v1 = app.MapGroup("/api/v1").RequireAuthorization(PdnAuthPolicies.Operate);

        // Connect out to a callsign (AX.25 dial) or NET/ROM alias (network route). Capture
        // the connector inside the gate; dial OUTSIDE it (bounded by DialTimeout + the
        // request token). Returns the new session's SessionInfo on success.
        v1.MapPost("/sessions", async (ConnectRequest body, HttpContext ctx, NodeHostedService host, SysopConsoleManager console, IAuditLog audit, TimeProvider clock, CancellationToken ct) =>
        {
            if (body is null || string.IsNullOrWhiteSpace(body.Target))
            {
                return Results.BadRequest(new { error = "A 'target' callsign or NET/ROM alias is required." });
            }
            if (!Callsign.TryParse(body.Target.Trim(), out var target))
            {
                return Results.BadRequest(new { error = $"'{body.Target}' is not a valid callsign or NET/ROM alias." });
            }

            // Audit the connect attempt: a dial TRANSMITS (SABM / NET/ROM circuit setup), so
            // the owner sees who reached out to which station, on which port, from where.
            audit.RecordRest(ctx, clock, "connect_session", target.ToString(), "requested",
                string.IsNullOrWhiteSpace(body.PortId) ? "port=default" : $"port={body.PortId}");

            // Capture a connector under the gate (a short critical section — no dial here).
            // The supervisor's resolver encodes callsign→AX.25-dial / alias→NET/ROM-route
            // AND claims the dialled remote so its SessionAccepted handler doesn't start a
            // node console against the station we're dialling.
            var (connector, portUnknown) = await host.RunExclusiveAsync(() =>
            {
                if (host.Supervisor is null)
                {
                    return Task.FromResult<(IOutboundConnector?, bool)>((null, false));
                }
                // If a portId was named, it must be a running port (honoured as validation;
                // see the type remarks for the v1 default-connector dial scope).
                if (!string.IsNullOrWhiteSpace(body.PortId) && host.Supervisor.GetPort(body.PortId!) is null)
                {
                    return Task.FromResult<(IOutboundConnector?, bool)>((null, true));
                }
                return Task.FromResult((host.Supervisor.ResolveDefaultConnector(), false));
            }, ct).ConfigureAwait(false);

            if (portUnknown)
            {
                return Results.NotFound(new { error = $"Port '{body.PortId}' is not running." });
            }
            if (connector is null)
            {
                return Results.NotFound(new { error = "No running port to connect out on." });
            }

            // Dial OUTSIDE the gate — awaiting SABM/UA (or a NET/ROM circuit) is the
            // long-running part and must not block config reconciles. The ceiling timer
            // rides the injected TimeProvider (repo rule §2.7: no wall-clock), linked with
            // the request token.
            using var ceiling = new CancellationTokenSource(DialTimeout, clock);
            using var dialCts = CancellationTokenSource.CreateLinkedTokenSource(ct, ceiling.Token);
            INodeConnection connection;
            try
            {
                connection = await connector.ConnectAsync(target, dialCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                return Results.Json(
                    new { error = $"Connect to {target} timed out after {DialTimeout.TotalSeconds:F0}s." },
                    statusCode: StatusCodes.Status504GatewayTimeout);
            }
            catch (TimeoutException ex)
            {
                return Results.Json(
                    new { error = $"Connect to {target} timed out: {ex.Message}" },
                    statusCode: StatusCodes.Status504GatewayTimeout);
            }
            catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
            {
                // No route / refused (DM) / no local port to dial it on.
                return Results.Json(
                    new { error = $"Connect to {target} failed: {ex.Message}" },
                    statusCode: StatusCodes.Status502BadGateway);
            }

            var info = await host.RunExclusiveAsync(
                () => Task.FromResult(ProjectConnected(host, connector.PortId, connection, target, clock)), ct)
                .ConfigureAwait(false);

            // Adopt the connection into the sysop console manager keyed by the SessionInfo's
            // id (the "{portId}:{peer}" the projection minted) so the stream/send/disconnect
            // endpoints address it by that exact id. The manager starts a read pump that
            // captures the peer's output (banner/prompt/responses) into a backlog and fans it
            // out to SSE subscribers — without this the dialled connection's output would
            // buffer unread (the v1 "blind connect-out"). The manager owns the connection's
            // lifetime from here (its CloseAsync / peer-gone path disposes it, posting DISC).
            console.Open(info.Id, connection);
            return Results.Ok(info);
        });

        // Disconnect a session: find it by {id} (portId:peer). If the manager owns it (an
        // adopted connect-out), close it through the manager — that stops the read pump,
        // disposes the connection (posts DISC), and completes any open SSE subscribers. Else
        // fall back to posting DL-DISCONNECT on a live AX.25 session under the gate. Absent in
        // both → 404, else 204.
        v1.MapDelete("/sessions/{id}", async (string id, HttpContext ctx, NodeHostedService host, SysopConsoleManager console, IAuditLog audit, TimeProvider clock, CancellationToken ct) =>
        {
            // A disconnect transmits DISC/DL-DISCONNECT — audit the teardown request.
            audit.RecordRest(ctx, clock, "disconnect_session", id, "requested", "");

            if (console.IsManaged(id))
            {
                await console.CloseAsync(id).ConfigureAwait(false);
                return Results.NoContent();
            }

            var found = await host.RunExclusiveAsync(() =>
            {
                var match = FindSession(host, id);
                match?.Session.PostEvent(new DlDisconnectRequest());
                return Task.FromResult(match is not null);
            }, ct).ConfigureAwait(false);

            return found ? Results.NoContent() : Results.NotFound();
        });

        // Send one text line into a connected-mode session. The line is UTF-8 with a
        // trailing CR (the node's console line discipline — CR, not CRLF). If the manager
        // owns the session (an adopted connect-out), type the line into the peer through the
        // manager's write path; else fall back to the live AX.25 session's SendData under the
        // gate. Absent in both → 404, else 202 (queued).
        v1.MapPost("/sessions/{id}/send", async (string id, SendRequest body, HttpContext ctx, NodeHostedService host, SysopConsoleManager console, IAuditLog audit, TimeProvider clock, CancellationToken ct) =>
        {
            if (body is null || body.Line is null)
            {
                return Results.BadRequest(new { error = "A 'line' is required." });
            }

            // Audit the send: this transmits I-frames on the link. Log the length, never the
            // line content (it may carry message text — payloads are summarised, not logged).
            audit.RecordRest(ctx, clock, "send_session", id, "requested", $"len={body.Line.Length}");

            // CR-terminated, UTF-8 — matches the telnet console's CR (not CRLF) relay
            // discipline onto the AX.25 link.
            var bytes = Encoding.UTF8.GetBytes(body.Line + "\r");

            if (console.IsManaged(id))
            {
                await console.WriteAsync(id, bytes, ct).ConfigureAwait(false);
                return Results.Accepted();
            }

            var sent = await host.RunExclusiveAsync(() =>
            {
                if (FindSession(host, id) is not { } match)
                {
                    return Task.FromResult(false);
                }
                match.Listener.SendData(match.Session, bytes, Ax25Frame.PidNoLayer3);
                return Task.FromResult(true);
            }, ct).ConfigureAwait(false);

            return sent ? Results.Accepted() : Results.NotFound();
        });

        // Per-session interactive output stream (Server-Sent Events). The browser's console
        // drawer opens this to watch an adopted connect-out's output live. The contract is
        // fixed (the frontend builds to it): each output chunk is one `output` SSE event
        // whose `data:` is the chunk JSON-encoded as a string — JSON-encoding is REQUIRED so
        // embedded CR/LF survive SSE's line framing (a raw \n in data: would break the event).
        // On subscribe we replay the backlog first (one `output` event), then stream live
        // chunks; a `: ping` heartbeat keeps the stream warm. If the id is not managed → 404
        // (checked BEFORE writing any bytes, since once the response body has started we can
        // no longer return a result).
        v1.MapGet("/sessions/{id}/stream", async (string id, HttpContext ctx, SysopConsoleManager console, TimeProvider clock) =>
        {
            var ct = ctx.RequestAborted;

            // Subscribe (and thus 404-check) BEFORE writing any bytes: a null subscription
            // means the id isn't managed, and we can still set a 404 status here.
            using var sub = console.Subscribe(id, out var backlog, out var reader);
            if (sub is null || reader is null)
            {
                ctx.Response.StatusCode = StatusCodes.Status404NotFound;
                return;
            }

            // SSE wire envelope — un-buffered end to end so a chunk reaches the browser the
            // instant the pump broadcasts it, not on a proxy flush.
            ctx.Response.Headers.ContentType = "text/event-stream";
            ctx.Response.Headers.CacheControl = "no-cache";
            ctx.Response.Headers["X-Accel-Buffering"] = "no";
            ctx.Features.Get<IHttpResponseBodyFeature>()?.DisableBuffering();

            // Replay the backlog first (the banner/prompt the browser missed) as one `output`
            // event, then stream live chunks. Even an empty backlog is sent so the client's
            // onopen-driven render has a deterministic first event and the headers flush.
            await WriteOutputAsync(ctx, backlog, ct).ConfigureAwait(false);

            try
            {
                while (!ct.IsCancellationRequested)
                {
                    // Race a chunk becoming readable against the heartbeat tick (injected
                    // TimeProvider — repo rule §2.7, no wall-clock), staying responsive to both.
                    var waitRead = reader.WaitToReadAsync(ct).AsTask();
                    var heartbeat = Task.Delay(HeartbeatInterval, clock, ct);
                    var done = await Task.WhenAny(waitRead, heartbeat).ConfigureAwait(false);

                    if (done == heartbeat)
                    {
                        await WriteRawAsync(ctx, ": ping\n\n", ct).ConfigureAwait(false);
                        continue;
                    }

                    if (!await waitRead.ConfigureAwait(false))
                    {
                        // The manager completed the channel — the peer went away or the
                        // session was closed. Nothing more will arrive; end the response.
                        break;
                    }

                    while (reader.TryRead(out var chunk))
                    {
                        await WriteOutputAsync(ctx, chunk, ct).ConfigureAwait(false);
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // The client went away (RequestAborted). Normal SSE teardown — the
                // using-scoped subscription unsubscribes + completes its channel.
            }
        });

        // Connectionless TEST ping ("axping"): validate the station + port, capture the
        // port's listener, and run AxPinger over it. The pinger only reads frames + sends
        // TEST commands, so it runs OUTSIDE the host gate (no port-set mutation); the
        // listener reference is captured once, defensively. An all-timeout result (a peer
        // that doesn't answer TEST) is a normal 200 with loss 100%, not an error.
        v1.MapPost("/ping", async (PingRequest body, HttpContext ctx, NodeHostedService host, IAuditLog audit, TimeProvider clock, CancellationToken ct) =>
        {
            if (body is null || string.IsNullOrWhiteSpace(body.Station))
            {
                return Results.BadRequest(new { error = "A 'station' callsign is required." });
            }
            if (!Callsign.TryParse(body.Station.Trim(), out var target))
            {
                return Results.BadRequest(new { error = $"'{body.Station}' is not a valid callsign." });
            }
            if (string.IsNullOrWhiteSpace(body.PortId))
            {
                return Results.BadRequest(new { error = "A 'portId' is required." });
            }

            // A ping transmits TEST command frames — audit who pinged which station on which port.
            audit.RecordRest(ctx, clock, "ping_station", target.ToString(), "requested", $"port={body.PortId}");

            var listener = host.Supervisor?.GetPort(body.PortId!)?.Listener;
            if (listener is null)
            {
                return Results.NotFound(new { error = $"Port '{body.PortId}' is not running." });
            }

            // Clamp the probe count to a safe range (default 5; the contract default).
            int count = Math.Clamp(body.Count, MinPingCount, MaxPingCount);

            var channel = new ListenerAxPingChannel(listener);
            var result = await AxPinger.RunAsync(channel, target, count, PerPingTimeout, clock, ct)
                .ConfigureAwait(false);
            return Results.Ok(result);
        });
    }

    /// <summary>The connect-out request body: a callsign or NET/ROM alias, optionally a port.</summary>
    public sealed record ConnectRequest(string Target, string? PortId = null);

    /// <summary>The send-line request body: one line of text (CR-terminated on the wire).</summary>
    public sealed record SendRequest(string Line);

    /// <summary>The ping request body: the station to TEST-ping, the port to send on, and the
    /// probe count (default 5, clamped to <c>1..20</c>).</summary>
    public sealed record PingRequest(string Station, string PortId, int Count = 5);

    /// <summary>A live session matched from a <c>{portId}:{peer}</c> id, with its owning listener.</summary>
    private readonly record struct SessionMatch(string PortId, Ax25Listener Listener, Ax25Session Session);

    /// <summary>
    /// Split a session id at the FIRST ':' into (portId, peer) — the convention
    /// <c>PdnReadApi.BuildSessions</c> mints (<c>$"{portId}:{peer}"</c>). The peer (a
    /// callsign with an SSID, e.g. <c>M0LTE-1</c>) itself contains no ':', so a single
    /// split on the first ':' is unambiguous. Returns false if there is no ':' .
    /// </summary>
    internal static bool TrySplitSessionId(string id, out string portId, out string peer)
    {
        portId = string.Empty;
        peer = string.Empty;
        if (string.IsNullOrEmpty(id))
        {
            return false;
        }
        int colon = id.IndexOf(':', StringComparison.Ordinal);
        if (colon <= 0 || colon >= id.Length - 1)
        {
            return false;
        }
        portId = id[..colon];
        peer = id[(colon + 1)..];
        return true;
    }

    // Resolve a {portId:peer} id to the live session on that port (matched on the peer's
    // canonical text), or null if the port isn't running / the peer has no live session /
    // the id is malformed. Caller holds the gate.
    private static SessionMatch? FindSession(NodeHostedService host, string id)
    {
        if (!TrySplitSessionId(id, out var portId, out var peer))
        {
            return null;
        }
        var listener = host.Supervisor?.GetPort(portId)?.Listener;
        var session = listener?.ActiveSessions.FirstOrDefault(s => s.Context.Remote.ToString() == peer);
        return listener is not null && session is not null
            ? new SessionMatch(portId, listener, session)
            : null;
    }

    // Project the SessionInfo for a freshly-opened connect-out. Prefer the AX.25 session
    // the connection wraps (Ax25NodeConnection.Session) so V(S)/V(R)/state are exact; for a
    // NET/ROM circuit (no AX.25 session) fall back to the live ActiveSessions on the port,
    // then to a minimal Connected projection from the connection's peer id. Caller holds
    // the gate.
    private static SessionInfo ProjectConnected(
        NodeHostedService host, string portId, INodeConnection connection, Callsign target, TimeProvider clock)
    {
        var neighbours = PdnReadApi.NeighbourCallsigns(host);
        var now = clock.GetUtcNow();

        if (connection is Ax25NodeConnection ax25)
        {
            return PdnReadApi.ProjectSession(host, portId, ax25.Session, neighbours, now);
        }

        // NET/ROM (or other) connection: try to find a matching live AX.25 session on the
        // port; else project a minimal Connected row from the connection's peer.
        var peer = connection.PeerId;
        var session = host.Supervisor?.GetPort(portId)?.Listener.ActiveSessions
            .FirstOrDefault(s => s.Context.Remote.ToString() == peer);
        if (session is not null)
        {
            return PdnReadApi.ProjectSession(host, portId, session, neighbours, now);
        }

        var who = string.IsNullOrEmpty(peer) ? target.ToString() : peer;
        return new SessionInfo(
            Id: $"{portId}:{who}",
            PortId: portId,
            Peer: who,
            Role: neighbours.Contains(who) ? "interlink" : "console",
            State: "Connected",
            Vs: 0,
            Vr: 0,
            Window: 0,
            UptimeSeconds: 0,
            BytesIn: 0,
            BytesOut: 0,
            LastActivity: "0:00:00");
    }

    // Emit one `output` SSE event carrying a text chunk. The chunk is JSON-encoded as a
    // string (JsonSerializer.Serialize) so embedded CR/LF — which a packet banner/prompt is
    // full of — survive SSE's line framing: a raw \n in a data: line would terminate the
    // event early. A plain string serializes identically under any options, so the default
    // serializer is used.
    private static Task WriteOutputAsync(HttpContext ctx, string chunk, CancellationToken ct)
    {
        var json = JsonSerializer.Serialize(chunk);
        return WriteRawAsync(ctx, $"event: output\ndata: {json}\n\n", ct);
    }

    // Write a UTF-8 SSE chunk and flush it immediately. A mid-write cancellation or
    // IOException means the client vanished while we were writing — a normal disconnect, not
    // a server fault, so it's swallowed rather than bubbling up as a 500.
    private static async Task WriteRawAsync(HttpContext ctx, string s, CancellationToken ct)
    {
        try
        {
            await ctx.Response.WriteAsync(s, ct).ConfigureAwait(false);
            await ctx.Response.Body.FlushAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Client disconnected mid-write — expected.
        }
        catch (IOException)
        {
            // Broken pipe to a vanished client — expected.
        }
    }
}
