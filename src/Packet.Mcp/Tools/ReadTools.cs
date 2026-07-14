using System.ComponentModel;
using ModelContextProtocol.Server;

namespace Packet.Mcp.Tools;

/// <summary>
/// The read-side MCP tools — ops/diagnostics/network-exploration over live node
/// state. All gated <c>read</c> by the host (pass-through when auth is off, like
/// the REST read API). Backed by <see cref="INodeMcpBackend"/>, so the same tool
/// surface serves the in-process (live) and stdio (loopback-REST) backends.
/// </summary>
[McpServerToolType]
public sealed class ReadTools(INodeMcpBackend backend)
{
    private readonly INodeMcpBackend backend = backend;

    [McpServerTool(Name = "list_ports")]
    [Description("List every configured radio port and its live state (up/down/faulted, session count, frame counters).")]
    public Task<IReadOnlyList<McpPortStatus>> ListPorts(CancellationToken ct = default)
        => backend.ListPortsAsync(ct);

    [McpServerTool(Name = "list_sessions")]
    [Description("List every live AX.25 session across all ports (peer, role, data-link state, V(s)/V(r), byte counts).")]
    public Task<IReadOnlyList<McpSessionInfo>> ListSessions(CancellationToken ct = default)
        => backend.ListSessionsAsync(ct);

    [McpServerTool(Name = "recent_frames")]
    [Description("Recent frames from the node's monitor ring, oldest first. Filter by port, peer, kind, and age.")]
    public Task<IReadOnlyList<McpMonitorFrame>> RecentFrames(
        [Description("Only frames on this port.")] string? port = null,
        [Description("Only frames to/from this callsign.")] string? peer = null,
        [Description("Only frames of this kind (UI, I, RR, ...).")] string? kind = null,
        [Description("Only frames newer than this many seconds ago.")] int? sinceSeconds = null,
        [Description("Max frames to return (1..250).")] int? limit = null,
        CancellationToken ct = default)
        => backend.RecentFramesAsync(new FrameFilter(port, peer, kind, sinceSeconds, limit), ct);

    [McpServerTool(Name = "link_quality")]
    [Description("Per-link quality for a remote station: smoothed RTT, current retry count, REJ/SREJ counts, and frame/byte counters.")]
    public Task<McpLinkQuality> LinkQuality(
        [Description("Remote callsign (with optional -SSID).")] string remote,
        [Description("Pin to a specific port; omit to take the first link to that peer.")] string? port = null,
        CancellationToken ct = default)
        => backend.LinkQualityAsync(remote, port, ct);

    [McpServerTool(Name = "network_topology")]
    [Description("The NET/ROM topology the node has learned: directly-heard neighbours and reachable destinations with their routes.")]
    public Task<McpNetworkTopology> NetworkTopology(CancellationToken ct = default)
        => backend.NetworkTopologyAsync(ct);

    [McpServerTool(Name = "get_rig_status")]
    [Description("Rig-control (CAT) status per port: attachment/health, backend identity, the advertised capability slice (sets against an unadvertised capability are refused), dial frequency, mode/passband, PTT state, and the last TX-side meter sample (SWR / RF power — meters sample only while transmitting; idle they read null or stale). Omit port for every rig-configured port; name a port for just its rig (empty result = no such port; a port with no rig block reports attached:false with an empty kind).")]
    public Task<IReadOnlyList<McpRigStatus>> GetRigStatus(
        [Description("Only this port's rig; omit for every rig-configured port.")] string? port = null,
        CancellationToken ct = default)
        => backend.RigStatusAsync(port, ct);
}
