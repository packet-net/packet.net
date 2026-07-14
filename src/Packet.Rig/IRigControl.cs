namespace Packet.Rig;

/// <summary>
/// Station-rig (CAT) control: frequency, mode, transmitter keying, and TX-side metering for the
/// kind of radio an operator tunes — an HF/all-mode transceiver behind hamlib's <c>rigctld</c>,
/// flrig, or a native CAT driver. The contract is the cross-backend common subset
/// ({frequency get/set, mode get/set, PTT, SWR / RF-power meters}); everything is
/// capability-probed via <see cref="Capabilities"/> because every backend, and every rig behind
/// a backend, supports a different slice.
/// </summary>
/// <remarks>
/// <para>
/// <b>Relationship to <c>Packet.Radio.IRadioControl</c>.</b> That interface is the
/// packet-medium seam — RSSI, hardware carrier-sense and PTT for CSMA on a channelised PMR
/// radio (Tait CCDI). This one is the <em>station-control</em> seam: QSY, mode selection and
/// transmit-health monitoring for CAT-controllable transceivers. They deliberately share the
/// capability-flag pattern (plan OQ-011); a bridge that surfaces an <see cref="IRigControl"/>
/// rig's PTT/DCD to the packet stack is future node-side work, not part of this abstraction.
/// </para>
/// <para>
/// <b>Threading.</b> Implementations serialise commands internally — callers may issue
/// concurrent calls, which queue in arrival order. Members for capabilities the backend lacks
/// throw <see cref="NotSupportedException"/> (same discipline as <c>IRadioControl</c>).
/// </para>
/// <para>
/// <b>Polling.</b> All current backends (rigctld, flrig) are poll-only — there is no
/// push/notification channel in this abstraction. Callers own their polling cadence.
/// </para>
/// </remarks>
public interface IRigControl : IAsyncDisposable
{
    /// <summary>What this rig's control channel actually supports, discovered at connect time.
    /// Callers must feature-probe before using the corresponding members.</summary>
    RigCapabilities Capabilities { get; }

    /// <summary>Identity of the backend and the rig behind it, for diagnostics/UX.</summary>
    RigInfo Info { get; }

    /// <summary>Read the current-VFO frequency in Hz.</summary>
    ValueTask<long> GetFrequencyAsync(CancellationToken cancellationToken = default);

    /// <summary>Tune the current VFO to <paramref name="frequencyHz"/> (Hz).</summary>
    ValueTask SetFrequencyAsync(long frequencyHz, CancellationToken cancellationToken = default);

    /// <summary>Read the current-VFO operating mode (and passband width where the backend
    /// reports one).</summary>
    ValueTask<RigModeState> GetModeAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Set the current-VFO operating mode. <paramref name="passbandHz"/> <c>null</c> selects the
    /// rig's default passband for the mode (the cross-backend uniform semantics — flrig cannot
    /// set widths at all); pass an explicit width to override where supported.
    /// </summary>
    ValueTask SetModeAsync(RigMode mode, int? passbandHz = null, CancellationToken cancellationToken = default);

    /// <summary>Read whether the transmitter is currently keyed.</summary>
    ValueTask<bool> GetPttAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Key or unkey the transmitter. Implementations must guarantee best-effort unkey on dispose
    /// — a rig latched in TX is a station incident (same contract as <c>IRadioControl</c>).
    /// </summary>
    ValueTask SetPttAsync(bool transmit, CancellationToken cancellationToken = default);

    /// <summary>
    /// Read the SWR meter as a dimensionless ratio (1.0 = perfect match). Meaningful while
    /// transmitting; most rigs report 0 or 1.0 when idle.
    /// </summary>
    ValueTask<double> ReadSwrAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Read the RF power-output meter as a fraction of full scale (0.0–1.0). Meaningful while
    /// transmitting. Prefer <see cref="ReadRfPowerWattsAsync"/> when
    /// <see cref="RigCapabilities.RfPowerMeterWatts"/> is available.
    /// </summary>
    ValueTask<double> ReadRfPowerAsync(CancellationToken cancellationToken = default);

    /// <summary>Read the RF power-output meter in watts (backends that can calibrate;
    /// hamlib ≥ 4.4 exposes this as <c>RFPOWER_METER_WATTS</c>).</summary>
    ValueTask<double> ReadRfPowerWattsAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Feature flags for <see cref="IRigControl.Capabilities"/>. Get/set are split because CAT
/// surfaces genuinely diverge (read-only rig interfaces exist, and e.g. hamlib's caps table
/// tracks each direction independently).
/// </summary>
[Flags]
public enum RigCapabilities
{
    /// <summary>No control-channel features available.</summary>
    None = 0,

    /// <summary><see cref="IRigControl.GetFrequencyAsync"/> works.</summary>
    FrequencyGet = 1 << 0,

    /// <summary><see cref="IRigControl.SetFrequencyAsync"/> works.</summary>
    FrequencySet = 1 << 1,

    /// <summary><see cref="IRigControl.GetModeAsync"/> works.</summary>
    ModeGet = 1 << 2,

    /// <summary><see cref="IRigControl.SetModeAsync"/> works.</summary>
    ModeSet = 1 << 3,

    /// <summary><see cref="IRigControl.GetPttAsync"/> works.</summary>
    PttGet = 1 << 4,

    /// <summary><see cref="IRigControl.SetPttAsync"/> works.</summary>
    PttSet = 1 << 5,

    /// <summary><see cref="IRigControl.ReadSwrAsync"/> works.</summary>
    SwrMeter = 1 << 6,

    /// <summary><see cref="IRigControl.ReadRfPowerAsync"/> works (relative 0–1 meter).</summary>
    RfPowerMeter = 1 << 7,

    /// <summary><see cref="IRigControl.ReadRfPowerWattsAsync"/> works (calibrated watts).</summary>
    RfPowerMeterWatts = 1 << 8,
}

/// <summary>Identity of a connected rig: which backend is in the path and what it says the
/// radio is. Purely informational — never dispatch behaviour on these strings.</summary>
/// <param name="Backend">The control backend, e.g. <c>"Hamlib rigctld"</c> or <c>"flrig"</c>.</param>
/// <param name="Manufacturer">Rig manufacturer as the backend reports it, when known.</param>
/// <param name="Model">Rig model as the backend reports it, when known.</param>
public sealed record RigInfo(string Backend, string? Manufacturer, string? Model);
