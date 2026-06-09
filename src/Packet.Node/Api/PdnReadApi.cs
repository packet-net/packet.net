using System.Diagnostics;
using System.Reflection;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Packet.NetRom.Routing;
using Packet.Node.Core.Api;
using Packet.Node.Core.Configuration;
using Packet.Node.Core.Hosting;
using Packet.Node.Core.NetRom;

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
/// the live SSE feed lives next door in <see cref="PdnEventsApi"/>. A handful of
/// fields can't be read from the frame tap alone (per-port last-error, per-link
/// SRTT/retries, the log tail) and stay placeholders behind a named <c>// TODO</c>.
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

        var v1 = app.MapGroup("/api/v1");

        v1.MapGet("/status", (NodeHostedService host, IConfigProvider config, TimeProvider clock)
            => Results.Ok(BuildStatus(host, config, clock)));

        v1.MapGet("/ports", (NodeHostedService host, IConfigProvider config)
            => Results.Ok(BuildPorts(host, config)));

        v1.MapGet("/sessions", (NodeHostedService host, IConfigProvider config, TimeProvider clock)
            => Results.Ok(BuildSessions(host, clock)));

        v1.MapGet("/netrom/routes", (NodeHostedService host, TimeProvider clock)
            => Results.Ok(BuildNetRomRoutes(host, clock)));

        v1.MapGet("/config", (IConfigProvider config)
            => Results.Json(config.Current));

        // Per-link rollup (frame/byte/REJ/SREJ counters) from the telemetry tap.
        v1.MapGet("/links", (NodeHostedService host) => Results.Ok(BuildLinks(host)));

        // TODO step 1b: log tail comes with the SSE feed (GET /events) — empty for now.
        v1.MapGet("/log", () => Results.Ok(Array.Empty<LogLine>()));
    }

    private static NodeStatus BuildStatus(NodeHostedService host, IConfigProvider config, TimeProvider clock)
    {
        var current = config.Current;
        var supervisor = host.Supervisor;
        var running = RunningPorts(supervisor);

        int portsUp = running.Count(p => p.Started);
        int sessionCount = running.Sum(p => p.Listener.ActiveSessions.Count);

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
            Netrom: netrom);
    }

    private static PortStatus[] BuildPorts(NodeHostedService host, IConfigProvider config)
    {
        var supervisor = host.Supervisor;
        var current = config.Current;

        return current.Ports.Select(port =>
        {
            var running = supervisor?.GetPort(port.Id);
            string state;
            int sessions = 0;

            if (!port.Enabled)
            {
                // Configured but switched off — not running by design.
                state = "down";
            }
            else if (running is { Started: true })
            {
                state = "up";
                sessions = running.Listener.ActiveSessions.Count;
            }
            else
            {
                // Enabled in config but either not (yet) running, or running in a
                // not-started (faulted-bring-up) state — either way it's not serving.
                state = "faulted";
            }

            var (framesIn, framesOut) = host.Telemetry.PortFrames(port.Id);

            return new PortStatus(
                Id: port.Id,
                Enabled: port.Enabled,
                State: state,
                SessionCount: sessions,
                LastError: null,        // TODO: surface the last bring-up fault per port (later step).
                FramesIn: framesIn,     // Live per-port frame totals from the telemetry tap.
                FramesOut: framesOut);
        }).ToArray();
    }

    private static SessionInfo[] BuildSessions(NodeHostedService host, TimeProvider clock)
    {
        var supervisor = host.Supervisor;
        var running = RunningPorts(supervisor);
        var snapshot = SnapshotOf(host);
        var now = clock.GetUtcNow();

        // Callsigns of directly-heard NET/ROM neighbours — a peer in this set on an
        // active session is treated as an interlink (NET/ROM datagrams), else a
        // console user. Best-effort classification (the role isn't tracked on the
        // session itself yet).
        var neighbours = snapshot.Neighbours
            .Select(n => n.Neighbour.ToString())
            .ToHashSet(StringComparer.Ordinal);

        var sessions = new List<SessionInfo>();
        foreach (var port in running)
        {
            foreach (var session in port.Listener.ActiveSessions)
            {
                var ctx = session.Context;
                var peer = ctx.Remote.ToString();
                var role = neighbours.Contains(peer) ? "interlink" : "console";

                // The (port, peer) telemetry link backs the session's byte/uptime/
                // last-activity fields. The link is keyed on the peer callsign, so a
                // session with no traffic yet (or whose link snapshot is absent) keeps
                // the zero/"—" placeholders rather than fabricating numbers.
                var link = host.Telemetry.Link(port.Id, peer);
                long bytesIn = link?.BytesIn ?? 0;
                long bytesOut = link?.BytesOut ?? 0;
                long uptime = link is { FirstSeen: var seen } && seen != DateTimeOffset.MinValue
                    ? (long)Math.Max(0, (now - seen).TotalSeconds)
                    : 0;
                string lastActivity = link is null ? "—" : RelativeAgo(now, link.LastActivity);

                sessions.Add(new SessionInfo(
                    Id: $"{port.Id}:{peer}",
                    PortId: port.Id,
                    Peer: peer,
                    Role: role,
                    State: session.CurrentState,
                    Vs: ctx.VS,
                    Vr: ctx.VR,
                    Window: ctx.K,
                    UptimeSeconds: uptime,
                    BytesIn: bytesIn,
                    BytesOut: bytesOut,
                    LastActivity: lastActivity));
            }
        }
        return sessions.ToArray();
    }

    private static LinkStats[] BuildLinks(NodeHostedService host)
    {
        // Frame/byte counts and REJ/SREJ tallies ARE real — they come straight from
        // the frame tap. SmoothedRttMs and Retries are NOT derivable from the tap
        // alone: they live in each session's T1/SRTT timer state, which the monitor
        // doesn't observe. They stay 0 until a later step surfaces the timer state
        // (rather than fabricating a value from frame timing, which would be wrong).
        return host.Telemetry.Links().Select(link => new LinkStats(
            PortId: link.PortId,
            Peer: link.Peer,
            SmoothedRttMs: 0,   // not derivable from the frame tap — see comment above.
            Retries: 0,         // not derivable from the frame tap — see comment above.
            RejCount: link.RejCount,
            SrejCount: link.SrejCount,
            FramesIn: link.FramesIn,
            FramesOut: link.FramesOut)).ToArray();
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
