namespace Packet.Radio;

/// <summary>
/// A control/telemetry channel to the radio behind a modem — the seam OQ-011 asked for.
/// The contract is the *common subset* a CAT-style serial protocol can realistically offer
/// (RSSI read, carrier-sense, transmitter keying); anything richer is discovered through
/// <see cref="Capabilities"/> and exposed on the concrete driver (e.g. Tait CCDI's PA
/// temperature or forward/reverse power live on <c>TaitCcdiRadio</c>, not here).
/// </summary>
/// <remarks>
/// Experimental (Phase 10 spike): the shape follows plan OQ-011's proposed common subset
/// {RSSI-get, busy-get, PTT-set} — frequency/channel control is deliberately deferred until a
/// second implementation (Yaesu CAT / ICOM CI-V) exists to test the abstraction against.
/// </remarks>
public interface IRadioControl : IAsyncDisposable
{
    /// <summary>What this radio's control channel actually supports. Callers must
    /// feature-probe before using the corresponding members.</summary>
    RadioCapabilities Capabilities { get; }

    /// <summary>
    /// Read the receiver's current RSSI in dBm (instantaneous, not averaged — suitable for
    /// per-frame attribution). Throws <see cref="NotSupportedException"/> when
    /// <see cref="RadioCapabilities.RssiRead"/> is absent.
    /// </summary>
    ValueTask<float> ReadRssiDbmAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Key or unkey the transmitter. Throws <see cref="NotSupportedException"/> when
    /// <see cref="RadioCapabilities.TransmitterControl"/> is absent. Drivers must guarantee
    /// best-effort unkey on dispose — a radio latched in TX is a site incident.
    /// </summary>
    ValueTask SetTransmitterAsync(bool transmit, CancellationToken cancellationToken = default);

    /// <summary>
    /// Last known carrier-sense state: <c>true</c> while the receiver reports RF on channel
    /// (hardware DCD), <c>false</c> when idle, <c>null</c> before the first report (or when
    /// <see cref="RadioCapabilities.CarrierSense"/> is absent).
    /// </summary>
    bool? ChannelBusy { get; }

    /// <summary>
    /// Fired on every carrier-sense edge the radio reports. This is a *hardware* data-carrier
    /// detect — it leads the modem's decoded frame by the whole preamble + frame duration, which
    /// is what makes it valuable for CSMA. Subscribers run on the driver's read pump: keep
    /// handlers fast and non-blocking.
    /// </summary>
    event EventHandler<CarrierSenseChange>? CarrierSenseChanged;
}

/// <summary>One hardware carrier-sense (DCD) edge.</summary>
/// <param name="Busy"><c>true</c> = RF appeared on channel; <c>false</c> = channel went quiet.</param>
/// <param name="At">When the radio reported the edge, stamped from the driver's clock.</param>
public readonly record struct CarrierSenseChange(bool Busy, DateTimeOffset At);

/// <summary>
/// Feature flags for <see cref="IRadioControl.Capabilities"/>. Flags with no interface member
/// yet (<see cref="ChannelChange"/>, <see cref="FrequencyControl"/>, <see cref="TxPowerControl"/>)
/// are reserved so drivers can advertise them ahead of the abstraction growing those members.
/// </summary>
[Flags]
public enum RadioCapabilities
{
    /// <summary>No control-channel features available.</summary>
    None = 0,

    /// <summary><see cref="IRadioControl.ReadRssiDbmAsync"/> works.</summary>
    RssiRead = 1 << 0,

    /// <summary><see cref="IRadioControl.CarrierSenseChanged"/> fires and
    /// <see cref="IRadioControl.ChannelBusy"/> is maintained.</summary>
    CarrierSense = 1 << 1,

    /// <summary><see cref="IRadioControl.SetTransmitterAsync"/> works.</summary>
    TransmitterControl = 1 << 2,

    /// <summary>Reserved: the radio can switch among programmed channels.</summary>
    ChannelChange = 1 << 3,

    /// <summary>Reserved: the radio accepts direct frequency programming.</summary>
    FrequencyControl = 1 << 4,

    /// <summary>Reserved: the radio accepts TX power-level changes.</summary>
    TxPowerControl = 1 << 5,

    /// <summary>
    /// The driver can supply an <see cref="IRadioSideChannel"/> (radio-native small-datagram
    /// messaging, e.g. Tait SDM). Advertises the <em>machinery</em> only — whether the feature
    /// is enabled in the radio's programming needs a live probe; see the capability-gating
    /// remarks on <see cref="IRadioSideChannel"/>.
    /// </summary>
    SideChannel = 1 << 6,
}
