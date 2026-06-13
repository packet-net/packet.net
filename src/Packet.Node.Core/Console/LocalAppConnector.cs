using Packet.Core;

namespace Packet.Node.Core.Console;

/// <summary>
/// An <see cref="IOutboundConnector"/> that "dials" a callsign-SSID the node is locally
/// registered for — an app bound on its own SSID (e.g. an RHP-attached chat app) — over an
/// in-memory <see cref="LoopbackNodeConnection"/> rather than out a port. The app end is handed
/// to the registration's accept handler (the same path an over-the-air session addressed to that
/// SSID takes); the returned user end is what the console relays the inbound user against. So
/// <c>C GB7RDG-4</c> from the node prompt bridges the caller straight into the local app — no RF,
/// no second SABM.
/// </summary>
internal sealed class LocalAppConnector : IOutboundConnector
{
    private readonly Func<INodeConnection, string, Task> onAccepted;
    private readonly string callerPeerId;
    private readonly NodeTransportKind callerKind;
    private readonly string portLabel;

    /// <param name="onAccepted">The registered app's accept handler (callback, arrival-port id).</param>
    /// <param name="callerPeerId">The inbound user's peer id — the app sees the real caller.</param>
    /// <param name="callerKind">The inbound user's transport — carried onto the app-facing end.</param>
    /// <param name="portLabel">A label for the bridge (the registration's bound port, or a marker).</param>
    public LocalAppConnector(
        Func<INodeConnection, string, Task> onAccepted, string callerPeerId, NodeTransportKind callerKind, string portLabel)
    {
        this.onAccepted = onAccepted ?? throw new ArgumentNullException(nameof(onAccepted));
        this.callerPeerId = callerPeerId;
        this.callerKind = callerKind;
        this.portLabel = portLabel ?? throw new ArgumentNullException(nameof(portLabel));
    }

    public string PortId => portLabel;

    public Task<INodeConnection> ConnectAsync(Callsign target, CancellationToken cancellationToken = default)
    {
        // appEnd carries the caller's identity (so the app's ACCEPT names the human who dialled);
        // userEnd is labelled with the app SSID for console messages.
        var (appEnd, userEnd) = LoopbackNodeConnection.CreatePair(
            appPeerId: callerPeerId, appKind: callerKind,
            userPeerId: target.ToString(), userKind: callerKind);

        // Hand the app end to the registered app. It owns appEnd from here (the RHP server pumps
        // it against the app's handle and disposes it when the handle closes). We don't await the
        // handler — the console drives the session via the user end — but we observe it so a
        // handler fault never surfaces as an unobserved task exception, and we always dispose the
        // app end when the handler returns (closes the loopback if the app side ended first).
        _ = RunAppAsync(appEnd);
        return Task.FromResult(userEnd);
    }

    private async Task RunAppAsync(INodeConnection appEnd)
    {
        try
        {
            await onAccepted(appEnd, portLabel).ConfigureAwait(false);
        }
        catch
        {
            // The app handler owns its own error reporting; a fault here just ends the bridge.
        }
        finally
        {
            await appEnd.DisposeAsync().ConfigureAwait(false);
        }
    }
}
