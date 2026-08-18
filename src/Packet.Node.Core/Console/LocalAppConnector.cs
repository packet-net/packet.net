using Packet.Core;

namespace Packet.Node.Core.Console;

/// <summary>
/// An <see cref="IOutboundConnector"/> that "dials" a callsign-SSID the node is locally
/// registered for - an app bound on its own SSID (e.g. an RHP-attached chat app) - over an
/// in-memory <see cref="LoopbackNodeConnection"/> rather than out a port. The app end is handed
/// to the registration's accept handler (the same path an over-the-air session addressed to that
/// SSID takes); the returned user end is what the console relays the inbound user against. So
/// <c>C GB7RDG-4</c> from the node prompt bridges the caller straight into the local app - no RF,
/// no second SABM.
/// </summary>
internal sealed class LocalAppConnector : IOutboundConnector
{
    private readonly Func<INodeConnection, string, Task> onAccepted;
    private readonly string callerPeerId;
    private readonly NodeTransportKind callerKind;
    private readonly string portLabel;

    /// <param name="onAccepted">The registered app's accept handler (callback, arrival-port id).</param>
    /// <param name="callerPeerId">The inbound user's peer id - the app sees the real caller.</param>
    /// <param name="callerKind">The inbound user's transport - carried onto the app-facing end.</param>
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

        // Hand the app end to the registered app's accept handler. From here the handler OWNS
        // appEnd, exactly as the over-the-air inbound path does (PortSupervisor.OnAppSessionAccepted
        // - "the handler owns the connection from here"): the RHPv2 server's accept handler pushes
        // ACCEPT and then pumps appEnd against the app's child handle in the BACKGROUND, returning
        // immediately. We observe the handler so a fault never becomes an unobserved task exception.
        _ = RunAppAsync(appEnd);
        return Task.FromResult(userEnd);
    }

    private async Task RunAppAsync(INodeConnection appEnd)
    {
        try
        {
            await onAccepted(appEnd, portLabel).ConfigureAwait(false);
            // Do NOT dispose appEnd here. onAccepted returns once the app has TAKEN OWNERSHIP
            // (its background pump now drives appEnd and disposes it when the child handle
            // closes) - NOT when the bridged session ends. Disposing on this (immediate) return
            // tore the loopback down the instant ACCEPT was pushed - Connected → immediately
            // Disconnected, no data bridged. Teardown flows through the shared loopback instead:
            // when the caller drops, the console disposes userEnd, which EOFs appEnd so the app's
            // pump closes; when the app closes its handle, the pump disposes appEnd, EOFing userEnd
            // so the console relay ends.
        }
        catch
        {
            // The handler faulted before taking ownership - nothing else will dispose appEnd, so
            // we must (this also unblocks the user end with EOF). DisposeAsync is idempotent, so a
            // handler that already disposed it (e.g. refused the accept) is harmless.
            await appEnd.DisposeAsync().ConfigureAwait(false);
        }
    }
}
