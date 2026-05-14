using System.Globalization;
using System.Text;

namespace Packet.Aprs;

/// <summary>
/// Decode an APRS telemetry report (DTI <c>T</c>) per APRS101 §13.
/// </summary>
/// <remarks>
/// <para>
/// On-wire format:
/// <code>
///   T#xxx,aaa,aaa,aaa,aaa,aaa,bbbbbbbb[,comment]
/// </code>
/// where <c>xxx</c> is a 3-char sequence number (digits or <c>MIC</c>),
/// the five <c>aaa</c>s are analog values 0–255, and <c>bbbbbbbb</c>
/// is an 8-bit digital value as ASCII 0/1.
/// </para>
/// <para>
/// We decode permissively because the corpus diverges from spec:
/// analog values may be variable-width integers or floating-point;
/// the leading comma after the sequence number is optional for
/// <c>MIC</c>-style packets per spec.
/// </para>
/// </remarks>
public static class AprsTelemetryDecoder
{
    private const int AnalogChannelCount = 5;
    private const int DigitalBitCount = 8;

    /// <summary>
    /// Try to decode an APRS telemetry report from <paramref name="info"/>,
    /// using the default lenient parser options.
    /// </summary>
    public static bool TryDecode(ReadOnlySpan<byte> info, out AprsTelemetry telemetry)
        => TryDecode(info, AprsParseOptions.Lenient, out telemetry);

    /// <summary>
    /// Try to decode an APRS telemetry report from <paramref name="info"/>,
    /// applying the supplied <see cref="AprsParseOptions"/>.
    /// </summary>
    /// <param name="info">Info bytes, optionally prefixed with the DTI byte <c>T</c>.</param>
    /// <param name="options">Strict-vs-lenient parser knobs.</param>
    /// <param name="telemetry">On success, the decoded telemetry.</param>
    /// <remarks>
    /// Strict mode enforces APRS101 §13's "3-digit decimal numbers in
    /// the range 000–255" for each analog channel — no variable-width
    /// integers, no floating-point.
    /// </remarks>
    public static bool TryDecode(ReadOnlySpan<byte> info, AprsParseOptions options, out AprsTelemetry telemetry)
    {
        ArgumentNullException.ThrowIfNull(options);
        telemetry = default;
        if (info.IsEmpty) return false;

        // Strip DTI byte.
        if (info[0] == (byte)'T') info = info[1..];

        // Require '#' format marker.
        if (info.IsEmpty || info[0] != (byte)'#') return false;
        info = info[1..];

        // Sequence number: 3 characters.
        if (info.Length < 3) return false;
        string sequence = Encoding.ASCII.GetString(info[..3]);
        info = info[3..];

        // Optional comma after sequence (spec §13 says the comma is
        // mandatory for the digit-form sequence but optional for MIC).
        if (info.Length > 0 && info[0] == (byte)',')
        {
            info = info[1..];
        }

        // Remainder: 5 analog values + digital bits + optional comment,
        // comma-separated.
        var tail = Encoding.ASCII.GetString(info);
        var parts = tail.Split(',');
        if (parts.Length < AnalogChannelCount + 1) return false;

        var analogs = new double[AnalogChannelCount];
        for (int i = 0; i < AnalogChannelCount; i++)
        {
            var raw = parts[i].Trim();
            if (!options.AllowNonIntegerTelemetry)
            {
                // Strict §13: each channel is exactly 3 digits, 000–255.
                if (raw.Length != 3) return false;
                foreach (var c in raw) if (c < '0' || c > '9') return false;
            }
            if (!double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out analogs[i]))
            {
                return false;
            }
            if (!options.AllowNonIntegerTelemetry && (analogs[i] < 0 || analogs[i] > 255))
            {
                return false;
            }
        }

        // The next part is the digital bits. May be shorter or longer than
        // 8 characters in pathological frames; pad/truncate to 8.
        var bitsText = parts[AnalogChannelCount].TrimEnd('\r', '\n');
        var bits = new bool[DigitalBitCount];
        int n = Math.Min(bitsText.Length, DigitalBitCount);
        for (int i = 0; i < n; i++)
        {
            char c = bitsText[i];
            if (c != '0' && c != '1') return false;
            bits[i] = c == '1';
        }

        // Anything after the bits is a comment (joined on comma).
        string comment = parts.Length > AnalogChannelCount + 1
            ? string.Join(',', parts.Skip(AnalogChannelCount + 1)).TrimEnd('\r', '\n')
            : string.Empty;

        telemetry = new AprsTelemetry(sequence, analogs, bits, comment);
        return true;
    }
}
