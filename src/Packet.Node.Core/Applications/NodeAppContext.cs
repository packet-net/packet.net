using Packet.Node.Core.Console;

namespace Packet.Node.Core.Applications;

/// <summary>
/// The launch context handed to an <see cref="INodeApplication"/>: who connected, how, and
/// with what arguments. This is what BPQ's application seam cannot cleanly give an app - the
/// connecting callsign, arrival port + transport, sysop status, and the verb arguments,
/// gathered in one place so the app needs no node internals to know its caller.
/// </summary>
public sealed class NodeAppContext
{
    /// <summary>The connecting station: a canonical AX.25 callsign (with SSID) for radio
    /// transports, or a remote endpoint string for telnet. Opaque to the app.</summary>
    public required string Callsign { get; init; }

    /// <summary>The transport the session arrived on.</summary>
    public required NodeTransportKind Transport { get; init; }

    /// <summary>The arrival port id when known (AX.25); null for telnet / network-arrived
    /// sessions.</summary>
    public string? PortId { get; init; }

    /// <summary>The tokens the user typed after the launch verb (may be empty). Joined with
    /// single spaces into the <c>args</c> header line for an external app.</summary>
    public IReadOnlyList<string> Args { get; init; } = [];

    /// <summary>Whether the launching session was sysop-elevated. Reserved - always
    /// <c>false</c> in slice 1 (apps launch from the unelevated prompt).</summary>
    public bool SysopElevated { get; init; }
}
