using Packet.Ax25.Session;
using Packet.Core;

namespace Packet.Node.Core.Console;

/// <summary>
/// An <see cref="IOutboundConnector"/> that dials out on one
/// <see cref="Ax25Listener"/> — the slice-1 same-port connect-out. The console's
/// <c>Connect</c> command uses it to open an outbound session and wrap it as a
/// <see cref="Ax25NodeConnection"/> to relay against the inbound user.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="Ax25Listener.ConnectAsync"/> raises <c>SessionAccepted</c> on
/// success — the SAME event the node uses to start an inbound console session.
/// Without coordination, dialling OUT to a station would also start a node
/// console <em>against that station</em> (spewing our prompt at it). The
/// optional <paramref name="claim"/> lets the owner (the port supervisor) mark
/// the dialled remote as outbound for the duration of the connect so its
/// <c>SessionAccepted</c> handler skips it; the claim is released once the
/// session is established (or the connect fails).
/// </para>
/// </remarks>
public sealed class Ax25OutboundConnector : IOutboundConnector
{
    private readonly Ax25Listener listener;
    private readonly Func<Callsign, IDisposable>? claim;

    public Ax25OutboundConnector(string portId, Ax25Listener listener, Func<Callsign, IDisposable>? claim = null)
    {
        PortId = portId ?? throw new ArgumentNullException(nameof(portId));
        this.listener = listener ?? throw new ArgumentNullException(nameof(listener));
        this.claim = claim;
    }

    /// <inheritdoc/>
    public string PortId { get; }

    /// <inheritdoc/>
    public async Task<INodeConnection> ConnectAsync(Callsign target, CancellationToken cancellationToken = default)
    {
        // Claim the remote as outbound so the supervisor's SessionAccepted handler
        // doesn't start a console session against it. Held across ConnectAsync
        // because the listener fires SessionAccepted synchronously within it.
        var ticket = claim?.Invoke(target);
        try
        {
            var session = await listener.ConnectAsync(target, cancellationToken).ConfigureAwait(false);
            return new Ax25NodeConnection(listener, session);
        }
        finally
        {
            ticket?.Dispose();
        }
    }
}
