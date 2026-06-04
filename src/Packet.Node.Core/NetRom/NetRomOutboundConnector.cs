using Packet.Core;
using Packet.Node.Core.Console;

namespace Packet.Node.Core.NetRom;

/// <summary>
/// An <see cref="IOutboundConnector"/> that routes <c>connect &lt;alias&gt;</c>
/// across the network via NET/ROM when it can, and falls back to a direct AX.25
/// dial when it can't. This is the headline NET/ROM capability: a user at the node
/// prompt typing <c>C SOT</c> (an alias) or <c>C GB7SOT</c> (a distant callsign)
/// is resolved in the routing table to the best neighbour, an interlink is opened,
/// an L4 circuit is established end-to-end, and the console relays the user against
/// it — reaching a node the operator has no direct RF path to, by name.
/// </summary>
/// <remarks>
/// <para>
/// Resolution order: if NET/ROM connect-routing is enabled and the routing table
/// has a route to the target (matched as an alias <em>or</em> a callsign), open an
/// L4 circuit. Otherwise defer to the wrapped <see cref="IOutboundConnector"/> (the
/// slice-1 same-port AX.25 dial) — so a plain <c>C M0LTE-1</c> to a station on the
/// local channel still works exactly as before.
/// </para>
/// <para>
/// The connect command parses its target as a <see cref="Callsign"/>; a NET/ROM
/// alias (uppercase, ≤6 alphanumerics, no SSID) is a valid <c>Callsign.Base</c>, so
/// <c>C SOT</c> arrives here as <c>Callsign("SOT")</c> and we resolve its text
/// against the table's aliases — no parser change needed.
/// </para>
/// </remarks>
public sealed class NetRomOutboundConnector : IOutboundConnector
{
    private readonly NetRomService netRom;
    private readonly IOutboundConnector? fallback;
    private readonly Callsign originatingUser;

    public NetRomOutboundConnector(NetRomService netRom, IOutboundConnector? fallback, Callsign originatingUser)
    {
        this.netRom = netRom ?? throw new ArgumentNullException(nameof(netRom));
        this.fallback = fallback;
        this.originatingUser = originatingUser;
    }

    /// <inheritdoc/>
    public string PortId => fallback?.PortId ?? "netrom";

    /// <inheritdoc/>
    public async Task<INodeConnection> ConnectAsync(Callsign target, CancellationToken cancellationToken = default)
    {
        // Try a NET/ROM route first when connect-routing is enabled.
        if (netRom.ConnectEnabled)
        {
            var destination = netRom.Snapshot().ResolveDestination(target.ToString());
            if (destination?.BestRoute is not null)
            {
                return await netRom.ConnectCircuitAsync(destination, originatingUser, cancellationToken).ConfigureAwait(false);
            }
        }

        // No NET/ROM route — fall back to a direct AX.25 dial on the local channel.
        if (fallback is not null)
        {
            return await fallback.ConnectAsync(target, cancellationToken).ConfigureAwait(false);
        }

        throw new InvalidOperationException(
            $"no NET/ROM route to {target} and no local port to dial it on.");
    }
}
