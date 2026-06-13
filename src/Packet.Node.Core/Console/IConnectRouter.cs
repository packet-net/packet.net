using Packet.Core;

namespace Packet.Node.Core.Console;

/// <summary>
/// Resolves a console <c>C[onnect] [port] &lt;callsign-ssid&gt;</c> to the right way out:
/// a <b>local crossconnect</b> to an application the node is registered for (e.g. an
/// RHP-attached chat app on its own SSID), or an <b>outbound dial</b> on a chosen — or the
/// default — port. This is the layer the connect command grew when "dial the one default
/// port" was no longer enough; the same routing the inbound listener already does for a
/// session addressed straight to an app SSID, made available from the prompt.
/// </summary>
/// <remarks>
/// Resolution rules (NET/ROM deliberately out of scope here — that adds aliases later):
/// <list type="bullet">
/// <item>No port given, target is a registered app SSID → loopback crossconnect to the app.</item>
/// <item>No port given, target is anything else → the session's default connector (first port).</item>
/// <item>An explicit port (1-indexed config order) → a direct dial out that port, even if the
/// target is also a local app — an explicit port is an explicit "go to RF".</item>
/// </list>
/// </remarks>
public interface IConnectRouter
{
    /// <summary>
    /// Resolve <paramref name="target"/> with an optional 1-indexed <paramref name="port"/>.
    /// <paramref name="inbound"/> is the connected user, so a local crossconnect can present the
    /// real caller's callsign to the app. Never throws; a bad port / no-route is a
    /// <see cref="ConnectResolution.Fail"/> the console reports back to the user.
    /// </summary>
    ConnectResolution Resolve(int? port, Callsign target, INodeConnection inbound);
}

/// <summary>The outcome of <see cref="IConnectRouter.Resolve"/>: a connector to dial, or a
/// human-readable failure. <see cref="IsLocalApp"/> flags the loopback-crossconnect case so the
/// console can word its messages accordingly.</summary>
public sealed class ConnectResolution
{
    private ConnectResolution(IOutboundConnector? connector, string? error, bool isLocalApp)
    {
        Connector = connector;
        Error = error;
        IsLocalApp = isLocalApp;
    }

    /// <summary>The connector to dial through. Null exactly when <see cref="Failed"/>.</summary>
    public IOutboundConnector? Connector { get; }

    /// <summary>The failure reason to show the user, or null on success.</summary>
    public string? Error { get; }

    /// <summary>True when the resolution is a local crossconnect to a registered app.</summary>
    public bool IsLocalApp { get; }

    /// <summary>Whether resolution failed (no such port, no route, nothing to dial).</summary>
    public bool Failed => Error is not null;

    public static ConnectResolution Dial(IOutboundConnector connector) => new(connector, null, isLocalApp: false);

    public static ConnectResolution LocalApp(IOutboundConnector connector) => new(connector, null, isLocalApp: true);

    public static ConnectResolution Fail(string error) => new(null, error, isLocalApp: false);
}
