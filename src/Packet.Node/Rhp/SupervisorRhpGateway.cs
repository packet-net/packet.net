using System.Globalization;
using Packet.Ax25;
using Packet.Ax25.Session;
using Packet.Core;
using Packet.Node.Core.Configuration;
using Packet.Node.Core.Console;
using Packet.Node.Core.Hosting;
using Packet.Node.Core.NetRom;
using Packet.NetRom.Transport;
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
/// A port label is the operator-defined <see cref="PortConfig.Id"/> (e.g. <c>"vhf-2m"</c>,
/// <c>"hf-40m"</c>), matched case-insensitively, or failing that the port's number in
/// configuration order counting from 1 (<c>"1"</c> = the first <c>ports:</c> entry), the
/// numeric form RHPv2 and XRouter-convention clients such as DAPPS send (#841). An id
/// match wins, so a port whose id is a number is always that port. For outbound opens, null resolves a
/// locally-registered app (loopback), or else a NET/ROM destination by callsign or alias (an L4
/// circuit, as the console's <c>C &lt;alias&gt;</c>, #932), or errors - it does not silently
/// default to the first port. R-2 limitation (named in <c>docs/rhp2-server.md</c>): <c>local</c> must be the
/// node's own callsign - originating from an arbitrary app callsign needs the R-3
/// multi-callsign engine work.
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

        // With NO explicit port, the open goes where the console's `C <call or alias>` goes: a
        // target the node is locally registered for (an app on its own SSID) is reached by an
        // in-process loopback crossconnect, and anything else is routed over NET/ROM, resolved
        // in the routing table by callsign or alias, as an L4 circuit (#932). The connector is
        // the console's own NET/ROM one, with no direct AX.25 fallback: a null port that matches
        // neither must not silently dial ports[0] (#665: an upstream bug that stripped the label
        // to null dialled the wrong port at the GB7RDG cutover), so no route is errCode 15. With
        // NET/ROM connect routing off, it is still the #665 errCode 10. An EXPLICIT port is an
        // explicit "go to RF" and always dials L2, unchanged.
        IOutboundConnector connector;
        if (string.IsNullOrWhiteSpace(portLabel))
        {
            var callerPeerId = localOverride?.ToString() ?? nodeCall;
            Callsign originatingUser = localOverride
                ?? (Callsign.TryParse(nodeCall.Trim().ToUpperInvariant(), out var nc) ? nc : default);
            connector = supervisor.TryResolveLocalAppConnector(target, callerPeerId, NodeTransportKind.Ax25)
                ?? supervisor.ResolveNetRomConnector(originatingUser)
                ?? throw new RhpGatewayException(RhpErrorCode.NoSuchPort,
                    $"No port specified, '{target}' is not a locally registered app, and NET/ROM connect routing is off; an explicit port id is required ({PortList(ports)}).");
        }
        else
        {
            var portId = ResolvePortId(portLabel, ports);
            connector = supervisor.ResolveConnector(portId, localOverride)
                ?? throw new RhpGatewayException(RhpErrorCode.NoSuchPort, $"Port '{portId}' is not running.");
        }

        using var bounded = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        bounded.CancelAfter(ConnectTimeout);
        try
        {
            return await connector.ConnectAsync(target, bounded.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;   // server shutting down - not a route failure
        }
        catch (Exception ex) when (ex is TimeoutException or OperationCanceledException)
        {
            throw new RhpGatewayException(RhpErrorCode.TimedOut, $"Connect to {target} timed out.");
        }
        catch (Exception ex) when (MapConnectFailure(ex, target) is { } mapped)
        {
            throw mapped;
        }
    }

    // Why a dial failed, in the wire's terms (docs/rhp2-server.md, "Why an open failed"; #849).
    // Null for an exception that is not a connect failure, which then propagates as it is.
    private static RhpGatewayException? MapConnectFailure(Exception ex, Callsign target) => ex switch
    {
        // pdn extension, error 18 (docs/rhp2-server.md row 8): the link to the peer is up and
        // held by an accept or a console, so this open would only reset it (#862).
        LinkAlreadyUpException => new RhpGatewayException(RhpErrorCode.AlreadyConnected, ex.Message),
        // The port's transport (multipoint AXUDP) has no way to reach the station at all.
        NoRouteToPeerException => new RhpGatewayException(RhpErrorCode.NoRoute, $"No route to {target}: {ex.Message}"),
        // A portless open (#932) whose destination the NET/ROM table has no usable route to.
        NetRomNoRouteException => new RhpGatewayException(RhpErrorCode.NoRoute, $"No route to {target}: {ex.Message}"),
        // The L4 circuit's own outcome: the far node's choke (busy) bit, or its retries running out.
        NetRomCircuitFailedException { Reason: NetRomCircuitCloseReason.Refused } =>
            new RhpGatewayException(RhpErrorCode.Refused, $"Connect to {target} refused: the far node answered busy."),
        NetRomCircuitFailedException { Reason: NetRomCircuitCloseReason.Timeout } =>
            new RhpGatewayException(RhpErrorCode.NoAnswer, $"Connect to {target} got no answer over NET/ROM (the L4 retries ran out)."),
        NetRomCircuitFailedException =>
            new RhpGatewayException(RhpErrorCode.LinkReset, $"Connect to {target} failed: the NET/ROM circuit closed before it connected."),
        Ax25ConnectFailedException { Failure: Ax25ConnectFailure.Refused } =>
            new RhpGatewayException(RhpErrorCode.Refused, $"Connect to {target} refused: the peer answered DM (busy or not accepting)."),
        Ax25ConnectFailedException { Failure: Ax25ConnectFailure.NoAnswer } =>
            new RhpGatewayException(RhpErrorCode.NoAnswer, $"Connect to {target} got no answer (T1 expired N2 times)."),
        Ax25ConnectFailedException =>
            new RhpGatewayException(RhpErrorCode.LinkReset, $"Connect to {target} failed: the link was reset before it came up."),
        // Anything else the dial threw keeps the code it always had.
        InvalidOperationException or IOException =>
            new RhpGatewayException(RhpErrorCode.NoRoute, $"Connect to {target} failed: {ex.Message}"),
        _ => null,
    };

    /// <inheritdoc/>
    public IDisposable RegisterListener(string? portLabel, string local, Func<INodeConnection, string, Task> onAccepted)
    {
        var supervisor = host.Supervisor
            ?? throw new RhpGatewayException(RhpErrorCode.Unspecified, "Node engine is not running.");

        if (!Callsign.TryParse(local.Trim().ToUpperInvariant(), out var call))
        {
            throw new RhpGatewayException(RhpErrorCode.InvalidLocalAddress, $"'{local}' is not a valid AX.25 callsign.");
        }

        // Port scope: null = all ports (the wire's null bind port); otherwise a port id.
        string? portId = null;
        var ports = config.Current.Ports;
        if (!string.IsNullOrWhiteSpace(portLabel))
        {
            portId = ResolvePortId(portLabel, ports);
        }

        Task OnAcceptedWithLabel(INodeConnection connection, string arrivalPortId)
            => onAccepted(connection, arrivalPortId);

        try
        {
            return supervisor.RegisterAppCallsign(call, portId, OnAcceptedWithLabel);
        }
        catch (InvalidOperationException ex)
        {
            // Already registered / the node's own callsign - the wire's "Duplicate socket".
            throw new RhpGatewayException(RhpErrorCode.DuplicateSocket, ex.Message);
        }
    }

    /// <inheritdoc/>
    public async Task SendUiAsync(
        string? portLabel, string local, string remote, ReadOnlyMemory<byte> info, byte pid, CancellationToken ct = default)
    {
        var supervisor = host.Supervisor
            ?? throw new RhpGatewayException(RhpErrorCode.Unspecified, "Node engine is not running.");

        var ports = config.Current.Ports;
        if (ports.Count == 0)
        {
            throw new RhpGatewayException(RhpErrorCode.NoSuchPort, "No ports are configured on this node.");
        }

        if (!Callsign.TryParse(local.Trim().ToUpperInvariant(), out var source))
        {
            throw new RhpGatewayException(RhpErrorCode.InvalidLocalAddress, $"'{local}' is not a valid AX.25 callsign.");
        }
        if (!Callsign.TryParse(remote.Trim().ToUpperInvariant(), out var dest))
        {
            throw new RhpGatewayException(RhpErrorCode.InvalidRemoteAddress, $"'{remote}' is not a valid AX.25 callsign.");
        }

        // Port labels are port ids; null = the first port (connectionless UI needs a port).
        var portId = ResolvePortId(portLabel, ports);
        var port = supervisor.GetPort(portId)
            ?? throw new RhpGatewayException(RhpErrorCode.NoSuchPort, $"Port '{portId}' is not running.");

        // A UI datagram is connectionless - no session, no registration; the source-bearing
        // overload emits it verbatim as the bound callsign.
        await port.Listener.SendUiAsync(source, dest, info, pid, ct).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public IDisposable RegisterUiListener(string? portLabel, Func<UiDatagram, Task> onReceived)
    {
        ArgumentNullException.ThrowIfNull(onReceived);
        var supervisor = host.Supervisor
            ?? throw new RhpGatewayException(RhpErrorCode.Unspecified, "Node engine is not running.");

        var ports = config.Current.Ports;

        // Which port(s) to tap, with each one's id as the label surfaced on the wire.
        var targets = new List<(string PortId, string Label)>();
        if (string.IsNullOrWhiteSpace(portLabel))
        {
            for (int i = 0; i < ports.Count; i++)
            {
                targets.Add((ports[i].Id, ports[i].Id));
            }
        }
        else
        {
            var resolved = ResolvePortId(portLabel, ports);
            targets.Add((resolved, resolved));
        }

        // Tap FrameTraced on each running port's listener - the same read-only, promiscuous tap
        // NetRomService uses to hear NODES (fires before address filtering, so it hears broadcast
        // UI like APRS regardless of the frame's destination). First-cut scope: ports running NOW
        // are tapped; a port that (re)starts later is not retro-subscribed.
        var subscriptions = new List<IDisposable>();
        foreach (var (portId, label) in targets)
        {
            var port = supervisor.GetPort(portId);
            if (port is null)
            {
                continue;   // not running now
            }
            var listener = port.Listener;

            void Handler(object? _, Ax25FrameEventArgs e)
            {
                if (e.Direction != FrameDirection.Received || !e.Frame.IsUi)
                {
                    return;   // only inbound UI datagrams
                }
                var frame = e.Frame;
                var dg = new UiDatagram(
                    frame.Source.Callsign.ToString(),
                    frame.Destination.Callsign.ToString(),
                    frame.Pid ?? Ax25Frame.PidNoLayer3,
                    frame.Info.ToArray(),
                    label);
                // Fire-and-forget: the recv push writes to the client asynchronously and a fault
                // there is the server's to log; the listener pump must never block on it.
                _ = onReceived(dg);
            }

            listener.FrameTraced += Handler;
            subscriptions.Add(new ListenerUnsubscriber(listener, Handler));
        }

        return new CompositeUnsubscriber(subscriptions);
    }

    // A port is named by its id or by its number. RHPv2 names ports by number (PWP-0222's
    // OPEN example sends "port": 2), and XRouter-convention clients such as DAPPS count them
    // from 1. pdn numbers them in configuration order, the same numbers PORTS and "C <n>" use
    // (operating/10-your-ports-in-order.md). The id is pdn's addition, which pdn-bbs sends
    // (#668). An id match wins, so a port whose id is itself a number is always that port.
    private static string ResolvePortId(string? portLabel, IReadOnlyList<PortConfig> ports)
    {
        if (string.IsNullOrWhiteSpace(portLabel))
        {
            return ports[0].Id;
        }
        for (int i = 0; i < ports.Count; i++)
        {
            if (string.Equals(ports[i].Id, portLabel, StringComparison.OrdinalIgnoreCase))
            {
                return ports[i].Id;
            }
        }
        if (int.TryParse(portLabel, NumberStyles.None, CultureInfo.InvariantCulture, out var number)
            && number >= 1 && number <= ports.Count)
        {
            return ports[number - 1].Id;
        }
        throw new RhpGatewayException(RhpErrorCode.NoSuchPort,
            $"No such port '{portLabel}' (a port id, {PortList(ports)}, or a number from 1 to {ports.Count}).");
    }

    private static string PortList(IReadOnlyList<PortConfig> ports) =>
        string.Join(", ", ports.Select(p => $"'{p.Id}'"));

    // Unhooks one FrameTraced handler from one listener.
    private sealed class ListenerUnsubscriber(Ax25Listener listener, EventHandler<Ax25FrameEventArgs> handler) : IDisposable
    {
        public void Dispose() => listener.FrameTraced -= handler;
    }

    // Composes the per-port taps of one RegisterUiListener registration into one disposable.
    private sealed class CompositeUnsubscriber(List<IDisposable> subscriptions) : IDisposable
    {
        public void Dispose()
        {
            foreach (var s in subscriptions)
            {
                s.Dispose();
            }
        }
    }
}
