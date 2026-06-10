using System.Text;

namespace Packet.Rhp2;

/// <summary>
/// Converts between binary payloads and the JSON <c>data</c> field.
/// </summary>
/// <remarks>
/// RHPv2 carries payload bytes inside a JSON string, NOT base64: each byte
/// maps to one character via Latin-1 (ISO-8859-1), and the JSON
/// serializer's escaping takes care of control characters (0x0D becomes
/// the two-character escape <c>\r</c> on the wire, 0x00 becomes
/// the six-character escape <c>\u0000</c>, and so on). Latin-1 is the one single-byte encoding
/// where every value 0x00–0xFF round-trips to exactly one
/// <see cref="char"/>, which is what makes arbitrary binary data safe
/// through this path.
/// </remarks>
public static class RhpDataEncoding
{
    /// <summary>Encodes payload bytes as a Latin-1 string for the JSON <c>data</c> field.</summary>
    public static string ToWireString(ReadOnlySpan<byte> bytes)
        => bytes.IsEmpty ? string.Empty : Encoding.Latin1.GetString(bytes);

    /// <summary>Decodes a JSON <c>data</c> string back into payload bytes.</summary>
    public static byte[] FromWireString(string s)
        => string.IsNullOrEmpty(s) ? [] : Encoding.Latin1.GetBytes(s);
}
