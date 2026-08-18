namespace Packet.Aprs;

/// <summary>
/// A decoded APRS telemetry report (DTI <c>T</c>) per APRS101 §13.
/// </summary>
/// <remarks>
/// <para>
/// Per spec §13: <c>T#xxx,aaa,aaa,aaa,aaa,aaa,bbbbbbbb[,comment]</c>
/// - sequence number, five 8-bit unsigned analog values 000-255, and
/// an 8-bit binary value (8 ASCII <c>0</c>/<c>1</c> bytes), plus
/// optional trailing comment.
/// </para>
/// <para>
/// Real-world telemetry frames diverge from spec in two common ways:
/// </para>
/// <list type="bullet">
///   <item>
///     Analog values are not always 3-character zero-padded integers;
///     stations emit <c>0</c>, <c>184</c>, <c>3.2</c>, etc.
///     Floating-point and variable-width forms are accepted.
///   </item>
///   <item>
///     Trailing comment frequently present (<c>SimplexLogic</c>,
///     <c>RepeaterLogic</c>, station ID).
///   </item>
/// </list>
/// <para>
/// Per Postel we decode the as-found shape and surface whatever the
/// station actually sent. <see cref="AnalogValues"/> is exposed as
/// <see cref="double"/> to cover both integer and float forms.
/// </para>
/// </remarks>
/// <param name="Sequence">
/// Sequence number - typically 3 digits (e.g. <c>"005"</c>) or the
/// literal <c>"MIC"</c> for MIM stations.
/// </param>
/// <param name="AnalogValues">Five analog channel values.</param>
/// <param name="DigitalBits">Eight digital channel bits (LSB-first index).</param>
/// <param name="Comment">Free-form trailing text. May be empty.</param>
public readonly record struct AprsTelemetry(
    string Sequence,
    IReadOnlyList<double> AnalogValues,
    IReadOnlyList<bool> DigitalBits,
    string Comment);
