using Packet.Node.Core.Hosting;

namespace Packet.Node.Core.M0lteMap;

/// <summary>
/// The live <see cref="IM0lteMapStateSource"/>: the serving ports from the
/// <see cref="PortSupervisor"/> and the directly-heard neighbours from the NET/ROM routing table,
/// both read off <see cref="NodeHostedService"/>. Before the node has started (or with NET/ROM
/// off) the corresponding part is simply empty.
/// </summary>
public sealed class NodeM0lteMapStateSource(NodeHostedService host) : IM0lteMapStateSource
{
    private readonly NodeHostedService host = host ?? throw new ArgumentNullException(nameof(host));

    /// <inheritdoc/>
    public M0lteMapSnapshot Capture()
    {
        var serving = host.Supervisor?.RunningPortIds ?? [];
        var neighbours = host.NetRom?.Snapshot().Neighbours
            .Select(n => new M0lteMapNeighbourState(n.Neighbour.ToString(), n.PortId, n.PathQuality))
            .ToList() ?? [];
        return new M0lteMapSnapshot { ServingPortIds = serving, Neighbours = neighbours };
    }
}
