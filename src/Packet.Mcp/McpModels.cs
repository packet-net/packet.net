using System.ComponentModel;

namespace Packet.Mcp;

// The transport-agnostic DTOs the MCP tools return and the INodeMcpBackend
// supplies. Deliberately Packet.Mcp's own shapes (not Packet.Node.Core's
// ReadModels) so the library stays decoupled from the node host: the live
// backend maps NodeHostedService state into these, the REST backend maps
// the /api/v1 JSON into these, and tests use a fake. See docs/mcp-design.md.

/// <summary>A configured port and its live state (the <c>list_ports</c> projection).</summary>
public sealed record McpPortStatus(
    [property: Description("Port id (the config key).")] string Id,
    [property: Description("Whether the port is enabled in config.")] bool Enabled,
    [property: Description("Live state: up | down | faulted.")] string State,
    [property: Description("Active AX.25 sessions on the port.")] int SessionCount,
    [property: Description("Frames received on the port since start.")] long FramesIn,
    [property: Description("Frames transmitted on the port since start.")] long FramesOut);

/// <summary>One live AX.25 session (the <c>list_sessions</c> projection).</summary>
public sealed record McpSessionInfo(
    [property: Description("Session id, formatted port:peer.")] string Id,
    [property: Description("Port the session lives on.")] string PortId,
    [property: Description("Remote callsign (with SSID).")] string Peer,
    [property: Description("Classification: console | interlink.")] string Role,
    [property: Description("AX.25 data-link state name.")] string State,
    [property: Description("V(s) — send state variable.")] int Vs,
    [property: Description("V(r) — receive state variable.")] int Vr,
    [property: Description("Window size k.")] int Window,
    [property: Description("Seconds since the session opened.")] long UptimeSeconds,
    [property: Description("Bytes received on the session.")] long BytesIn,
    [property: Description("Bytes transmitted on the session.")] long BytesOut,
    [property: Description("Relative time since last activity, e.g. 0:01:14.")] string LastActivity);

/// <summary>One observed frame from the telemetry ring (the <c>recent_frames</c> projection).</summary>
public sealed record McpMonitorFrame(
    [property: Description("Monotonic sequence number within the ring.")] long Seq,
    [property: Description("UTC timestamp the frame was observed.")] DateTimeOffset Timestamp,
    [property: Description("Port the frame was seen on.")] string PortId,
    [property: Description("in (received) | out (transmitted).")] string Direction,
    [property: Description("Source callsign.")] string Source,
    [property: Description("Destination callsign.")] string Destination,
    [property: Description("Frame kind, e.g. UI, I, RR, SABM.")] string Kind,
    [property: Description("Frame length in bytes.")] int Length);

/// <summary>Per-link quality rollup (the <c>link_quality</c> projection).</summary>
public sealed record McpLinkQuality(
    [property: Description("Port the link is on.")] string PortId,
    [property: Description("Remote callsign.")] string Peer,
    [property: Description("Smoothed round-trip time (ms) from the link's SRT estimator; 0 when no connected-mode session backs the link.")] int SmoothedRttMs,
    [property: Description("Current retransmission count (RC) on the link; 0 when no connected-mode session backs the link.")] int Retries,
    [property: Description("REJ frames observed on the link.")] long RejCount,
    [property: Description("SREJ frames observed on the link.")] long SrejCount,
    [property: Description("Frames received on the link.")] long FramesIn,
    [property: Description("Frames transmitted on the link.")] long FramesOut,
    [property: Description("True when no link to this peer is currently known.")] bool Unknown);

/// <summary>The NET/ROM topology (the <c>network_topology</c> projection).</summary>
public sealed record McpNetworkTopology(
    [property: Description("When the routing snapshot was generated (UTC).")] DateTimeOffset GeneratedAt,
    [property: Description("Directly-heard neighbours.")] IReadOnlyList<McpNeighbour> Neighbours,
    [property: Description("Reachable destinations and their routes.")] IReadOnlyList<McpDestination> Destinations);

/// <summary>A directly-heard NET/ROM neighbour.</summary>
public sealed record McpNeighbour(
    string Neighbour, string? Alias, string PortId, int PathQuality, string LastHeard);

/// <summary>A reachable NET/ROM destination and the routes to it.</summary>
public sealed record McpDestination(
    string Destination, string? Alias, IReadOnlyList<McpRoute> Routes);

/// <summary>One route to a destination, via a neighbour.</summary>
public sealed record McpRoute(string Neighbour, int Quality, int Obsolescence);

/// <summary>Filter for <c>recent_frames</c>. All fields optional.</summary>
public sealed record FrameFilter(
    [property: Description("Only frames on this port.")] string? Port = null,
    [property: Description("Only frames to/from this callsign.")] string? Peer = null,
    [property: Description("Only frames of this kind (UI, I, RR, ...).")] string? Kind = null,
    [property: Description("Only frames newer than this many seconds ago.")] int? SinceSeconds = null,
    [property: Description("Max frames to return (1..250).")] int? Limit = null);

// ---- write-tool requests + results ----

/// <summary>Request for <c>send_ui_frame</c>.</summary>
public sealed record SendUiRequest(
    [property: Description("Port to transmit on.")] string Port,
    [property: Description("Destination callsign (with optional -SSID).")] string Dest,
    [property: Description("Payload as a UTF-8 string.")] string Payload,
    [property: Description("Optional digipeater path, in order.")] IReadOnlyList<string>? Path = null,
    [property: Description("PID byte; defaults to 0xF0 (no layer 3).")] byte Pid = 0xF0);

/// <summary>Result of a write tool that sends a frame.</summary>
public sealed record SendResult(bool Accepted, string Message);

/// <summary>Result of a port action (reset).</summary>
public sealed record PortActionResult(bool Accepted, string PortId, string Message);

/// <summary>Result of a session action (disconnect).</summary>
public sealed record SessionResult(bool Accepted, string SessionId, string Message);

/// <summary>Request for <c>set_kiss_param</c>.</summary>
public sealed record SetKissParamRequest(
    [property: Description("Port whose KISS parameter to set.")] string Port,
    [property: Description("Parameter name, e.g. txdelay, persist, slottime, txtail.")] string Param,
    [property: Description("Parameter value (0..255 for the byte params).")] int Value);

/// <summary>
/// Result of <c>set_kiss_param</c>. <paramref name="RequiresRestart"/> tells the
/// caller whether the change took on the live KISS link or needs a port restart
/// (the construction-time params like <c>kiss.ackMode</c>).
/// </summary>
public sealed record KissParamResult(bool Accepted, bool RequiresRestart, string Message);

/// <summary>
/// Scope names mirroring the node's <c>AuthScopes</c> (the hierarchical
/// <c>read</c> ⊂ <c>operate</c> ⊂ <c>admin</c> model). Kept as plain strings here
/// so Packet.Mcp stays free of the node-host assembly; the host populates a
/// caller's <see cref="McpCaller.Scopes"/> with the expanded set.
/// </summary>
public static class McpScopes
{
    /// <summary>Read tools.</summary>
    public const string Read = "read";
    /// <summary>Write tools.</summary>
    public const string Operate = "operate";
    /// <summary>Administrative (superset of operate).</summary>
    public const string Admin = "admin";
}

/// <summary>
/// Who is invoking a tool, for authorization + the audit trail. <see cref="Actor"/>
/// is the token subject over SSE, or <c>local-stdio</c> over the stdio bridge.
/// <see cref="Scopes"/> is the caller's expanded scope set (the host fills it from
/// the authenticated token, or grants all when auth is off / for the local user).
/// </summary>
public sealed record McpCaller(string Actor, string Transport, IReadOnlySet<string> Scopes, string? ClientIp = null)
{
    /// <summary>True if the caller holds <paramref name="scope"/>.</summary>
    public bool HasScope(string scope) => Scopes.Contains(scope);

    /// <summary>
    /// The local-user identity for the stdio transport (no token). A process that
    /// can exec <c>pdn mcp</c> and reach loopback is OS-trusted, so it holds every
    /// scope. No client IP — it's a local process.
    /// </summary>
    public static McpCaller LocalStdio { get; } =
        new("local-stdio", "stdio", new HashSet<string>(StringComparer.Ordinal)
            { McpScopes.Read, McpScopes.Operate, McpScopes.Admin });
}
