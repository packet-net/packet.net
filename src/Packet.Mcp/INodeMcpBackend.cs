namespace Packet.Mcp;

/// <summary>
/// The seam between the transport-agnostic MCP tool surface and a concrete
/// source of live node state. Two implementations exist (see docs/mcp-design.md):
/// <list type="bullet">
/// <item><c>LiveNodeMcpBackend</c> (in the node host) — reads
/// <c>NodeHostedService</c> directly; this is what the in-process SSE transport
/// serves.</item>
/// <item><c>RestNodeMcpBackend</c> (in the <c>pdn mcp</c> stdio entrypoint) — an
/// HTTP client of the running node's loopback REST API, bridging stdio to the
/// live node across the process boundary.</item>
/// </list>
/// A fake implements it in tests. <c>decode_frame</c> is pure and does not go
/// through this seam.
/// </summary>
public interface INodeMcpBackend
{
    // ---- read ----

    /// <summary>Every configured port and its live state.</summary>
    Task<IReadOnlyList<McpPortStatus>> ListPortsAsync(CancellationToken ct = default);

    /// <summary>Every live AX.25 session across all ports.</summary>
    Task<IReadOnlyList<McpSessionInfo>> ListSessionsAsync(CancellationToken ct = default);

    /// <summary>Recent frames from the telemetry ring, filtered and bounded.</summary>
    Task<IReadOnlyList<McpMonitorFrame>> RecentFramesAsync(FrameFilter filter, CancellationToken ct = default);

    /// <summary>Per-link quality rollup for a given peer (optionally pinned to a port).</summary>
    Task<McpLinkQuality> LinkQualityAsync(string remote, string? portId = null, CancellationToken ct = default);

    /// <summary>The current NET/ROM topology snapshot.</summary>
    Task<McpNetworkTopology> NetworkTopologyAsync(CancellationToken ct = default);

    // ---- write (operate-gated, audited by the host) ----

    /// <summary>Send a UI frame on a port.</summary>
    Task<SendResult> SendUiFrameAsync(SendUiRequest req, McpCaller caller, CancellationToken ct = default);

    /// <summary>Restart a port (the <c>reset_port</c> action).</summary>
    Task<PortActionResult> ResetPortAsync(string portId, McpCaller caller, CancellationToken ct = default);

    /// <summary>Disconnect a live session by its <c>port:peer</c> id.</summary>
    Task<SessionResult> DisconnectSessionAsync(string sessionId, McpCaller caller, CancellationToken ct = default);

    /// <summary>Set a KISS parameter on a port.</summary>
    Task<KissParamResult> SetKissParamAsync(SetKissParamRequest req, McpCaller caller, CancellationToken ct = default);
}
