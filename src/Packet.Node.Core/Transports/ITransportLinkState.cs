namespace Packet.Node.Core.Transports;

/// <summary>
/// Live link-state exposure for a self-healing transport decorator
/// (<see cref="ReconnectingKissModem"/>): whether the underlying link is currently down and being
/// re-dialled. Read-only observability seam (#583) — <c>pdn_port_up</c> reads "up" while the
/// reconnect wrapper is quietly re-dialling a dead far end, so the metrics exporter reflects this
/// as <c>pdn_port_transport_reconnecting{port}</c> instead. Captured on
/// <see cref="Hosting.RunningPort.LinkState"/> at wire-up time (before later decorators hide the
/// wrapper), the same pre-decorator capture pattern as <see cref="Hosting.RunningPort.NinoTnc"/>.
/// </summary>
public interface ITransportLinkState
{
    /// <summary>True while the transport's link is down and the wrapper is re-dialling (backoff
    /// loop included); false while an established link is being pumped.</summary>
    bool IsReconnecting { get; }
}
