using Packet.Core;
using Packet.NetRom.Transport;

namespace Packet.Node.Core.NetRom;

/// <summary>
/// A NET/ROM connect with nowhere to go: the routing table has no usable route to the
/// destination, either because none was ever learned or because this connect tried every
/// next hop and none of their interlinks came up (<see cref="NextHopsFailed"/>). Derives from
/// <see cref="InvalidOperationException"/>, which is what this failure was thrown as before it
/// had a type of its own, so existing handlers still catch it.
/// </summary>
public sealed class NetRomNoRouteException : InvalidOperationException
{
    public NetRomNoRouteException(string destination, string message, bool nextHopsFailed = false)
        : base(message)
    {
        Destination = destination;
        NextHopsFailed = nextHopsFailed;
    }

    /// <summary>The destination as it was asked for (a callsign or an alias).</summary>
    public string Destination { get; }

    /// <summary>
    /// True when the destination was in the table and this connect tried its routes, but the
    /// interlink to every next hop failed (each was marked down as it failed). A dead neighbour,
    /// not an unknown destination: BPQ says "Failure with" for it, never "not a node".
    /// </summary>
    public bool NextHopsFailed { get; }
}

/// <summary>
/// An L4 circuit that closed before it connected, with the reason (packet-net/packet.net#849):
/// <see cref="NetRomCircuitCloseReason.Refused"/> when the far node answered the Connect Request
/// with the choke (busy) bit, <see cref="NetRomCircuitCloseReason.Timeout"/> when its retries
/// ran out with no answer. Derives from <see cref="IOException"/>, which is what this failure
/// was thrown as before, so existing handlers still catch it.
/// </summary>
public sealed class NetRomCircuitFailedException : IOException
{
    public NetRomCircuitFailedException(Callsign destination, NetRomCircuitCloseReason reason)
        : base($"NET/ROM circuit to {destination} {reason.ToString().ToLowerInvariant()}.")
    {
        Destination = destination;
        Reason = reason;
    }

    /// <summary>The node the circuit was for.</summary>
    public Callsign Destination { get; }

    /// <summary>Why the circuit closed.</summary>
    public NetRomCircuitCloseReason Reason { get; }
}
