using System.Diagnostics.CodeAnalysis;

namespace Packet.Core;

/// <summary>
/// An amateur-radio callsign with an optional secondary station identifier
/// (SSID, 0-15). The base callsign is 0-6 uppercase ASCII alphanumerics -
/// the encoded form AX.25 allows.
/// </summary>
/// <remarks>
/// <para>
/// This is the human-friendly representation. The on-the-wire encoded form
/// (6 octets left-shifted by 1 plus an SSID byte) lives in
/// <see cref="Ax25Address"/>.
/// </para>
/// <para>
/// Length range is 0-6. Zero-length is permitted because AX.25 v2.2 §3.12.2
/// says "If the call sign contains fewer than six characters, it is padded
/// with ASCII spaces between the last call sign character and the SSID
/// octet" - without specifying a minimum - and §6.1.1 acknowledges that
/// "operation with destination addresses other than actual amateur call
/// signs is a subject for further study." In practice some implementations
/// (BPQ's own ID beacon, some station QRV broadcasts) emit UI frames with
/// an all-space dest or source slot; this represents that on-wire state.
/// Note: <see cref="Parse"/> and <see cref="TryParse"/> over a text string
/// remain strict (≥1 char) - that path is for user-typed input where empty
/// is a typo, not a legitimate value.
/// </para>
/// </remarks>
public readonly struct Callsign : IEquatable<Callsign>
{
    /// <summary>The base callsign, e.g. "G7XYZ". Always uppercase A-Z / 0-9; can be empty.</summary>
    public string Base { get; }

    /// <summary>Secondary Station Identifier, 0-15.</summary>
    public byte Ssid { get; }

    /// <summary>
    /// Create a callsign from its parts. Base must be 0-6 uppercase
    /// A-Z / 0-9 characters; empty is permitted (see remarks on
    /// <see cref="Callsign"/>).
    /// </summary>
    /// <exception cref="ArgumentException">Base or SSID out of range.</exception>
    public Callsign(string @base, byte ssid = 0)
    {
        ArgumentNullException.ThrowIfNull(@base);
        if (@base.Length > 6)
        {
            throw new ArgumentException($"callsign base must be 0–6 characters (got '{@base}')", nameof(@base));
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
