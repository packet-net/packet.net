using System.Globalization;

namespace Packet.Tune.Core;

/// <summary>
/// The meter end's per-burst measurement — the payload of an <c>MS</c>
/// telegram. GETRSSI (the 3.41-era TNC audio meter) is gone in firmware
/// 3.44, so the deviation meter reads these signals instead: decoded-frame
/// count vs sent, the GETALL delta of IL2P FEC-corrected bytes (register 11
/// — only meaningful in IL2P modes, so prefer mode 7 for tuning sessions),
/// the lost-ADC-sample delta (clipping = gross over-deviation), and the
/// Tait CCDI RSSI as the constant RF-path check.
/// </summary>
/// <param name="DecodedFrames">Burst frames the meter's TNC decoded.</param>
/// <param name="RequestedFrames">Burst frames the meter asked for (<c>RQ</c> n).</param>
/// <param name="FecCorrectedBytesDelta">IL2P FEC-corrected-byte delta across the
/// burst (GETALL register 11), or <c>null</c> when the firmware's GETALL reply
/// doesn't carry it / the mode is not IL2P.</param>
/// <param name="LostAdcSamplesDelta">Lost-ADC-sample delta across the burst
/// (labelled <c>LostADCSmp</c>), or <c>null</c> when unavailable. Positive =
/// the RX audio clipped — gross over-deviation.</param>
/// <param name="RssiDbm">Median Tait CCDI RSSI during the burst in dBm, or
/// <c>null</c> when no CCDI radio is attached at the meter end.</param>
public sealed record MeterReport(
    int DecodedFrames,
    int RequestedFrames,
    long? FecCorrectedBytesDelta,
    long? LostAdcSamplesDelta,
    double? RssiDbm)
{
    private const string Unavailable = "na";

    /// <summary>Decode success as a fraction (0 when nothing was requested).</summary>
    public double DecodeRate => RequestedFrames > 0 ? (double)DecodedFrames / RequestedFrames : 0;

    /// <summary>
    /// Canonical <c>MS</c> args:
    /// <c>&lt;dec&gt;/&lt;n&gt;|fec:&lt;delta&gt;|clip:&lt;delta&gt;|rssi:&lt;dbm&gt;</c>,
    /// with <c>na</c> for unavailable values.
    /// </summary>
    public string ToArgs() => string.Create(
        CultureInfo.InvariantCulture,
        $"{DecodedFrames}/{RequestedFrames}|fec:{FmtLong(FecCorrectedBytesDelta)}|clip:{FmtLong(LostAdcSamplesDelta)}|rssi:{FmtRssi(RssiDbm)}");

    /// <summary>
    /// Compact wire form for the 32-character SDM budget: single-letter keys
    /// (<c>f</c>/<c>c</c>/<c>r</c>), unavailable fields omitted entirely.
    /// E.g. <c>5/5|f0|c0|r-90.4</c>.
    /// </summary>
    public string ToCompactArgs()
    {
        string args = string.Create(CultureInfo.InvariantCulture, $"{DecodedFrames}/{RequestedFrames}");
        if (FecCorrectedBytesDelta is { } fec)
        {
            args += string.Create(CultureInfo.InvariantCulture, $"|f{fec}");
        }
        if (LostAdcSamplesDelta is { } clip)
        {
            args += string.Create(CultureInfo.InvariantCulture, $"|c{clip}");
        }
        if (RssiDbm is { } rssi)
        {
            args += string.Create(CultureInfo.InvariantCulture, $"|r{rssi:0.0}");
        }
        return args;
    }

    /// <summary>Parse <c>MS</c> args in either the canonical or the compact form.</summary>
    public static bool TryParse(string? args, out MeterReport? report)
    {
        report = null;
        if (string.IsNullOrEmpty(args))
        {
            return false;
        }

        string[] parts = args.Split('|');
        string[] counts = parts[0].Split('/');
        if (counts.Length != 2 ||
            !int.TryParse(counts[0], NumberStyles.None, CultureInfo.InvariantCulture, out int decoded) ||
            !int.TryParse(counts[1], NumberStyles.None, CultureInfo.InvariantCulture, out int requested))
        {
            return false;
        }

        long? fec = null, clip = null;
        double? rssi = null;
        foreach (string part in parts.Skip(1))
        {
            if (TryField(part, "fec:", "f", out string? fecText))
            {
                fec = ParseLong(fecText);
            }
            else if (TryField(part, "clip:", "c", out string? clipText))
            {
                clip = ParseLong(clipText);
            }
            else if (TryField(part, "rssi:", "r", out string? rssiText))
            {
                rssi = ParseDouble(rssiText);
            }
            else
            {
                return false; // unknown field — not an MS args string
            }
        }

        report = new MeterReport(decoded, requested, fec, clip, rssi);
        return true;
    }

    /// <inheritdoc/>
    public override string ToString() => ToArgs();

    private static bool TryField(string part, string canonicalKey, string compactKey, out string? value)
    {
        if (part.StartsWith(canonicalKey, StringComparison.Ordinal))
        {
            value = part[canonicalKey.Length..];
            return true;
        }
        if (part.StartsWith(compactKey, StringComparison.Ordinal))
        {
            value = part[compactKey.Length..];
            return true;
        }
        value = null;
        return false;
    }

    private static long? ParseLong(string? text) =>
        text is not null && text != Unavailable &&
        long.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out long v)
            ? v
            : null;

    private static double? ParseDouble(string? text) =>
        text is not null && text != Unavailable &&
        double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double v)
            ? v
            : null;

    private static string FmtLong(long? value) =>
        value?.ToString(CultureInfo.InvariantCulture) ?? Unavailable;

    private static string FmtRssi(double? value) =>
        value?.ToString("0.0", CultureInfo.InvariantCulture) ?? Unavailable;
}
