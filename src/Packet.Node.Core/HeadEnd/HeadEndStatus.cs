namespace Packet.Node.Core.HeadEnd;

/// <summary>
/// The <c>GET /statusz</c> response from a head-end daemon (headend-v0.1.4+, #587): the instance's
/// stable identity, the live bridge count, and each bridge's client-connection state — the head-end
/// self-observability surface (#583) that <see cref="HeadEndHealthMonitor"/> polls. Older daemons
/// (≤0.1.3) answer 404 here; the poller falls back to the bare <c>GET /healthz</c> liveness probe.
/// Field names bind to the Go daemon's camelCase JSON (see <c>headend/api.go</c>
/// <c>StatusResponse</c>).
/// </summary>
public sealed record HeadEndStatus
{
    /// <summary>The head-end's stable instance id (== <see cref="HeadEndInventory.InstanceId"/>).</summary>
    public string InstanceId { get; init; } = "";

    /// <summary>The number of live device bridges (== <see cref="Bridges"/> count, precomputed by
    /// the daemon so external monitoring can read one field).</summary>
    public int BridgeCount { get; init; }

    /// <summary>Per-bridge status rows, in the daemon's stable (TCP-port) order.</summary>
    public IReadOnlyList<HeadEndBridgeStatus> Bridges { get; init; } = [];
}

/// <summary>One bridge row in a <see cref="HeadEndStatus"/>: the bridged device's stable id, its
/// raw-pipe TCP port, and whether a client (normally this node) is currently attached to the pipe.</summary>
public sealed record HeadEndBridgeStatus
{
    /// <summary>The stable device id (the same id the inventory carries and a port config binds to).</summary>
    public string Id { get; init; } = "";

    /// <summary>The TCP port carrying this device's raw transparent byte pipe.</summary>
    public int TcpPort { get; init; }

    /// <summary>True while a client holds the (single-client) raw pipe.</summary>
    public bool ClientConnected { get; init; }
}
