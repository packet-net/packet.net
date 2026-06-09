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
    NetRomSummary Netrom);

public sealed record NetRomSummary(int Neighbours, int Destinations, bool Inp3Enabled);

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
