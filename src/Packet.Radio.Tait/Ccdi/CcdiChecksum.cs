using System.Globalization;

namespace Packet.Radio.Tait.Ccdi;

/// <summary>
/// The CCDI checksum (manual §1.8.5): modulo-256 sum of every message byte before the checksum
/// field, two's-complemented, rendered as two upper-case ASCII hex digits.
/// </summary>
public static class CcdiChecksum
{
    /// <summary>Compute the two-character checksum for <paramref name="body"/> (the
    /// <c>[IDENT][SIZE][PARAMETERS]</c> portion of a message).</summary>
    public static string Compute(ReadOnlySpan<char> body)
    {
        int sum = 0;
        foreach (char c in body)
        {
            sum += (byte)c;
        }
        return ((-sum) & 0xFF).ToString("X2", CultureInfo.InvariantCulture);
    }

    /// <summary>Validate that <paramref name="checksum"/> is the correct checksum of
    /// <paramref name="body"/>. Case-sensitive, per the spec's upper-case rule.</summary>
    public static bool IsValid(ReadOnlySpan<char> body, ReadOnlySpan<char> checksum) =>
        checksum.Length == 2 && Compute(body).AsSpan().SequenceEqual(checksum);
}
