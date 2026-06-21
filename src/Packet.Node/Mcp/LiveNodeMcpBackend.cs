using Packet.Ax25.Session;
using Packet.Node.Core.Audit;
using Packet.Core;
using Packet.Mcp;
using Packet.Node.Api;
using Packet.Node.Core.Configuration;
using Packet.Node.Core.Hosting;

namespace Packet.Node.Mcp;

/// <summary>
/// The in-process <see cref="INodeMcpBackend"/> — reads live node state straight
/// from <see cref="NodeHostedService"/> and reuses <see cref="PdnReadApi"/>'s
/// projection helpers so the MCP tools never drift from what <c>/api/v1</c>
/// reports. This is what the in-process SSE transport serves. Write actions run
/// through the host's exclusive gate (the same serialisation the REST write
/// endpoints use), so an MCP write can never race a reconcile. See
/// <c>docs/mcp-design.md</c>.
/// </summary>
public sealed class LiveNodeMcpBackend(
    NodeHostedService host,
    IConfigProvider config,
    TimeProvider clock,
    IAuditLog audit) : INodeMcpBackend
{
    // ---- read ----

    public Task<IReadOnlyList<McpPortStatus>> ListPortsAsync(CancellationToken ct = default)
    {
        IReadOnlyList<McpPortStatus> ports = PdnReadApi.BuildPorts(host, config)
            .Select(p => new McpPortStatus(p.Id, p.Enabled, p.State, p.SessionCount, p.FramesIn, p.FramesOut))
            .ToList();
        return Task.FromResult(ports);
    }

    public Task<IReadOnlyList<McpSessionInfo>> ListSessionsAsync(CancellationToken ct = default)
    {
        IReadOnlyList<McpSessionInfo> sessions = PdnReadApi.BuildSessions(host, clock)
            .Select(s => new McpSessionInfo(
                s.Id, s.PortId, s.Peer, s.Role, s.State, s.Vs, s.Vr, s.Window,
                s.UptimeSeconds, s.BytesIn, s.BytesOut, s.LastActivity))
            .ToList();
        return Task.FromResult(sessions);
    }

    public Task<IReadOnlyList<McpMonitorFrame>> RecentFramesAsync(FrameFilter filter, CancellationToken ct = default)
    {
        int limit = Math.Clamp(filter.Limit ?? 250, 1, 250);

        // Pull the whole ring (capped), filter, then take the newest `limit`
        // (RecentFrames returns oldest→newest, so Take from the tail).
        IEnumerable<Packet.Node.Core.Api.MonitorEvent> frames = host.Telemetry.RecentFrames(250);

        if (!string.IsNullOrWhiteSpace(filter.Port))
        {
            frames = frames.Where(f => string.Equals(f.PortId, filter.Port, StringComparison.OrdinalIgnoreCase));
        }
        if (!string.IsNullOrWhiteSpace(filter.Peer))
        {
            frames = frames.Where(f =>
                string.Equals(f.Source, filter.Peer, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(f.Dest, filter.Peer, StringComparison.OrdinalIgnoreCase));
        }
        if (!string.IsNullOrWhiteSpace(filter.Kind))
        {
            frames = frames.Where(f => string.Equals(f.Type, filter.Kind, StringComparison.OrdinalIgnoreCase));
        }
        if (filter.SinceSeconds is { } secs && secs > 0)
        {
            var cutoff = clock.GetUtcNow() - TimeSpan.FromSeconds(secs);
            frames = frames.Where(f => f.Timestamp >= cutoff);
        }

        IReadOnlyList<McpMonitorFrame> result = frames
            .TakeLast(limit)
            .Select(f => new McpMonitorFrame(
                f.Seq, f.Timestamp, f.PortId, f.Direction, f.Source, f.Dest, f.Type, f.Length))
            .ToList();
        return Task.FromResult(result);
    }

    public Task<McpLinkQuality> LinkQualityAsync(string remote, string? portId = null, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(remote);

        var link = host.Telemetry.Links().FirstOrDefault(l =>
            string.Equals(l.Peer, remote, StringComparison.OrdinalIgnoreCase) &&
            (portId is null || string.Equals(l.PortId, portId, StringComparison.OrdinalIgnoreCase)));

        var result = link is null
            ? new McpLinkQuality(portId ?? "?", remote, 0, 0, 0, 0, 0, 0, Unknown: true)
            : BuildLinkQuality(link);
        return Task.FromResult(result);
    }

    private McpLinkQuality BuildLinkQuality(Packet.Node.Core.Telemetry.LinkSnapshot link)
    {
        // SmoothedRttMs + Retries are read from the live session's timer state (the
        // monitor-v2 seam, #173) — 0 when no connected-mode session backs this link.
        var (rttMs, retries) = PdnReadApi.SessionTimers(host.Supervisor, link.PortId, link.Peer);
        return new McpLinkQuality(link.PortId, link.Peer, rttMs, retries, link.RejCount,
            link.SrejCount, link.FramesIn, link.FramesOut, Unknown: false);
    }

    public Task<McpNetworkTopology> NetworkTopologyAsync(CancellationToken ct = default)
    {
        var snapshot = host.NetRom?.Snapshot() ?? NetRom.Routing.NetRomRoutingSnapshot.Empty;
        var now = clock.GetUtcNow();

        var neighbours = snapshot.Neighbours
            .Select(n => new McpNeighbour(
                n.Neighbour.ToString(),
                string.IsNullOrEmpty(n.Alias) ? null : n.Alias,
                n.PortId, n.PathQuality, RelativeAgo(now, n.LastHeard)))
            .ToList();

        var destinations = snapshot.Destinations
            .Select(d => new McpDestination(
                d.Destination.ToString(),
                string.IsNullOrEmpty(d.Alias) ? null : d.Alias,
                d.Routes.Select(r => new McpRoute(r.Neighbour.ToString(), r.Quality, r.Obsolescence)).ToList()))
            .ToList();

        return Task.FromResult(new McpNetworkTopology(snapshot.GeneratedAt, neighbours, destinations));
    }

    // ---- write (operate-gated upstream in WriteTools; audited here) ----

    public async Task<SendResult> SendUiFrameAsync(SendUiRequest req, McpCaller caller, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(req);
        Audit(caller, "send_ui_frame", req.Port, $"dest={req.Dest} len={req.Payload?.Length ?? 0}");

        if (req.Path is { Count: > 0 })
        {
            return new SendResult(false, "a digipeater path is not yet supported on send_ui_frame.");
        }
        if (!Callsign.TryParse(req.Dest, out var dest))
        {
            return new SendResult(false, $"'{req.Dest}' is not a valid callsign.");
        }
        if (host.Supervisor?.GetPort(req.Port) is not { Started: true } port)
        {
            return new SendResult(false, $"port '{req.Port}' is not up.");
        }

        var info = System.Text.Encoding.UTF8.GetBytes(req.Payload ?? string.Empty);
        await port.Listener.SendUiAsync(dest, info, req.Pid, ct).ConfigureAwait(false);
        return new SendResult(true, $"UI frame sent to {dest} on {req.Port}.");
    }

    public async Task<PortActionResult> ResetPortAsync(string portId, McpCaller caller, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(portId);
        Audit(caller, "reset_port", portId, "restart");

        if (host.Supervisor is not { } supervisor)
        {
            return new PortActionResult(false, portId, "the node is still starting.");
        }
        bool restarted = await host.RunExclusiveAsync(() => supervisor.RestartPortAsync(portId, ct), ct)
            .ConfigureAwait(false);
        return restarted
            ? new PortActionResult(true, portId, $"port '{portId}' restarted.")
            : new PortActionResult(false, portId, $"port '{portId}' is not running (bring it up first).");
    }

    public async Task<SessionResult> DisconnectSessionAsync(string sessionId, McpCaller caller, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        Audit(caller, "disconnect_session", sessionId, "DL-DISCONNECT");

        int colon = sessionId.IndexOf(':', StringComparison.Ordinal);
        if (colon <= 0 || colon == sessionId.Length - 1)
        {
            return new SessionResult(false, sessionId, "id must be in port:peer form.");
        }
        string portId = sessionId[..colon];
        string peer = sessionId[(colon + 1)..];

        bool found = await host.RunExclusiveAsync(() =>
        {
            var listener = host.Supervisor?.GetPort(portId)?.Listener;
            var session = listener?.ActiveSessions.FirstOrDefault(s => s.Context.Remote.ToString() == peer);
            session?.PostEvent(new DlDisconnectRequest());
            return Task.FromResult(session is not null);
        }, ct).ConfigureAwait(false);

        return found
            ? new SessionResult(true, sessionId, "DL-DISCONNECT posted.")
            : new SessionResult(false, sessionId, "no such session.");
    }

    public async Task<KissParamResult> SetKissParamAsync(SetKissParamRequest req, McpCaller caller, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(req);
        Audit(caller, "set_kiss_param", req.Port, $"{req.Param}={req.Value}");

        if (host.Supervisor is not { } supervisor)
        {
            return new KissParamResult(false, false, "the node is still starting.");
        }

        // Push the parameter to the live transport through the same exclusive gate the
        // reconcile / restart paths use, so a KISS-param write can't race a port
        // teardown and write to a half-disposed transport. KissParamWriter owns the
        // settable-param set + range validation and dispatches to the matching
        // ICsmaChannelParams setter, which emits the KISS command frame on the wire.
        return await host.RunExclusiveAsync(async () =>
        {
            if (supervisor.GetPort(req.Port) is not { Started: true } port)
            {
                return new KissParamResult(false, false, $"port '{req.Port}' is not up.");
            }

            var r = await Packet.Node.Core.Transports.KissParamWriter
                .ApplyAsync(port.Transport, req.Param, req.Value, ct).ConfigureAwait(false);
            return new KissParamResult(r.Accepted, r.RequiresRestart, r.Message);
        }, ct).ConfigureAwait(false);
    }

    // Persist the privileged-action invocation to the node-wide audit log (pdn.db).
    // Recorded at invocation (the security-critical "who invoked what, from where"); the
    // operate-scope gate upstream means a write that reaches here was authorized. The
    // store also emits a structured log line, so this is visible in normal logs too.
    private void Audit(McpCaller caller, string action, string target, string detail)
    {
        string source = caller.Transport == "stdio" ? "mcp:stdio" : caller.Transport;
        audit.Record(AuditEntry.New(
            clock.GetUtcNow(), caller.Actor, source, action, target, "requested", detail, caller.ClientIp));
    }

    // Format a past instant as "h:mm:ss" ago (matches the REST projections' style).
    private static string RelativeAgo(DateTimeOffset now, DateTimeOffset then)
    {
        if (then == DateTimeOffset.MinValue)
        {
            return "0:00:00";
        }
        var ago = now - then;
        if (ago < TimeSpan.Zero)
        {
            ago = TimeSpan.Zero;
        }
        return $"{(int)ago.TotalHours}:{ago.Minutes:D2}:{ago.Seconds:D2}";
    }
}
