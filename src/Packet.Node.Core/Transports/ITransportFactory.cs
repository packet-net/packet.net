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
    /// <exception cref="NotSupportedException">If the transport kind has no
    /// <see cref="IKissModem"/> implementation in this build (e.g. AXUDP).</exception>
    Task<IKissModem> CreateAsync(TransportConfig transport, CancellationToken cancellationToken = default);
}
