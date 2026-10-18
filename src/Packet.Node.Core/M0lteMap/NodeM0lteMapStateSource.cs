using Packet.Node.Core.Hosting;

namespace Packet.Node.Core.M0lteMap;

/// <summary>
/// The live <see cref="IM0lteMapStateSource"/>: the serving ports from the
/// <see cref="PortSupervisor"/>, and the NET/ROM neighbours the node has a live L2 link to. A
/// neighbour is a directly-heard station in the NET/ROM routing table; it is reported only while
/// a connected AX.25 session to it is up on the port it was heard on, which is LinBPQ's rule
/// (the neighbour's link at <c>L2STATE &gt;= 5</c>). Before the node has started, or with NET/ROM
/// off, the corresponding part is empty.
/// </summary>
public sealed class NodeM0lteMapStateSource(NodeHostedService host) : IM0lteMapStateSource
{
    private readonly NodeHostedService host = host ?? throw new ArgumentNullException(nameof(host));

    /// <inheritdoc/>
    public M0lteMapSnapshot Capture()
    {
        var supervisor = host.Supervisor;
        if (supervisor is null)
        {
            return M0lteMapSnapshot.Empty;
        }

        var serving = supervisor.RunningPortIds;
        var live = new HashSet<(string PortId, string Remote)>();
        foreach (var portId in serving)
        {
            var port = supervisor.GetPort(portId);
            if (port is null)
            {
                continue;
            }
            foreach (var session in port.Listener.ActiveSessions)
            {
                if (Api.SessionLiveness.IsLive(session))
                {
                    live.Add((portId, session.Context.Remote.ToString()));
                }
            }
        }

        var neighbours = host.NetRom?.Snapshot().Neighbours
            .Select(n => new M0lteMapNeighbourState(n.Neighbour.ToString(), n.PortId, n.PathQuality))
            .Where(n => live.Contains((n.PortId, n.Callsign)))
            .ToList() ?? [];
        return new M0lteMapSnapshot { ServingPortIds = serving, Neighbours = neighbours };
    }
}
