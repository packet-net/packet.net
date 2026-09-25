using System.Diagnostics.CodeAnalysis;

namespace Packet.Aprs;

/// <summary>
/// A station or path address as it appears in an APRS packet header: the source, the
/// destination, or one digipeater / APRS-IS path entry.
/// </summary>
/// <remarks>
/// <para>
/// On RF an address is an AX.25 address: 1 to 6 upper-case letters and digits, plus an optional
/// numeric SSID 1 to 15 (APRS12c §3, UAP §1.1). On APRS-IS the TNC2 text form is looser: names
/// from internet-only stations, servers and q-constructs (<c>WHO-IS</c>, <c>T2SPAIN</c>,
/// <c>qAC</c>) are legal there (APRS12c §17, UAP §4). This type holds the address exactly as
/// written, so it round-trips, and reports through <see cref="IsAx25"/> whether it would be
/// valid on air.
/// </para>
/// <para>
/// Equality is ordinal on <see cref="Value"/>: <c>N0CALL</c> and <c>N0CALL-0</c> are different
/// spellings and compare unequal. Use <see cref="IsSameStation"/> to compare AX.25 identity.
/// </para>
/// </remarks>
public readonly record struct AprsAddress
{
    /// <summary>The longest address accepted in TNC2 text. APRS-IS allows 9; this leaves headroom
    /// for real-world oddities without admitting arbitrary junk.</summary>
    public const int MaxLength = 16;

    private AprsAddress(string value) => Value = value;

    /// <summary>The address exactly as written, including any <c>-SSID</c> suffix.</summary>
    public string Value { get; }

    /// <summary>The part before the first <c>-</c>: the callsign without its SSID, as
    /// <see cref="Packet.Core.Callsign.Base"/> is for an AX.25 callsign.</summary>
    public string Base => Value.IndexOf('-', StringComparison.Ordinal) is var i and >= 0 ? Value[..i] : Value;

    /// <summary>The SSID text after the first <c>-</c>, or empty if there is none.</summary>
    public string Ssid => Value.IndexOf('-', StringComparison.Ordinal) is var i and >= 0 ? Value[(i + 1)..] : "";

    /// <summary>
    /// The SSID as a number 0 to 15 when it is numeric and in range; 0 when there is no SSID;
    /// otherwise null (for example <c>WHO-IS</c>).
    /// </summary>
    public int? NumericSsid => Ssid.Length == 0 ? 0 : TryParseNumericSsid(Ssid, out int n) ? n : null;

    /// <summary>
    /// True when this address can be carried in an AX.25 address field as written: a 1 to 6
    /// character upper-case alphanumeric callsign and either no SSID or an SSID 1 to 15 without
    /// leading zeros. <c>N0CALL-0</c> is not canonical (UAP §1.1: an SSID of zero is not written).
    /// </summary>
    public bool IsAx25
    {
        get
        {
            string call = Base;
            if (call.Length is < 1 or > 6 || !call.All(IsAx25Char))
            {
                return false;
            }

            string ssid = Ssid;
            if (Value.Contains('-', StringComparison.Ordinal) && ssid.Length == 0)
            {
                return false;
            }

            return ssid.Length == 0 || (TryParseNumericSsid(ssid, out int n) && n > 0 && ssid[0] != '0');
        }
    }

    /// <summary>
    /// True when both addresses name the same AX.25 station: same callsign and same numeric SSID,
    /// treating a missing SSID and <c>-0</c> as equal. Case is significant (APRS is case-sensitive).
    /// </summary>
    public bool IsSameStation(AprsAddress other) =>
        string.Equals(Base, other.Base, StringComparison.Ordinal)
        && NumericSsid is { } a && other.NumericSsid is { } b && a == b;

    /// <summary>Parses an address in TNC2 text form. Throws <see cref="FormatException"/> if it is empty,
    /// too long, or contains a character that cannot appear in a TNC2 header.</summary>
    public static AprsAddress Parse(string value) =>
        TryParse(value, out AprsAddress address)
            ? address
            : throw new FormatException($"'{value}' is not a valid APRS address");

    /// <summary>Tries to parse an address in TNC2 text form.</summary>
    public static bool TryParse([NotNullWhen(true)] string? value, out AprsAddress address)
    {
        address = default;
        if (string.IsNullOrEmpty(value) || value.Length > MaxLength || !value.All(IsTnc2AddressChar))
        {
            return false;
        }

        address = new AprsAddress(value);
        return true;
    }

    /// <summary>Creates an AX.25 address from a callsign and numeric SSID (0 writes no SSID).</summary>
    public static AprsAddress FromAx25(string callsign, int ssid = 0)
    {
        ArgumentNullException.ThrowIfNull(callsign);
        ArgumentOutOfRangeException.ThrowIfNegative(ssid);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(ssid, 15);
        var address = new AprsAddress(ssid == 0 ? callsign : $"{callsign}-{ssid}");
        return address.IsAx25
            ? address
            : throw new ArgumentException($"'{callsign}' is not an AX.25 callsign (1-6 upper-case letters and digits)", nameof(callsign));
    }

    /// <summary>
    /// Creates an address from a Packet.NET <see cref="Packet.Core.Callsign"/>. Throws
    /// <see cref="ArgumentException"/> for an empty base, which an AX.25 address field can carry
    /// but which names no APRS station.
    /// </summary>
    public static AprsAddress FromCallsign(Packet.Core.Callsign callsign)
    {
        ArgumentException.ThrowIfNullOrEmpty(callsign.Base, nameof(callsign));
        return FromAx25(callsign.Base, callsign.Ssid);
    }

    /// <summary>
    /// Gets this address as a Packet.NET <see cref="Packet.Core.Callsign"/>, for handing to the
    /// AX.25 layer. False when it is not an AX.25 address (<see cref="IsAx25"/>), such as the
    /// APRS-IS names <c>WHO-IS</c> or <c>qAC</c>, or a lower-case callsign.
    /// </summary>
    public bool TryGetCallsign(out Packet.Core.Callsign callsign)
    {
        callsign = default;
        if (!IsAx25)
        {
            return false;
        }

        callsign = new Packet.Core.Callsign(Base, (byte)(NumericSsid ?? 0));
        return true;
    }

    /// <summary>Returns <see cref="Value"/>.</summary>
    public override string ToString() => Value ?? "";

    internal static AprsAddress CreateUnchecked(string value) => new(value);

    internal static bool IsAx25Char(char c) => c is (>= 'A' and <= 'Z') or (>= '0' and <= '9');

    // Printable ASCII other than the TNC2 header delimiters and the used-marker.
    internal static bool IsTnc2AddressChar(char c) => c is > ' ' and < (char)0x7F and not (',' or ':' or '>' or '*');

    private static bool TryParseNumericSsid(string ssid, out int n)
    {
        n = 0;
        if (ssid.Length is < 1 or > 2 || !ssid.All(char.IsAsciiDigit))
        {
            return false;
        }

        n = int.Parse(ssid, System.Globalization.CultureInfo.InvariantCulture);
        return n <= 15;
    }
}
