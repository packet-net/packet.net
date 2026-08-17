namespace Packet.Node.Core.Api;

// The read-side DTOs the control API (Slice 3) projects from the live node and
// serves under /api/v1. Field names match docs/node-api.md + the web UI's
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

/// <summary>
/// Live state of one configured port - the single projection of the supervisor's port state
/// model (<c>PortHealth</c>), built by <c>PortStatusProjector</c> and served by
/// <c>/ports</c>, <c>/ports/{id}</c>, the metrics endpoint and the MCP backend
/// (packet-net/packet.net#722; it used to be derived twice, verbatim, with a third
/// vocabulary in the console and a fourth in the browser).
/// </summary>
/// <param name="State">The port's lifecycle state, one of <c>PortStates.All</c>:
/// <c>configured</c> | <c>disabled</c> | <c>starting</c> | <c>up</c> | <c>degraded</c> |
/// <c>faulted</c> | <c>retrying</c> | <c>stopping</c>. <c>up</c> and <c>degraded</c> are the
/// serving states.</param>
/// <param name="LastError">Why the port last failed to come up or died, or null if it never
/// has. Retained after a recovery, so a port that is up again still shows what happened.</param>
/// <param name="Degraded">The components a <c>degraded</c> port is running without
/// (<c>radio</c> / <c>rig</c> / <c>rigctld</c> / <c>transport</c>); empty otherwise.</param>
/// <param name="Since">When the port entered <paramref name="State"/> (UTC).</param>
/// <param name="ChannelBusy">Port-level carrier sense, from whichever source feeds the
/// listener's gate (radio hardware DCD, or a channel-sensing transport such as the
/// in-process soundmodem): true = busy, false = clear, null = the port has no
/// carrier-sense source (or is not serving).</param>
public sealed record PortStatus(
    string Id,
    bool Enabled,
    string State,
    int SessionCount,
    string? LastError,
    long FramesIn,
    long FramesOut,
    IReadOnlyList<string> Degraded,
    DateTimeOffset Since,
    bool? ChannelBusy = null);

/// <summary>
/// One <b>live</b> circuit: the <c>/sessions</c> family projects every session the engine holds
/// in a state other than <c>Disconnected</c> (established, handshaking or releasing), never the
/// listener's cached Disconnected peers (see <see cref="SessionLiveness"/>). Read
/// <see cref="State"/> to tell an established circuit from one still coming up or going down.
/// </summary>
/// <remarks>
/// <para>
/// <b>The last four fields are per-LINK, not per-session.</b> <see cref="UptimeSeconds"/>,
/// <see cref="BytesIn"/>, <see cref="BytesOut"/> and <see cref="LastActivity"/> are read from the
/// node's <c>(portId, peer)</c> telemetry link, which spans every circuit this node has had with
/// that callsign on that port since the port attached, and counts <em>all</em> traffic to and
/// from it (UI frames and beacons included), not just this circuit's I-frames. So a peer that
/// reconnects shows the running totals and an uptime measured from when it was first heard, not
/// from when the current circuit came up. Read them as "this peer on this port", and the
/// per-circuit truth as <see cref="State"/> / <see cref="Vs"/> / <see cref="Vr"/> /
/// <see cref="Window"/>, which come from the live session. Deliberate and documented rather than
/// silently wrong (review item C063, #694): the engine's <c>Ax25Session</c> carries no
/// per-session byte tally to report instead.
/// </para>
/// </remarks>
public sealed record SessionInfo(
    string Id,
    string PortId,
    string Peer,
    string Role,             // "console" | "interlink" | "bridge"
    string State,            // Connected, TimerRecovery, …
    int Vs,
    int Vr,
    int Window,
    long UptimeSeconds,      // link lifetime (this peer on this port), not this circuit's
    long BytesIn,            // link total, all frame types
    long BytesOut,           // link total, all frame types
    string LastActivity);    // last frame on the link, not necessarily on this circuit

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
