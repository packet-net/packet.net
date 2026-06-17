using Packet.Kiss;
using Packet.Node.Core.Configuration;

namespace Packet.Node.Core.Transports;

/// <summary>
/// Builds a live <see cref="IKissModem"/> from a <see cref="TransportConfig"/>.
/// The seam between config and hardware/network: the port supervisor calls this
/// to bring a port up, and only this class knows how each transport kind maps
/// onto a concrete modem.
/// </summary>
public interface ITransportFactory
{
    /// <summary>
    /// Open the modem described by <paramref name="transport"/>. For serial
    /// transports this opens the port; for KISS-TCP it dials the endpoint; for a
    /// NinoTNC it opens the port and applies the configured mode.
    /// </summary>
    /// <param name="transport">The transport to open.</param>
    /// <param name="timeProvider">
    /// Clock for any transport-internal timers (e.g. the KISS-TCP read-idle
    /// liveness timeout — #464). Null uses the system clock; the port supervisor
    /// threads its own clock through so component tests stay deterministic.
    /// </param>
    /// <param name="cancellationToken">Cancels the open/connect.</param>
    /// <exception cref="NotSupportedException">If the transport kind has no
    /// <see cref="IKissModem"/> implementation in this build (e.g. AXUDP).</exception>
    Task<IKissModem> CreateAsync(
        TransportConfig transport,
        TimeProvider? timeProvider = null,
        CancellationToken cancellationToken = default);
}
