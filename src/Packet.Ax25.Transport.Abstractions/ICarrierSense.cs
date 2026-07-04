namespace Packet.Ax25.Transport;

/// <summary>
/// Optional medium-access capability: a source of hardware carrier-sense (DCD) that the
/// AX.25 link-multiplexer consults before it keys the radio. This is the neutral,
/// dependency-free seam the native CSMA gate reads — "is the channel busy right now?" —
/// so the stack can defer a keyup while another station is transmitting, emulating a TNC
/// that exposes DCD.
/// </summary>
/// <remarks>
/// <para>
/// A facet, not core: only a transport (or a radio-control bridge) that can genuinely
/// observe channel occupancy implements it. A radio-control channel exposes hardware DCD
/// (which leads the modem's decoded output by the whole preamble + frame, the reason it
/// is worth gating on); a future KISS DCD extension would expose the same signal at the
/// transport. A consumer that has neither simply does not supply one, and the gate treats
/// the absent source as always-clear (fail-open) — traffic never stops because telemetry
/// is missing.
/// </para>
/// <para>
/// Kept separate from <see cref="IAx25Transport"/> deliberately: the carrier-sense source
/// need not be the frame transport (a radio-control channel is a different device from the
/// KISS modem), so this does not extend the transport interface. Consumers inject it — or
/// feature-detect it on a transport with <c>transport is ICarrierSense</c>.
/// </para>
/// </remarks>
public interface ICarrierSense
{
    /// <summary>
    /// Last known carrier-sense state: <c>true</c> while the channel is busy (RF on channel
    /// / hardware DCD asserted), <c>false</c> when idle, and <c>null</c> when unknown (no
    /// report yet, or the source cannot sense carrier). The gate treats anything other than
    /// a definite <c>true</c> as clear, so an unknown state fails open rather than wedging
    /// transmission.
    /// </summary>
    bool? ChannelBusy { get; }
}
