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

        // R-2: the originating callsign is the node's own; an app callsign needs R-3's
        // multi-callsign engine work (named limitation — docs/rhp2-server.md).
        var nodeCall = config.Current.Identity.Callsign;
        if (!string.IsNullOrWhiteSpace(local) && !string.Equals(local.Trim(), nodeCall, StringComparison.OrdinalIgnoreCase))
        {
            throw new RhpGatewayException(
                RhpErrorCode.InvalidLocalAddress,
                $"local must be the node callsign '{nodeCall}' (app-callsign origination lands in R-3).");
        }

        if (!Callsign.TryParse(remote.Trim().ToUpperInvariant(), out var target))
        {
            throw new RhpGatewayException(RhpErrorCode.InvalidRemoteAddress, $"'{remote}' is not a valid AX.25 callsign.");
        }

        var connector = supervisor.ResolveConnector(portId)
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
}
