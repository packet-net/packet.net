using Packet.Ax25.Transport;

namespace Packet.Radio;

/// <summary>
/// Bridges a radio control channel's hardware carrier-sense (<see cref="IRadioControl.ChannelBusy"/>)
/// onto the neutral <see cref="ICarrierSense"/> seam the AX.25 link-multiplexer's native CSMA
/// gate consults. This is how a radio-attached port feeds real DCD into the stack's
/// medium-access layer: the node constructs one over the port's open <see cref="IRadioControl"/>
/// (when it advertises <see cref="RadioCapabilities.CarrierSense"/>) and injects it into the
/// listener via the parity-tracked <c>Ax25ListenerOptions.CarrierSense</c> member.
/// </summary>
/// <remarks>
/// A pure read-through — it forwards the radio's live <see cref="IRadioControl.ChannelBusy"/>
/// and owns nothing (the caller keeps and disposes the radio). Before the first DCD report (or
/// on a radio that cannot sense carrier) <c>ChannelBusy</c> is <c>null</c>, which the gate
/// treats as clear (fail-open).
/// </remarks>
public sealed class RadioCarrierSense : ICarrierSense
{
    private readonly IRadioControl radio;

    /// <summary>
    /// Wrap <paramref name="radio"/>, exposing its carrier-sense to the AX.25 medium-access
    /// gate. The radio should advertise <see cref="RadioCapabilities.CarrierSense"/>; one that
    /// does not simply reports <c>ChannelBusy == null</c> forever, which fails open (no CSMA).
    /// Ownership of the radio stays with the caller.
    /// </summary>
    public RadioCarrierSense(IRadioControl radio)
    {
        ArgumentNullException.ThrowIfNull(radio);
        this.radio = radio;
    }

    /// <inheritdoc/>
    public bool? ChannelBusy => radio.ChannelBusy;
}
