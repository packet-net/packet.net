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
/// <param name="LastRssiDbm">Received signal strength (dBm) of the most recent frame heard from this
/// station (on this port, or on whichever port heard it last for the node-wide view), when a radio
/// control channel measured it — <c>null</c> when the port has no radio attached or the newest frame
/// carried no attributed RSSI.</param>
/// <param name="LastSnrDb">Signal-to-noise ratio (dB) of the most recent frame heard from this station
/// (on this port, or on whichever port heard it last for the node-wide view), when a radio control
/// channel measured it — <c>null</c> when the port has no radio attached or the newest frame carried
/// no attributed SNR.</param>
/// <param name="MedianPreDataCarrierMs">Rolling median of the station's measured carrier-rise→data
/// lead (ms) — its effective TXDELAY as heard here plus a small constant rig overhead — over the last
/// 32 burst-opening frames a radio attributed. <c>null</c> when never measured.</param>
/// <param name="PreDataCarrierSamples">Samples behind <see cref="MedianPreDataCarrierMs"/> (a
/// confidence signal); 0 when never measured.</param>
/// <param name="TxDelayAdvisory">The passive excess-TXDELAY advisory for this station, when its
/// median pre-data carrier exceeds the threshold with enough samples behind it (e.g.
/// <c>"GB7XXX keys ~412 ms before data — TXDELAY likely too high, wasting airtime …"</c>);
/// <c>null</c> for a healthy or unmeasured station. Computed by
/// <c>Packet.Tune.Core.ExcessTxDelayAdvisor</c> — see docs/research/txdelay-optimisation.md.</param>
public sealed record HeardStation(
    string Callsign,
    string? PortId,
    string FirstHeard,
    string LastHeard,
    long Count,
    int Ports,
    float? LastRssiDbm = null,
    float? LastSnrDb = null,
    float? MedianPreDataCarrierMs = null,
    int PreDataCarrierSamples = 0,
    string? TxDelayAdvisory = null);

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
