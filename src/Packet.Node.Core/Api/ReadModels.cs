namespace Packet.Node.Core.Api;

// The read-side DTOs the control API (Slice 3) projects from the live node and
// serves under /api/v1. Field names match docs/node-api.yaml + the web UI's
// src/lib/types.ts (System.Text.Json's web defaults camel-case the PascalCase
// properties, so NodeStatus.Callsign → "callsign"). These are pure value shapes
// — the projection logic lives in the API endpoint layer.

/// <summary>Node health summary — the dashboard top strip + station card.</summary>
public sealed record NodeStatus(
    string Callsign,
    string? Alias,
    string? Grid,
    string Version,
    long UptimeSeconds,
    int PortsUp,
    int PortsTotal,
    int SessionCount,
    NetRomSummary Netrom,
    TrafficLogStatus Traffic);

public sealed record NetRomSummary(int Neighbours, int Destinations, bool Inp3Enabled);

/// <summary>The persistent traffic log's health: whether it is running this boot,
/// and how many frames it has dropped (writer behind — the log's loss counter,
/// never the radio path's).</summary>
public sealed record TrafficLogStatus(bool Enabled, long Dropped);

/// <summary>Live state of one configured port.</summary>
public sealed record PortStatus(
    string Id,
    bool Enabled,
    string State,            // "up" | "down" | "faulted"
    int SessionCount,
    string? LastError,
    long FramesIn,
    long FramesOut);

/// <summary>One active connected-mode circuit.</summary>
public sealed record SessionInfo(
    string Id,
    string PortId,
    string Peer,
    string Role,             // "console" | "interlink" | "bridge"
    string State,            // Connected, TimerRecovery, …
    int Vs,
    int Vr,
    int Window,
    long UptimeSeconds,
    long BytesIn,
    long BytesOut,
    string LastActivity);

/// <summary>Per-link rollup for the monitor stat strip + sessions detail.</summary>
public sealed record LinkStats(
    string PortId,
    string Peer,
    int SmoothedRttMs,
    int Retries,
    int RejCount,
    int SrejCount,
    long FramesIn,
    long FramesOut);

/// <summary>A node log line for the dashboard tail.</summary>
public sealed record LogLine(string T, string Lvl, string Msg);

/// <summary>
/// One heard station for the MHeard surface (#454) — the REST projection of a
/// <c>HeardEntry</c> / <c>HeardStationSummary</c>. The two instants render as relative-ago
/// strings (the NetRom/Capabilities row style) so the client needs no clock of its own. For the
/// node-wide view <see cref="PortId"/> is null and <see cref="Ports"/> is the count of distinct
/// ports the station was heard on; for the per-port view <see cref="PortId"/> is the port id and
/// <see cref="Ports"/> is 1.
/// </summary>
public sealed record HeardStation(
    string Callsign,
    string? PortId,
    string FirstHeard,
    string LastHeard,
    long Count,
    int Ports);

/// <summary>One learned per-peer AX.25 capability record, projected for the operator
/// surface (the web Capabilities screen + the MCP read tool). Mirrors the live
/// <c>PeerCapabilityCache</c> record, with the two <see cref="DateTimeOffset"/> instants
/// rendered as relative-ago strings (the NetRom row's "h:mm:ss" style) so the client
/// renders them without a clock of its own. The nullable bools carry the cache's
/// three-state meaning: <c>true</c>/<c>false</c> = learned, <c>null</c> = never probed
/// (the UI shows a "v2.2?" / "SREJ?" unknown badge). <see cref="LastRefused"/> is null
/// when the peer never refused/degraded an extended dial.</summary>
public sealed record PeerCapability(
    string PortId,
    string Peer,
    bool? SupportsExtended,
    bool? SupportsSrejViaXid,
    string LastProbed,
    string? LastRefused);
