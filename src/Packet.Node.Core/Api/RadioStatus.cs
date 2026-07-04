namespace Packet.Node.Core.Api;

/// <summary>
/// The live status of one port's radio-control attachment — the read model behind
/// <c>GET /api/v1/radios</c> and <c>GET /api/v1/ports/{id}/radio</c>. Present for every port that
/// has a <c>radio:</c> block: <see cref="Attached"/> distinguishes a radio the node has open and is
/// polling (<c>true</c>) from one that is configured but not currently attached (<c>false</c> — the
/// port isn't running, or the radio failed to open and the port degraded to running without it).
/// System.Text.Json's web defaults camel-case the properties.
/// </summary>
/// <param name="PortId">The node port this radio is (or would be) attached to.</param>
/// <param name="Attached"><c>true</c> when the radio control channel is open and being polled;
/// <c>false</c> for a configured-but-not-attached radio (degraded or port down).</param>
/// <param name="Kind">The radio-control kind — currently <c>tait-ccdi</c>.</param>
/// <param name="ControlPort">The serial device the control channel is on: the resolved
/// <c>/dev/ttyUSB*</c> when attached (which, for a serial-bound radio, is the path discovery
/// matched), or the configured device when not. <c>null</c> when bound only by serial and not
/// attached.</param>
/// <param name="Serial">The radio's CCDI serial number — from config when serial-bound, else from
/// the radio's own identity once queried, else <c>null</c>.</param>
/// <param name="Identity">The radio's self-reported identity (model + CCDI version), or <c>null</c>
/// before it has answered an identity query (or when not attached).</param>
/// <param name="ConnectionState">The control-link health: <c>healthy</c> (answering),
/// <c>faulted</c> (serial link dead / unresponsive), or <c>unknown</c> (not attached, or a radio
/// kind that doesn't track it).</param>
/// <param name="ChannelBusy">Last known hardware carrier-sense (DCD): <c>true</c> = RF on channel,
/// <c>false</c> = idle, <c>null</c> = not reported yet / unavailable.</param>
/// <param name="Health">The latest radio-health sample projection, or <c>null</c> when no health
/// sampling is available (not attached, or a non-Tait radio) or none has landed yet.</param>
public sealed record RadioStatus(
    string PortId,
    bool Attached,
    string Kind,
    string? ControlPort,
    string? Serial,
    RadioIdentity? Identity,
    string ConnectionState,
    bool? ChannelBusy,
    RadioHealth? Health);

/// <summary>A radio's self-reported identity, from its CCDI MODEL / versions queries.</summary>
/// <param name="Model">Friendly product name (e.g. <c>Tait TM8110</c>).</param>
/// <param name="CcdiVersion">The CCDI protocol version string the radio reports.</param>
public sealed record RadioIdentity(string Model, string CcdiVersion);

/// <summary>
/// The latest radio-health sample, projected for the operator surface. RSSI is read only while the
/// receiver is un-muted (i.e. not transmitting); the forward/reverse figures are populated only on
/// transmit samples.
/// </summary>
/// <param name="RssiDbm">The radio's own most-recent sliding-average RSSI (dBm), or <c>null</c>
/// (transmitting, or not yet sampled).</param>
/// <param name="AveragedRssiDbm">The median RSSI over the health monitor's rolling window (dBm) — a
/// smoothed figure less jumpy than the single reading; <c>null</c> until the window has an RX
/// sample.</param>
/// <param name="PaTemperatureC">Power-amplifier temperature (°C), or <c>null</c> when unavailable
/// (TM8200 reports only an ADC value).</param>
/// <param name="ForwardTrendMillivolts">Offset-corrected forward-power detector reading (mV) —
/// populated only on transmit samples. <b>A per-station TREND, not a power measurement.</b></param>
/// <param name="ReverseTrendMillivolts">Offset-corrected reverse-power detector reading (mV) —
/// transmit samples only. <b>A TREND, not a power measurement.</b></param>
/// <param name="ReverseForwardRatio">Offset-corrected reverse/forward detector ratio — transmit
/// samples only. <b>This is a TREND, never VSWR</b>: the detectors are uncalibrated, √P-scaled, and
/// only service-specified at High power — alert on a station's <i>change</i>, never on the absolute
/// value. See <c>TaitRadioHealthMonitor</c>.</param>
/// <param name="SampleAt">When the sample was taken, or <c>null</c> when none yet.</param>
public sealed record RadioHealth(
    float? RssiDbm,
    float? AveragedRssiDbm,
    int? PaTemperatureC,
    int? ForwardTrendMillivolts,
    int? ReverseTrendMillivolts,
    double? ReverseForwardRatio,
    DateTimeOffset? SampleAt);
