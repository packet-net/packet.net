using Packet.Node.Core.Api;
using Packet.Node.Core.Configuration;
using Packet.Node.Core.Hosting;

namespace Packet.Node.Core.Radios;

/// <summary>
/// Projects the live node's radio-control attachments into <see cref="RadioStatus"/> read models —
/// the shared logic behind <c>GET /api/v1/radios</c> and <c>GET /api/v1/ports/{id}/radio</c>. Kept in
/// <c>Packet.Node.Core</c> (not the web layer) so it can be exercised directly against a live
/// <see cref="PortSupervisor"/> without booting an HTTP host.
/// </summary>
/// <remarks>
/// A configured radio is reported <c>attached: false</c> when its port isn't running, or when the
/// radio failed to open and the port degraded to running without it (the <see cref="RunningPort"/>
/// then has no status monitor). An attached radio's status comes from its live monitor snapshot.
/// </remarks>
public static class RadioReadModels
{
    /// <summary>
    /// Every configured radio attachment (one per port that has a <c>radio:</c> block), attached or
    /// not. Ports without a radio block are omitted. Empty when the supervisor is still booting or no
    /// port has a radio.
    /// </summary>
    public static IReadOnlyList<RadioStatus> All(PortSupervisor? supervisor, NodeConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        var list = new List<RadioStatus>();
        foreach (var port in config.Ports)
        {
            if (port.Radio is { } radioConfig)
            {
                list.Add(Project(supervisor, port.Id, radioConfig));
            }
        }
        return list;
    }

    /// <summary>
    /// The radio status for one port. Returns <c>null</c> when <paramref name="portId"/> names no
    /// configured port (the endpoint maps that to 404). A configured port with no <c>radio:</c> block
    /// yields an <c>attached: false</c> status with an empty kind — an honest "this port has no radio",
    /// distinct from "no such port".
    /// </summary>
    public static RadioStatus? ForPort(PortSupervisor? supervisor, NodeConfig config, string portId)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(portId);

        var port = config.Ports.FirstOrDefault(p => string.Equals(p.Id, portId, StringComparison.Ordinal));
        if (port is null)
        {
            return null;
        }
        if (port.Radio is not { } radioConfig)
        {
            return new RadioStatus(
                PortId: portId, Attached: false, Kind: "", ControlPort: null, Serial: null,
                Identity: null, ConnectionState: "unknown", ChannelBusy: null, Health: null);
        }
        return Project(supervisor, portId, radioConfig);
    }

    // A running port with a live status monitor projects from it (attached); otherwise the config
    // describes a configured-but-not-attached radio.
    private static RadioStatus Project(PortSupervisor? supervisor, string portId, PortRadioConfig radioConfig)
    {
        if (supervisor?.GetPort(portId)?.RadioStatus is { } monitor)
        {
            return monitor.Snapshot();
        }
        return new RadioStatus(
            PortId: portId,
            Attached: false,
            Kind: radioConfig.Kind,
            ControlPort: string.IsNullOrWhiteSpace(radioConfig.Port) ? null : radioConfig.Port,
            Serial: string.IsNullOrWhiteSpace(radioConfig.Serial) ? null : radioConfig.Serial,
            Identity: null,
            ConnectionState: "unknown",
            ChannelBusy: null,
            Health: null);
    }
}
