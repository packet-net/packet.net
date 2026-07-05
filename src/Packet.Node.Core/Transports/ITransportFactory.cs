using Packet.Ax25.Transport;
using Packet.Node.Core.Configuration;
using Packet.Node.Core.HeadEnd;

namespace Packet.Node.Core.Transports;

/// <summary>
/// Builds a live <see cref="IAx25Transport"/> from a <see cref="TransportConfig"/>.
/// The seam between config and hardware/network: the port supervisor calls this
/// to bring a port up, and only this class knows how each transport kind maps
/// onto a concrete transport.
/// </summary>
public interface ITransportFactory
{
    /// <summary>
    /// Open the transport described by <paramref name="transport"/>. For serial
    /// transports this opens the port; for KISS-TCP it dials the endpoint; for a
    /// NinoTNC it opens the port and applies the configured mode. Every transport
    /// kind — the KISS-speaking ones and AXUDP alike — implements
    /// <see cref="IAx25Transport"/> natively, so the factory uniformly returns
    /// <see cref="IAx25Transport"/>.
    /// </summary>
    /// <param name="transport">The transport to open.</param>
    /// <param name="timeProvider">
    /// Clock for any transport-internal timers (e.g. the KISS-TCP read-idle
    /// liveness timeout — #464) and inbound-frame timestamping. Null uses the system
    /// clock; the port supervisor threads its own clock through so component tests
    /// stay deterministic.
    /// </param>
    /// <param name="headEndResolver">Resolves a <c>nino-tnc-tcp</c> transport's
    /// <c>(headEndId, deviceId)</c> to the head-end's raw TCP pipe (split-station topology).
    /// Required for a head-end transport; null (or a local/kiss-tcp/AXUDP transport) ignores it.</param>
    /// <param name="cancellationToken">Cancels the open/connect.</param>
    /// <exception cref="NotSupportedException">If the transport kind has no
    /// implementation in this build.</exception>
    Task<IAx25Transport> CreateAsync(
        TransportConfig transport,
        TimeProvider? timeProvider = null,
        HeadEndDeviceResolver? headEndResolver = null,
        CancellationToken cancellationToken = default);
}
