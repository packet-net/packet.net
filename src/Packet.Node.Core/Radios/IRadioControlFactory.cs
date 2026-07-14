using Packet.Node.Core.Configuration;
using Packet.Node.Core.HeadEnd;
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
    /// reporting enabled; for kind <c>rig</c>: a dedicated rig connection wrapped in
    /// the rig→radio bridge). The returned radio is ready to sample; the caller owns
    /// its disposal.
    /// </summary>
    /// <param name="radio">The validated per-port radio attachment config.</param>
    /// <param name="timeProvider">Clock for driver-internal timers (keep-alive
    /// watchdog, edge timestamps). Null uses the system clock.</param>
    /// <param name="headEndResolver">Resolves a head-end-bound radio's
    /// <c>(headEndId, deviceId)</c> to a raw TCP pipe + line-control seam (split-station topology).
    /// Required for a head-end-bound radio; null (or a local-serial radio) uses the local open path.</param>
    /// <param name="rig">The port's <c>rig:</c> block — which CAT daemon a kind-<c>rig</c> radio
    /// dials its dedicated connection to. Required for kind <c>rig</c> (the validator guarantees
    /// the block exists on the same port); ignored for every other kind.</param>
    /// <param name="cancellationToken">Cancels the open/handshake.</param>
    /// <exception cref="NotSupportedException">If the radio kind has no
    /// implementation in this build (unreachable for validated config — the
    /// validator and factory share <see cref="RadioKinds"/>).</exception>
    Task<IRadioControl> CreateAsync(
        PortRadioConfig radio,
        TimeProvider? timeProvider = null,
        HeadEndDeviceResolver? headEndResolver = null,
        PortRigConfig? rig = null,
        CancellationToken cancellationToken = default);
}
