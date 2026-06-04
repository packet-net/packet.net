using Packet.NetRom.Routing;

namespace Packet.Node.Core.NetRom;

/// <summary>
/// The read-only view of the node's learned NET/ROM routing table. Surfaced to
/// the <c>Nodes</c> console command today, and structured so a future MCP
/// <c>network_topology</c> tool and the web monitor read the same model — they
/// all just call <see cref="Snapshot"/>.
/// </summary>
/// <remarks>
/// Pure read side. Nothing on this interface can transmit, originate a NODES
/// broadcast, or open a circuit — it only hands out an immutable
/// <see cref="NetRomRoutingSnapshot"/> of what the node has heard.
/// </remarks>
public interface INetRomRoutingView
{
    /// <summary>True if NET/ROM awareness is enabled on this node (at least one
    /// participating port). When false, the snapshot is always empty.</summary>
    bool Enabled { get; }

    /// <summary>Take an immutable, point-in-time snapshot of the learned routing
    /// table — destinations with their best-first routes, plus directly-heard
    /// neighbours.</summary>
    NetRomRoutingSnapshot Snapshot();
}
