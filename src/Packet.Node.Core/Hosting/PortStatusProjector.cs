using Packet.Node.Core.Api;
using Packet.Node.Core.Configuration;
using Packet.Node.Core.Telemetry;

namespace Packet.Node.Core.Hosting;

/// <summary>
/// The ONE derivation of a port's operator-facing <see cref="PortStatus"/>
/// (packet-net/packet.net#722). It reads the supervisor's port state model
/// (<see cref="PortHealth"/>) and adds the live counts: sessions off the listener, frames off
/// the telemetry tap, carrier sense off whichever source feeds the port's medium-access gate.
/// </summary>
/// <remarks>
/// <para>
/// It replaces four vocabularies that had drifted apart: two verbatim copies of an
/// <c>up</c>/<c>down</c>/<c>faulted</c> derivation in the read API and the ports API (both with
/// <c>lastError</c> hard-coded null, and both calling every not-yet-reconciled port "faulted",
/// so a port read "faulted" during boot and for an instant after a successful
/// <c>POST /ports/{id}/lifecycle up</c>), an <c>Enabled</c>-only derivation in the console
/// <c>PORTS</c> verb that never consulted the supervisor at all, and the browser's own
/// heuristic.
/// </para>
/// <para>
/// Every consumer now projects from here: <c>GET /ports</c>, <c>GET /ports/{id}</c>, the
/// <c>pdn_port_*</c> metrics, the MCP backend, and (through <see cref="PortHealth"/> directly)
/// the console.
/// </para>
/// </remarks>
public static class PortStatusProjector
{
    /// <summary>
    /// Project every configured port, in canonical config order. <paramref name="supervisor"/>
    /// is null only while the node is still starting - the ports then project from config alone
    /// (<c>configured</c> / <c>disabled</c>), never as a fault.
    /// </summary>
    public static PortStatus[] ProjectAll(
        PortSupervisor? supervisor, IReadOnlyList<PortConfig> ports, NodeTelemetry? telemetry)
    {
        ArgumentNullException.ThrowIfNull(ports);
        return [.. ports.Select(port => Project(supervisor, port, telemetry))];
    }

    /// <summary>Project one configured port.</summary>
    public static PortStatus Project(PortSupervisor? supervisor, PortConfig port, NodeTelemetry? telemetry)
    {
        ArgumentNullException.ThrowIfNull(port);

        var health = supervisor?.GetHealth(port.Id) ?? new PortHealth
        {
            Id = port.Id,
            State = port.Enabled ? PortState.Configured : PortState.Disabled,
            Since = default,
        };

        // The runtime half only exists while the port is serving; a port that is faulted,
        // retrying or mid-restart reports no sessions and no carrier sense (not "0 busy").
        var running = health.IsServing ? supervisor?.GetPort(port.Id) : null;
        var (framesIn, framesOut) = telemetry?.PortFrames(port.Id) ?? (0L, 0L);

        return new PortStatus(
            Id: port.Id,
            Enabled: port.Enabled,
            State: health.StateName,
            // Live sessions only: the listener's ActiveSessions is a peer CACHE that keeps
            // Disconnected entries until LRU eviction (review item C052, #694).
            SessionCount: running is { IsAlive: true }
                ? running.Listener.ActiveSessions.Count(SessionLiveness.IsLive)
                : 0,
            LastError: health.LastError,
            FramesIn: framesIn,
            FramesOut: framesOut,
            Degraded: health.Degraded,
            Since: health.Since,
            ChannelBusy: running is { IsAlive: true } ? running.CarrierSense?.ChannelBusy : null);
    }

    /// <summary>
    /// The status of an id that is no longer in config at all (a port removed by the very
    /// request that is now projecting it). Total rather than a 500.
    /// </summary>
    public static PortStatus Unknown(string id) => new(
        Id: id, Enabled: false, State: PortStates.Disabled, SessionCount: 0, LastError: null,
        FramesIn: 0, FramesOut: 0, Degraded: [], Since: default, ChannelBusy: null);
}
