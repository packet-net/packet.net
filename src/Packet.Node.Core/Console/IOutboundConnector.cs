using Packet.Core;

namespace Packet.Node.Core.Console;

/// <summary>
/// Opens an outbound connection to a remote station on behalf of a console
/// session's <c>Connect</c> command, returning it as another
/// <see cref="INodeConnection"/> so the console can relay the two together with
/// no AX.25 knowledge. In slice 1 this is same-port-only: the connector an
/// AX.25 console session is given dials on the very listener the inbound session
/// arrived on.
/// </summary>
public interface IOutboundConnector
{
    /// <summary>A human label for the port this connector dials on (for prompts
    /// and errors).</summary>
    string PortId { get; }

    /// <summary>
    /// What to tell the user before dialling <paramref name="target"/>, e.g. <c>BDH on axip-1</c>
    /// for a direct dial, or <c>BDH via GB7BDH on axip-2</c> when a NET/ROM route carries it.
    /// Names the route actually used, not the connector's default port.
    /// </summary>
    string DescribeRoute(Callsign target) => $"{target} on {PortId}";

    /// <summary>
    /// Connect to <paramref name="target"/> and wrap the resulting session as a
    /// <see cref="INodeConnection"/>. Throws on connect failure (timeout /
    /// refusal); the console catches it and reports back to the user.
    /// </summary>
    Task<INodeConnection> ConnectAsync(Callsign target, CancellationToken cancellationToken = default);
}
