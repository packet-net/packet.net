using Packet.Core;
using Packet.Node.Core.Configuration;
using Packet.Node.Core.Console;
using Packet.Node.Core.Hosting;
using Packet.Rhp2;
using Packet.Rhp2.Server;

namespace Packet.Node.Rhp;

/// <summary>
/// The host-side implementation of <see cref="IRhpGateway"/>: maps the wire's
/// (port-label, local, remote) onto the running <see cref="PortSupervisor"/> and dials out
/// via the same claimed connect-out path the console uses. Owns the RHPv2 error vocabulary
/// (PWP-0222 codes) so the wire loop stays mechanical.
/// </summary>
/// <remarks>
/// Port labels are XRouter-convention: <b>1-indexed, in config order</b> (<c>"1"</c> = the
/// first <c>ports:</c> entry); null means the first port. R-2 limitation (named in
/// <c>docs/rhp2-server.md</c>): <c>local</c> must be the node's own callsign — originating
/// from an arbitrary app callsign needs the R-3 multi-callsign engine work.
/// </remarks>
public sealed class SupervisorRhpGateway : IRhpGateway
{
    /// <summary>Bound on the AX.25 connect (SABM retries ride the listener's own timers; this
    /// is the backstop so a wedged dial can't hold an RHP open forever).</summary>
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(90);

    private readonly NodeHostedService host;
    private readonly IConfigProvider config;

    public SupervisorRhpGateway(NodeHostedService host, IConfigProvider config)
    {
        this.host = host ?? throw new ArgumentNullException(nameof(host));
        this.config = config ?? throw new ArgumentNullException(nameof(config));
    }

    /// <inheritdoc/>
    public async Task<INodeConnection> OpenAx25StreamAsync(
        string? portLabel, string? local, string remote, CancellationToken cancellationToken = default)
    {
        var supervisor = host.Supervisor
            ?? throw new RhpGatewayException(RhpErrorCode.Unspecified, "Node engine is not running.");

        var ports = config.Current.Ports;
        if (ports.Count == 0)
        {
            throw new RhpGatewayException(RhpErrorCode.NoSuchPort, "No ports are configured on this node.");
        }

        // Port label: 1-indexed config order, null = the first port (XRouter convention).
        int index = 0;
        if (!string.IsNullOrWhiteSpace(portLabel))
        {
            if (!int.TryParse(portLabel, out var n) || n < 1 || n > ports.Count)
            {
                throw new RhpGatewayException(RhpErrorCode.NoSuchPort, $"No such port '{portLabel}' (1..{ports.Count}).");
            }
            index = n - 1;
        }
        var portId = ports[index].Id;

        // R-3: the session may originate from an application callsign (the wire's open.local);
        // null/the node's own callsign means dial as the node. The engine's (local, remote)
        // session keying routes the peer's replies to the right link either way.
        var nodeCall = config.Current.Identity.Callsign;
        Callsign? localOverride = null;
        if (!string.IsNullOrWhiteSpace(local) && !string.Equals(local.Trim(), nodeCall, StringComparison.OrdinalIgnoreCase))
        {
            if (!Callsign.TryParse(local.Trim().ToUpperInvariant(), out var localCall))
            {
                throw new RhpGatewayException(RhpErrorCode.InvalidLocalAddress, $"'{local}' is not a valid AX.25 callsign.");
            }
            localOverride = localCall;
        }

        if (!Callsign.TryParse(remote.Trim().ToUpperInvariant(), out var target))
        {
            throw new RhpGatewayException(RhpErrorCode.InvalidRemoteAddress, $"'{remote}' is not a valid AX.25 callsign.");
        }

        var connector = supervisor.ResolveConnector(portId, localOverride)
            ?? throw new RhpGatewayException(RhpErrorCode.NoSuchPort, $"Port '{portId}' is not running.");

        using var bounded = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        bounded.CancelAfter(ConnectTimeout);
        try
        {
            return await connector.ConnectAsync(target, bounded.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;   // server shutting down — not a route failure
        }
        catch (Exception ex) when (ex is TimeoutException or OperationCanceledException)
        {
            throw new RhpGatewayException(RhpErrorCode.NoRoute, $"Connect to {target} timed out.");
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException)
        {
            throw new RhpGatewayException(RhpErrorCode.NoRoute, $"Connect to {target} failed: {ex.Message}");
        }
    }

    /// <inheritdoc/>
    public IDisposable RegisterListener(string? portLabel, string local, Func<INodeConnection, string, Task> onAccepted)
    {
        var supervisor = host.Supervisor
            ?? throw new RhpGatewayException(RhpErrorCode.Unspecified, "Node engine is not running.");

        if (!Callsign.TryParse(local.Trim().ToUpperInvariant(), out var call))
        {
            throw new RhpGatewayException(RhpErrorCode.InvalidLocalAddress, $"'{local}' is not a valid AX.25 callsign.");
        }

        // Port scope: null = all ports (the wire's null bind port); "N" = 1-indexed config order.
        string? portId = null;
        var ports = config.Current.Ports;
        if (!string.IsNullOrWhiteSpace(portLabel))
        {
            if (!int.TryParse(portLabel, out var n) || n < 1 || n > ports.Count)
            {
                throw new RhpGatewayException(RhpErrorCode.NoSuchPort, $"No such port '{portLabel}' (1..{ports.Count}).");
            }
            portId = ports[n - 1].Id;
        }

        // The accept push wants the arrival port as its 1-indexed LABEL; the supervisor hands
        // us the port id — translate per the live config (config order defines the labels).
        Task OnAcceptedWithLabel(INodeConnection connection, string arrivalPortId)
        {
            var snapshot = config.Current.Ports;
            int idx = -1;
            for (int i = 0; i < snapshot.Count; i++)
            {
                if (string.Equals(snapshot[i].Id, arrivalPortId, StringComparison.Ordinal))
                {
                    idx = i;
                    break;
                }
            }
            return onAccepted(connection, idx >= 0 ? (idx + 1).ToString(System.Globalization.CultureInfo.InvariantCulture) : "1");
        }

        try
        {
            return supervisor.RegisterAppCallsign(call, portId, OnAcceptedWithLabel);
        }
        catch (InvalidOperationException ex)
        {
            // Already registered / the node's own callsign — the wire's "Duplicate socket".
            throw new RhpGatewayException(RhpErrorCode.DuplicateSocket, ex.Message);
        }
    }
}
