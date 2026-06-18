namespace Packet.Node.Core.Oarc;

/// <summary>
/// The node-state snapshot seam the <c>OarcReporter</c> polls each tick (#459). It decouples the
/// reporter's policy (diffing for up/down, gating by toggle, mapping to DTOs, the send queue) from
/// the AX.25 / NET/ROM internals it reports on — so the reporter is unit-tested against a fake source,
/// and the concrete <c>OarcStateSource</c> adapter owns the (subsystem-specific) capture: enumerating
/// each running port's <c>Ax25Listener.ActiveSessions</c>, correlating per-link frame/byte counters
/// from <c>NodeTelemetry</c>, reading <c>NetRomService.Circuits</c>, and tracking inbound-vs-outbound
/// via the <c>SessionAccepted</c> / <c>IncomingCircuit</c> events.
/// </summary>
public interface IOarcStateSource
{
    /// <summary>Capture a consistent point-in-time view of the node's reportable state.</summary>
    OarcNodeSnapshot Capture();
}

/// <summary>A point-in-time view of everything the reporter needs for node-status + the link/circuit
/// diff. <see cref="UptimeSeconds"/> and <see cref="L3Relayed"/> feed node-status; the link/circuit
/// lists drive both the up/down diff and the periodic status reports.</summary>
public sealed record OarcNodeSnapshot
{
    /// <summary>Seconds since the node host started.</summary>
    public long UptimeSeconds { get; init; }

    /// <summary>Total L3 datagrams this node has forwarded (transit), for node-status
    /// <c>l3Relayed</c>. 0 on an endpoint-only node.</summary>
    public long L3Relayed { get; init; }

    /// <summary>The currently-connected L2 links (AX.25 sessions in a connected state).</summary>
    public IReadOnlyList<OarcLinkState> Links { get; init; } = [];

    /// <summary>The currently-connected L4 NET/ROM circuits.</summary>
    public IReadOnlyList<OarcCircuitState> Circuits { get; init; } = [];
}

/// <summary>
/// One live L2 link in a snapshot. <see cref="Id"/> is a stable positive serial the adapter assigns
/// per live session (never reused while alive) — it is both the collector's link <c>id</c> and the
/// reporter's diff key. <see cref="Inbound"/> maps to the wire <c>direction</c>: <c>true</c> →
/// <c>"incoming"</c> (remote-initiated uplink), <c>false</c> → <c>"outgoing"</c> (locally-initiated
/// downlink). RTT is optional (omitted when the engine has no smoothed estimate yet).
/// </summary>
public sealed record OarcLinkState
{
    public required int Id { get; init; }
    public required string Port { get; init; }
    public required string Local { get; init; }
    public required string Remote { get; init; }
    public required bool Inbound { get; init; }
    public long FramesSent { get; init; }
    public long FramesReceived { get; init; }
    public long FramesResent { get; init; }
    public long BytesSent { get; init; }
    public long BytesReceived { get; init; }
    public long? L2RttMs { get; init; }
    public long UpForSeconds { get; init; }
}

/// <summary>
/// One live L4 NET/ROM circuit in a snapshot. <see cref="Id"/> is a stable positive serial (the
/// circuit's local id, or an adapter-assigned serial), both the wire <c>id</c> and the diff key.
/// <see cref="Inbound"/> maps to <c>direction</c> as for links. Segment/byte counters are best-effort:
/// the current <c>NetRomCircuit</c> surface does not expose them, so the adapter reports 0 until they
/// are surfaced (a named follow-up — see <c>docs/oarc-reporting-design.md</c> §9); a circuit still
/// appears on the map with its identity + lifecycle.
/// </summary>
public sealed record OarcCircuitState
{
    public required int Id { get; init; }
    public required string Local { get; init; }
    public required string Remote { get; init; }
    public required bool Inbound { get; init; }
    public int? Service { get; init; }
    public long SegmentsSent { get; init; }
    public long SegmentsReceived { get; init; }
    public long SegmentsResent { get; init; }
    public long SegmentsQueued { get; init; }
    public long? BytesSent { get; init; }
    public long? BytesReceived { get; init; }
    public long UpForSeconds { get; init; }
}
