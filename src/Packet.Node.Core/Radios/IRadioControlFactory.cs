using Packet.Node.Core.Configuration;
using Packet.Radio;

namespace Packet.Node.Core.Radios;

/// <summary>
/// Builds a live <see cref="IRadioControl"/> from a port's <see cref="PortRadioConfig"/> —
/// the radio-side sibling of <see cref="Transports.ITransportFactory"/>. The port
/// supervisor calls this when a port with a <c>radio:</c> block comes up, and only
/// this seam knows how each radio kind maps onto a concrete driver (which lets
/// component tests substitute a scripted radio instead of opening real hardware).
/// </summary>
public interface IRadioControlFactory
{
    /// <summary>
    /// Open the radio-control channel described by <paramref name="radio"/> and put it
    /// in the state the RSSI-tagging pipeline needs (for Tait CCDI: progress/DCD
    /// reporting enabled). The returned radio is ready to sample; the caller owns its
    /// disposal.
    /// </summary>
    /// <param name="radio">The validated per-port radio attachment config.</param>
    /// <param name="timeProvider">Clock for driver-internal timers (keep-alive
    /// watchdog, edge timestamps). Null uses the system clock.</param>
    /// <param name="cancellationToken">Cancels the open/handshake.</param>
    /// <exception cref="NotSupportedException">If the radio kind has no
    /// implementation in this build (unreachable for validated config — the
    /// validator and factory share <see cref="RadioKinds"/>).</exception>
    Task<IRadioControl> CreateAsync(
        PortRadioConfig radio,
        TimeProvider? timeProvider = null,
        CancellationToken cancellationToken = default);
}
