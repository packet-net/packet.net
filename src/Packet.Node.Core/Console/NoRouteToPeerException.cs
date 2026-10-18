using Packet.Core;

namespace Packet.Node.Core.Console;

/// <summary>
/// A connect was refused before any frame was sent because the port's transport has no route
/// to the target (multipoint AXUDP with no peers entry for it).
/// </summary>
public sealed class NoRouteToPeerException : InvalidOperationException
{
    public NoRouteToPeerException(string portId, Callsign target)
        : base($"no route to {target} on port {portId}: this port has no peers entry for {target}, so nothing can be sent to it. Add a peers entry (call, host, port) for {target} to port {portId}.")
    {
        PortId = portId;
        Target = target;
    }

    /// <summary>The port the connect was attempted on.</summary>
    public string PortId { get; }

    /// <summary>The station that could not be routed to.</summary>
    public Callsign Target { get; }
}
