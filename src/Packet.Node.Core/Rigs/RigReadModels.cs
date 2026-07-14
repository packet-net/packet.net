using Packet.Node.Core.Api;
using Packet.Node.Core.Configuration;
using Packet.Node.Core.Hosting;

namespace Packet.Node.Core.Rigs;

/// <summary>
/// Projects the live node's rig-control attachments into <see cref="RigStatus"/> read models —
/// the shared logic behind <c>GET /api/v1/rigs</c> and <c>GET /api/v1/ports/{id}/rig</c>. Kept in
/// <c>Packet.Node.Core</c> (not the web layer) so it can be exercised directly against a live
/// <see cref="PortSupervisor"/> without booting an HTTP host. (Mirrors
/// <see cref="Radios.RadioReadModels"/>.)
/// </summary>
/// <remarks>
/// A configured rig is reported <c>attached: false</c> when its port isn't running, or when the
/// daemon was unreachable at bring-up and the port degraded to running without it (the
/// <see cref="RunningPort"/> then has no rig status monitor). An attached rig's status comes from
/// its live monitor snapshot.
/// </remarks>
public static class RigReadModels
{
    /// <summary>
    /// Every configured rig attachment (one per port that has a <c>rig:</c> block), attached or
    /// not. Ports without a rig block are omitted. Empty when the supervisor is still booting or
    /// no port has a rig.
    /// </summary>
    public static IReadOnlyList<RigStatus> All(PortSupervisor? supervisor, NodeConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        var list = new List<RigStatus>();
        foreach (var port in config.Ports)
        {
            if (port.Rig is { } rigConfig)
            {
                list.Add(Project(supervisor, port.Id, rigConfig));
            }
        }
        return list;
    }

    /// <summary>
    /// The rig status for one port. Returns <c>null</c> when <paramref name="portId"/> names no
    /// configured port (the endpoint maps that to 404). A configured port with no <c>rig:</c>
    /// block yields an <c>attached: false</c> status with an empty kind — an honest "this port
    /// has no rig", distinct from "no such port".
    /// </summary>
    public static RigStatus? ForPort(PortSupervisor? supervisor, NodeConfig config, string portId)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(portId);

        var port = config.Ports.FirstOrDefault(p => string.Equals(p.Id, portId, StringComparison.Ordinal));
        if (port is null)
        {
            return null;
        }
        if (port.Rig is not { } rigConfig)
        {
            return new RigStatus(
                PortId: portId, Attached: false, Kind: "", Endpoint: "", Backend: null,
                Manufacturer: null, Model: null, Capabilities: [], ConnectionState: "unknown",
                FrequencyHz: null, Mode: null, PassbandHz: null, Transmitting: null,
                Meters: null, SampledAt: null);
        }
        return Project(supervisor, portId, rigConfig);
    }

    // A running port with a live status monitor projects from it (attached); otherwise the config
    // describes a configured-but-not-attached rig.
    private static RigStatus Project(PortSupervisor? supervisor, string portId, PortRigConfig rigConfig)
    {
        if (supervisor?.GetPort(portId)?.RigStatus is { } monitor)
        {
            return monitor.Snapshot();
        }
        return new RigStatus(
            PortId: portId,
            Attached: false,
            Kind: rigConfig.Kind,
            Endpoint: rigConfig.DescribeEndpoint(),
            Backend: null,
            Manufacturer: null,
            Model: null,
            Capabilities: [],
            ConnectionState: "unknown",
            FrequencyHz: null,
            Mode: null,
            PassbandHz: null,
            Transmitting: null,
            Meters: null,
            SampledAt: null);
    }
}
