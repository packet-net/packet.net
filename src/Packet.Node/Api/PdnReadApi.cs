using System.Diagnostics;
using System.Reflection;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Packet.NetRom.Routing;
using Packet.Node.Core.Api;
using Packet.Node.Core.Capabilities;
using Packet.Node.Core.Configuration;
using Packet.Node.Core.Hosting;
using Packet.Node.Core.NetRom;
using Packet.Node.Core.Traffic;

namespace Packet.Node.Api;

/// <summary>
/// The read side of the pdn node control API (Slice 3, step 1). Maps the
/// unauthenticated <c>GET /api/v1/*</c> endpoints the web monitor consumes,
/// projecting each from the live node state — the <see cref="NodeHostedService"/>
/// singleton's <see cref="NodeHostedService.Supervisor"/> / <see cref="NodeHostedService.NetRom"/>
/// handles, the <see cref="IConfigProvider"/>, and the injected <see cref="TimeProvider"/>.
/// </summary>
/// <remarks>
/// <para>
/// Both live handles can be <c>null</c> while the host is still starting (the
/// hosted service creates them inside <c>ExecuteAsync</c>); every handler treats
/// that as an empty/default node rather than throwing, so a request that races the
/// boot returns a sensible 200 instead of a 500.
/// </para>
/// <para>
/// Auth is a later step — these are read-only and the node binds 127.0.0.1 by
/// default. As of step 1b the frame/byte/REJ/SREJ counters that back the port,
/// link, and session projections are live (read from <see cref="NodeHostedService.Telemetry"/>);
/// the live SSE feed lives next door in <see cref="PdnEventsApi"/>. The per-port state,
/// last-error and degraded components come from the supervisor's port state model through
/// <see cref="PortStatusProjector"/> (packet-net/packet.net#722), which every other port
/// surface projects from too.
/// </para>
/// </remarks>
public static class PdnReadApi
{
    // Node start instant on a monotonic source (Stopwatch ticks). Captured once at
    // module load so UptimeSeconds is wall-clock-independent (repo rule §2.7: no
    // DateTime.Now in logic — TimeProvider / Stopwatch only). Stopwatch.GetTimestamp
    // is the process-relative monotonic clock; close enough to "node start" for an
    // uptime read (the API maps as the host comes up).
    private static readonly long StartTimestamp = Stopwatch.GetTimestamp();

    // The assembly informational version (e.g. "0.1.0+<sha>"), trimmed of any build
    // metadata, or "dev" when unattributed. Computed once.
    private static readonly string NodeVersion = ResolveVersion();

    /// <summary>
    /// Map the read-side control endpoints under <c>/api/v1</c>. Called from the
    /// node composition root before the SPA fallback.
    /// </summary>
    public static void MapPdnReadApi(this WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);

        // Touch StartTimestamp here (this runs at app startup, before any request)
        // so node uptime is measured from startup, not from the first /status call.
        _ = StartTimestamp;

        // The read endpoints are gated `read` (admin ⊃ operate ⊃ read). The gate is a
        // no-op when management.auth.enabled is off (ScopeRequirementHandler passes
        // through), so the unauthenticated behaviour is unchanged by default.
        var v1 = app.MapGroup("/api/v1").RequireAuthorization(PdnAuthPolicies.Read);

        // The traffic-log pieces resolve [FromServices] + nullable because they are
        // only registered when traffic.enabled (same pattern as JwtTokenService —
        // without [FromServices] an unregistered complex type fails endpoint
        // inference at startup instead of degrading).
        v1.MapGet("/status", (NodeHostedService host, IConfigProvider config, TimeProvider clock,
                [FromServices] TrafficLogService? traffic)
            => Results.Ok(BuildStatus(host, config, clock, traffic)));

        v1.MapGet("/ports", (NodeHostedService host, IConfigProvider config)
            => Results.Ok(BuildPorts(host, config)));

        v1.MapGet("/sessions", (NodeHostedService host, IConfigProvider config, TimeProvider clock)
            => Results.Ok(BuildSessions(host, clock)));

        v1.MapGet("/netrom/routes", (NodeHostedService host, TimeProvider clock)
            => Results.Ok(BuildNetRomRoutes(host, clock)));

        // The ETag is the config document's version token: echo it back as If-Match on a
        // PUT /config (or any port edit) to get compare-and-swap instead of last-writer-wins
        // (review item C065, #694 - see ConfigCas). Read-scoped, so the secret-bearing fields
        // (tailscale.authKey, mqtt.password, the PKCS#12 password) are masked to "***": a
        // reader learns whether one is set, never its value; a PUT that echoes the placeholder
        // back keeps the stored secret (see ConfigRedaction, C010).
        v1.MapGet("/config", (HttpContext ctx, IConfigProvider config, IWritableConfigProvider writable) =>
        {
            ConfigCas.SetETag(ctx, writable);
            return Results.Json(ConfigRedaction.Redact(config.Current));
        });

        // Per-link rollup (frame/byte/REJ/SREJ counters) from the telemetry tap.
        v1.MapGet("/links", (NodeHostedService host) => Results.Ok(BuildLinks(host)));

        // The persisted MHeard log (#454): recently heard stations, the REST companion to the MH
        // console verb. No `port` query ⇒ the node-wide view (each callsign merged across ports);
        // `?port=<id>` ⇒ that port's stations. Most-recently-heard first. Empty when the heard log
        // is absent (default-off host) or nothing has been heard yet — never a throw.
        // `?txdelayThresholdMs=` overrides the excess-TXDELAY advisory threshold (default 250 ms).
        v1.MapGet("/heard", (NodeHostedService host, TimeProvider clock, string? port, double? txdelayThresholdMs)
            => Results.Ok(BuildHeard(host, clock, port, txdelayThresholdMs)));

        // The learned per-peer AX.25 capability cache (which neighbours speak v2.2 /
        // answer a pre-connect XID). Projects host.Capabilities.All() with the two instants
        // rendered relative-ago, same model as /links + the NetRom rows. Empty when the cache
        // is absent (default-off host) or still booting.
        v1.MapGet("/capabilities", (NodeHostedService host, TimeProvider clock)
            => Results.Ok(BuildCapabilities(host, clock)));

        // Recent frames (oldest → newest) from the telemetry ring, so the web monitor
        // bootstraps with history instead of an empty table: the client seeds with this,
        // then live-streams /events and dedupes the overlap by seq. Bounded to the ring.
        v1.MapGet("/monitor/recent", (NodeHostedService host, int? limit)
            => Results.Ok(host.Telemetry.RecentFrames(Math.Clamp(limit ?? 250, 1, 250))));

        // The persisted traffic log (the separate traffic.db): recent frames
        // newest-first, filterable by port + UTC time range. Returns empty when
        // traffic logging is disabled (the store is then unregistered); /status's
        // traffic.enabled flag says which. Bad since/until values 400 via binding.
        v1.MapGet("/traffic", (string? port, DateTimeOffset? since, DateTimeOffset? until, int? limit,
                [FromServices] SqliteTrafficStore? store)
            => Results.Ok(store is null
                ? Array.Empty<TrafficFrame>()
                : store.Query(port, since, until, Math.Clamp(limit ?? 250, 1, 1000))));

        // The node's own recent log lines, newest first, from the bounded in-memory ring the
        // NodeLogRingProvider fills (this process since start; not journald - see NodeLogRing).
        // `?limit=` clamps to the ring. This was an empty stub until #694 (review item C008).
        v1.MapGet("/log", (NodeLogRing ring, int? limit)
            => Results.Ok(ring.Recent(limit ?? NodeLogRing.DefaultTail)));
    }

    internal static NodeStatus BuildStatus(
        NodeHostedService host, IConfigProvider config, TimeProvider clock, TrafficLogService? traffic)
    {
        var current = config.Current;
        var supervisor = host.Supervisor;
        var running = RunningPorts(supervisor);

        // Serving ports: up OR degraded (a port running without its radio still carries
        // traffic). The supervisor's state model is the authority (#722).
        int portsUp = supervisor?.Snapshot().Count(p => p.IsServing) ?? 0;
        // Live sessions only: ActiveSessions is the listener's LRU peer CACHE, which keeps a
        // Disconnected session object until eviction, so summing it made "Active sessions" climb
        // forever (review item C052, #694). SessionLiveness is the shared predicate: everything
        // except Disconnected counts, so a link mid-handshake or mid-release is still visible
        // (#727 item 8).
        int sessionCount = running.Sum(p => p.Listener.ActiveSessions.Count(SessionLiveness.IsLive));

        var snapshot = SnapshotOf(host);
        var netrom = new NetRomSummary(
            Neighbours: snapshot.Neighbours.Count,
            Destinations: snapshot.Destinations.Count,
            Inp3Enabled: current.NetRom.Inp3.Enabled);

        return new NodeStatus(
            Callsign: current.Identity.Callsign,
            Alias: current.Identity.Alias,
            Grid: current.Identity.Grid,
            Version: NodeVersion,
            UptimeSeconds: (long)UptimeSeconds(clock),
            PortsUp: portsUp,
            PortsTotal: current.Ports.Count,
            SessionCount: sessionCount,
            Netrom: netrom,
            Traffic: new TrafficLogStatus(
                Enabled: traffic is not null,
                Dropped: traffic?.DroppedFrames ?? 0));
    }

    // One derivation, shared with GET /ports/{id}, the metrics endpoint, the MCP backend and
    // (through PortHealth) the console PORTS verb - see PortStatusProjector for what the four
    // divergent copies used to get wrong (#722).
    internal static PortStatus[] BuildPorts(NodeHostedService host, IConfigProvider config)
        => PortStatusProjector.ProjectAll(host.Supervisor, config.Current.Ports, host.Telemetry);

    internal static SessionInfo[] BuildSessions(NodeHostedService host, TimeProvider clock)
    {
        var supervisor = host.Supervisor;
        var running = RunningPorts(supervisor);
        var now = clock.GetUtcNow();

        // Callsigns of directly-heard NET/ROM neighbours — a peer in this set on an
        // active session is treated as an interlink (NET/ROM datagrams), else a
        // console user. Best-effort classification (the role isn't tracked on the
        // session itself yet).
        var neighbours = NeighbourCallsigns(host);

        var sessions = new List<SessionInfo>();
        foreach (var port in running)
        {
            foreach (var session in port.Listener.ActiveSessions)
            {
                // The listener's ActiveSessions is a peer CACHE that keeps Disconnected entries
                // until LRU eviction; only the cached dead peer is filtered out, so a
                // DELETE /sessions/{id} that really did tear the link down stops showing a row
                // (review item C052, #694) while a session still handshaking or releasing stays
                // listed and stays actionable (#727 item 8). The engine keeps its cache untouched.
                if (!SessionLiveness.IsLive(session))
                {
                    continue;
                }
                sessions.Add(ProjectSession(host, port.Id, session, neighbours, now));
            }
        }
        return sessions.ToArray();
    }

    /// <summary>
    /// Project a single live <see cref="Ax25Session"/> on a given port to the
    /// <see cref="SessionInfo"/> the <c>/sessions</c> family serves — the per-session
    /// half of <see cref="BuildSessions"/>, factored out so the actions API
    /// (<c>PdnSessionsApi</c>) projects a freshly-opened session through the exact same
    /// shape (id convention, role classification, telemetry-backed byte/uptime fields).
    /// </summary>
    /// <param name="neighbours">The directly-heard NET/ROM neighbour callsigns — a peer
    /// in this set is classified <c>interlink</c>, else <c>console</c>. Pass the result of
    /// <c>host.NetRom?.Snapshot()</c>'s neighbour set, or empty to default everything to
    /// <c>console</c>.</param>
    /// <param name="now">The clock's current instant, for the relative uptime / last-activity.</param>
    internal static SessionInfo ProjectSession(
        NodeHostedService host, string portId, Packet.Ax25.Session.Ax25Session session,
        IReadOnlySet<string> neighbours, DateTimeOffset now)
    {
        var ctx = session.Context;
        var peer = ctx.Remote.ToString();
        var role = neighbours.Contains(peer) ? "interlink" : "console";
        // The LOCAL half of the engine's session key. A circuit to an application callsign is a
        // different circuit from one to the node's own call, even from the same station on the
        // same port - so it gets its own id (#723 item 5).
        var local = ctx.Local.ToString();
        var nodeCall = NodeCallOn(host, portId);

        // The (port, peer) telemetry LINK backs the byte/uptime/last-activity fields, and a
        // link outlives any one session: it is keyed on the peer callsign, counts every frame
        // heard from or sent to that peer (UI/beacons included, not just this circuit's
        // I-frames), stamps FirstSeen once, and is only cleared when the port detaches or the
        // node restarts. So on a reconnect these three read as "this peer on this port since
        // first heard", NOT "since this circuit came up" (review item C063, #694). That is the
        // documented contract of the fields (see SessionInfo in ReadModels.cs and the
        // node-api.md description) rather than a per-session counter, because there is no
        // per-session byte tally on the engine's Ax25Session to read one from. A session with
        // no link snapshot at all keeps the zero / not-known placeholders rather than fabricating.
        var link = host.Telemetry.Link(portId, peer);
        long bytesIn = link?.BytesIn ?? 0;
        long bytesOut = link?.BytesOut ?? 0;
        long uptime = link is { FirstSeen: var seen } && seen != DateTimeOffset.MinValue
            ? (long)Math.Max(0, (now - seen).TotalSeconds)
            : 0;
        string lastActivity = link is null ? "-" : RelativeAgo(now, link.LastActivity);

        return new SessionInfo(
            Id: SessionIds.Format(portId, peer, local, nodeCall),
            PortId: portId,
            Peer: peer,
            Local: local,
            Role: role,
            State: session.CurrentState,
            Vs: ctx.VS,
            Vr: ctx.VR,
            Window: ctx.K,
            UptimeSeconds: uptime,
            BytesIn: bytesIn,
            BytesOut: bytesOut,
            LastActivity: lastActivity);
    }

    /// <summary>
    /// The callsign the port's listener answers as (its <c>MyCall</c>) - the "is this the node's
    /// own identity" side of the session-id convention. Falls back to the configured node
    /// identity when the port is not serving (a projection racing a teardown), and null when
    /// neither is available, which <see cref="SessionIds.Format"/> reads as "assume node call".
    /// </summary>
    internal static string? NodeCallOn(NodeHostedService host, string portId)
    {
        var myCall = host.Supervisor?.GetPort(portId)?.Listener.MyCall;
        return myCall?.ToString() ?? host.Config.Current.Identity.Callsign;
    }

    /// <summary>
    /// The directly-heard NET/ROM neighbour callsigns from the live routing snapshot,
    /// for session role classification (a peer that is a neighbour rides interlink
    /// datagrams, else it's a console user). Empty when NET/ROM is off or still booting.
    /// </summary>
    internal static HashSet<string> NeighbourCallsigns(NodeHostedService host)
        => SnapshotOf(host).Neighbours
            .Select(n => n.Neighbour.ToString())
            .ToHashSet(StringComparer.Ordinal);

    internal static LinkStats[] BuildLinks(NodeHostedService host)
    {
        // Frame/byte counts and REJ/SREJ tallies come from the frame tap. SmoothedRttMs
        // and Retries can't come from the tap — they live in the connected-mode session's
        // T1/SRTT timer state — so they're read from the live session for the (port, peer)
        // when one exists (the monitor-v2 seam, #173); a link with no live session (UI-only
        // traffic, or a session that has since closed) keeps 0, which is honest.
        return host.Telemetry.Links().Select(link =>
        {
            var (rttMs, retries) = SessionTimers(host.Supervisor, link.PortId, link.Peer);
            return new LinkStats(
                PortId: link.PortId,
                Peer: link.Peer,
                SmoothedRttMs: rttMs,
                Retries: retries,
                RejCount: link.RejCount,
                SrejCount: link.SrejCount,
                FramesIn: link.FramesIn,
                FramesOut: link.FramesOut);
        }).ToArray();
    }

    /// <summary>
    /// The live connected-mode timer state for a link — the smoothed round-trip time
    /// (SRT, ms) and the current retry count (RC) — read from the matching live
    /// <see cref="Packet.Ax25.Session.Ax25Session"/> on the port. Returns <c>(0, 0)</c>
    /// when no connected session exists for <paramref name="peer"/> (e.g. UI-only
    /// traffic, or after the session closed). This is the monitor-v2 source feeding both
    /// the REST <c>/links</c> projection and the MCP <c>link_quality</c> tool (#173).
    /// </summary>
    internal static (int RttMs, int Retries) SessionTimers(PortSupervisor? supervisor, string portId, string peer)
    {
        var session = supervisor?.GetPort(portId)?.Listener.ActiveSessions
            .FirstOrDefault(s => s.Context.Remote.ToString() == peer);
        if (session is null)
        {
            return (0, 0);
        }
        var ctx = session.Context;
        int rtt = (int)Math.Clamp(ctx.Srt.TotalMilliseconds, 0, int.MaxValue);
        return (rtt, ctx.RC);
    }

    internal static HeardStation[] BuildHeard(
        NodeHostedService host, TimeProvider clock, string? port, double? txdelayThresholdMs = null)
    {
        // The log handle is null on a host built without one (older tests / an embedder that didn't
        // register the singleton) — an empty array is the honest read, never a throw.
        var log = host.Heard;
        if (log is null)
        {
            return [];
        }
        var now = clock.GetUtcNow();

        // The passive excess-TXDELAY advisory (docs/research/txdelay-optimisation.md): peers whose
        // rolling median pre-data carrier exceeds the threshold get a one-line flag on their row —
        // the "link health" read the tuning view lists. Threshold overridable per request.
        var advisorOptions = txdelayThresholdMs is { } t and > 0
            ? new Packet.Tune.Core.ExcessTxDelayAdvisorOptions { ThresholdMs = t }
            : null;
        string? Advisory(string callsign, float? medianMs, int samples) =>
            Packet.Tune.Core.ExcessTxDelayAdvisor.Assess(callsign, medianMs, samples, advisorOptions)?.Message;

        if (string.IsNullOrWhiteSpace(port))
        {
            // Node-wide: one row per callsign, merged across the ports it was heard on.
            return log.NodeWide().Select(s => new HeardStation(
                Callsign: s.Callsign,
                PortId: null,
                FirstHeard: RelativeAgo(now, s.FirstHeard),
                LastHeard: RelativeAgo(now, s.LastHeard),
                Count: s.Count,
                Ports: s.Ports,
                LastRssiDbm: s.LastRssiDbm,
                LastSnrDb: s.LastSnrDb,
                MedianPreDataCarrierMs: s.MedianPreDataCarrierMs,
                PreDataCarrierSamples: s.PreDataCarrierSamples,
                TxDelayAdvisory: Advisory(s.Callsign, s.MedianPreDataCarrierMs, s.PreDataCarrierSamples))).ToArray();
        }

        // Per-port: one row per callsign heard on that port.
        return log.ForPort(port).Select(e => new HeardStation(
            Callsign: e.Callsign,
            PortId: e.PortId,
            FirstHeard: RelativeAgo(now, e.FirstHeard),
            LastHeard: RelativeAgo(now, e.LastHeard),
            Count: e.Count,
            Ports: 1,
            LastRssiDbm: e.LastRssiDbm,
            LastSnrDb: e.LastSnrDb,
            MedianPreDataCarrierMs: e.MedianPreDataCarrierMs,
            PreDataCarrierSamples: e.PreDataCarrierSamples,
            TxDelayAdvisory: Advisory(e.Callsign, e.MedianPreDataCarrierMs, e.PreDataCarrierSamples))).ToArray();
    }

    internal static PeerCapability[] BuildCapabilities(NodeHostedService host, TimeProvider clock)
    {
        // The cache handle is null on a host built without one (older tests / an embedder that
        // didn't register the singleton) and the records are absent until a dial has learned
        // something — either way an empty array is the honest read, never a throw.
        var cache = host.Capabilities;
        if (cache is null)
        {
            return [];
        }
        var now = clock.GetUtcNow();
        return cache.All().Select(r => new PeerCapability(
            PortId: r.PortId,
            Peer: r.Peer,
            SupportsExtended: r.SupportsExtended,
            SupportsSrejViaXid: r.SupportsSrejViaXid,
            LastProbed: RelativeAgo(now, r.LastProbed),
            LastRefused: r.LastRefused is { } refused ? RelativeAgo(now, refused) : null)).ToArray();
    }

    private static object BuildNetRomRoutes(NodeHostedService host, TimeProvider clock)
    {
        var snapshot = SnapshotOf(host);
        var now = clock.GetUtcNow();

        var neighbours = snapshot.Neighbours.Select(n => new
        {
            neighbour = n.Neighbour.ToString(),
            alias = n.Alias,
            portId = n.PortId,
            pathQuality = (int)n.PathQuality,
            lastHeard = RelativeAgo(now, n.LastHeard),
        }).ToArray();

        var destinations = snapshot.Destinations.Select(d => new
        {
            destination = d.Destination.ToString(),
            alias = d.Alias,
            bestRoute = 0,   // index of the active route within `routes` (best-quality first).
            routes = d.Routes.Select(r => new
            {
                neighbour = r.Neighbour.ToString(),
                quality = (int)r.Quality,
                obsolescence = r.Obsolescence,
                inp3 = r.Inp3 is { } m
                    ? new { targetTimeMs = m.TargetTimeMs, hopCount = (int)m.HopCount }
                    : null,
            }).ToArray(),
        }).ToArray();

        return new
        {
            generatedAt = snapshot.GeneratedAt,
            neighbours,
            destinations,
        };
    }

    // The live NET/ROM routing snapshot, or an empty one when the service isn't up
    // yet (still booting) or NET/ROM is disabled.
    private static NetRomRoutingSnapshot SnapshotOf(NodeHostedService host)
        => host.NetRom?.Snapshot() ?? NetRomRoutingSnapshot.Empty;

    private static List<RunningPort> RunningPorts(PortSupervisor? supervisor)
    {
        if (supervisor is null)
        {
            return [];
        }
        var list = new List<RunningPort>();
        foreach (var id in supervisor.RunningPortIds)
        {
            if (supervisor.GetPort(id) is { } port)
            {
                list.Add(port);
            }
        }
        return list;
    }

    private static double UptimeSeconds(TimeProvider clock)
    {
        // Monotonic elapsed since module load. TimeProvider.GetTimestamp shares
        // Stopwatch's frequency, so the two timestamps are comparable.
        long elapsed = clock.GetTimestamp() - StartTimestamp;
        double seconds = elapsed / (double)Stopwatch.Frequency;
        return seconds < 0 ? 0 : seconds;
    }

    // Format a past instant as a relative "h:mm:ss" ago string (e.g. 74 s → "0:01:14").
    // A future/zero/min instant clamps to "0:00:00".
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
        int hours = (int)ago.TotalHours;
        return $"{hours}:{ago.Minutes:D2}:{ago.Seconds:D2}";
    }

    private static string ResolveVersion()
    {
        var info = typeof(PdnReadApi).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (string.IsNullOrWhiteSpace(info))
        {
            return "dev";
        }
        // Strip the "+<commit>" build-metadata suffix SourceLink appends.
        int plus = info.IndexOf('+', StringComparison.Ordinal);
        return plus >= 0 ? info[..plus] : info;
    }
}
