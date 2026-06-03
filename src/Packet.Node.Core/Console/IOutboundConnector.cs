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
    /// Connect to <paramref name="target"/> and wrap the resulting session as a
    /// <see cref="INodeConnection"/>. Throws on connect failure (timeout /
    /// refusal); the console catches it and reports back to the user.
    /// </summary>
    Task<INodeConnection> ConnectAsync(Callsign target, CancellationToken cancellationToken = default);
}
