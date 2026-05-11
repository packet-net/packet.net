using System.Diagnostics.CodeAnalysis;

namespace Packet.Core;

/// <summary>
/// An amateur-radio callsign with an optional secondary station identifier
/// (SSID, 0–15). The base callsign is 1–6 uppercase ASCII alphanumerics —
/// the encoded form AX.25 allows.
/// </summary>
/// <remarks>
/// This is the human-friendly representation. The on-the-wire encoded form
/// (6 octets left-shifted by 1 plus an SSID byte) lives in
/// <see cref="Ax25Address"/>.
/// </remarks>
public readonly struct Callsign : IEquatable<Callsign>
{
    /// <summary>The base callsign, e.g. "G7XYZ". Always uppercase A–Z / 0–9.</summary>
    public string Base { get; }

    /// <summary>Secondary Station Identifier, 0–15.</summary>
    public byte Ssid { get; }

    /// <summary>
    /// Create a callsign from its parts.
    /// </summary>
    /// <exception cref="ArgumentException">Base or SSID out of range.</exception>
    public Callsign(string @base, byte ssid = 0)
    {
        ArgumentNullException.ThrowIfNull(@base);
        if (@base.Length is < 1 or > 6)
        {
            throw new ArgumentException($"callsign base must be 1–6 characters (got '{@base}')", nameof(@base));
        }
        foreach (char c in @base)
        {
            if (!IsValidBaseChar(c))
            {
                throw new ArgumentException($"callsign base must be A–Z / 0–9 (got '{c}')", nameof(@base));
            }
        }
        if (ssid > 15)
        {
            throw new ArgumentException($"SSID must be 0–15 (got {ssid})", nameof(ssid));
        }
        Base = @base;
        Ssid = ssid;
    }

    /// <summary>
    /// Parse the canonical text form: "BASE" or "BASE-SSID".
    /// </summary>
    public static Callsign Parse(string text)
    {
        if (TryParse(text, out var c))
        {
            return c;
        }
        throw new FormatException($"invalid callsign: '{text}'");
    }

    /// <summary>
    /// Try-parse the canonical text form.
    /// </summary>
    public static bool TryParse([NotNullWhen(true)] string? text, out Callsign callsign)
    {
        callsign = default;
        if (string.IsNullOrEmpty(text))
        {
            return false;
        }

        string baseStr;
        byte ssid;
        int dash = text.IndexOf('-', StringComparison.Ordinal);
        if (dash >= 0)
        {
            baseStr = text[..dash];
            var ssidStr = text[(dash + 1)..];
            if (!byte.TryParse(ssidStr, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out ssid) || ssid > 15)
            {
                return false;
            }
        }
        else
        {
            baseStr = text;
            ssid = 0;
        }

        if (baseStr.Length is < 1 or > 6)
        {
            return false;
        }
        foreach (char c in baseStr)
        {
            if (!IsValidBaseChar(c))
            {
                return false;
            }
        }

        callsign = new Callsign(baseStr, ssid);
        return true;
    }

    private static bool IsValidBaseChar(char c) => c is (>= 'A' and <= 'Z') or (>= '0' and <= '9');

    /// <inheritdoc/>
    public override string ToString() => Ssid == 0 ? Base : $"{Base}-{Ssid}";

    /// <inheritdoc/>
    public bool Equals(Callsign other) =>
        string.Equals(Base, other.Base, StringComparison.Ordinal) && Ssid == other.Ssid;

    /// <inheritdoc/>
    public override bool Equals(object? obj) => obj is Callsign c && Equals(c);

    /// <inheritdoc/>
    public override int GetHashCode() => HashCode.Combine(Base, Ssid);

    public static bool operator ==(Callsign left, Callsign right) => left.Equals(right);

    public static bool operator !=(Callsign left, Callsign right) => !left.Equals(right);
}
