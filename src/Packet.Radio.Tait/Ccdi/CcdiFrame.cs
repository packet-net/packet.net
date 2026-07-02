using System.Globalization;

namespace Packet.Radio.Tait.Ccdi;

/// <summary>
/// CCDI wire framing (manual §1.8.3): <c>[IDENT][SIZE][PARAMETERS][CHECKSUM]&lt;CR&gt;</c> where
/// IDENT is one lower-case ASCII character, SIZE is the PARAMETERS length as two ASCII hex
/// digits, and CHECKSUM is <see cref="CcdiChecksum"/> over everything before it.
/// </summary>
public readonly record struct CcdiFrame(char Ident, string Parameters)
{
    /// <summary>Render the frame as its on-wire ASCII form, without the trailing CR.</summary>
    public string Encode()
    {
        string body = string.Create(
            CultureInfo.InvariantCulture, $"{Ident}{Parameters.Length:X2}{Parameters}");
        return body + CcdiChecksum.Compute(body);
    }

    /// <summary>Render the frame as transmit-ready bytes, including the trailing CR.</summary>
    public byte[] EncodeToBytes()
    {
        string encoded = Encode();
        var bytes = new byte[encoded.Length + 1];
        for (int i = 0; i < encoded.Length; i++)
        {
            bytes[i] = (byte)encoded[i];
        }
        bytes[^1] = (byte)'\r';
        return bytes;
    }

    /// <summary>
    /// Parse one received line (CR already stripped). Rejects anything whose SIZE doesn't match
    /// the actual parameter length or whose checksum fails — CCDI runs over plain async serial,
    /// so line noise is a normal event, not an exception.
    /// </summary>
    public static bool TryParse(ReadOnlySpan<char> line, out CcdiFrame frame)
    {
        frame = default;
        if (line.Length < 5)
        {
            return false;
        }

        if (!byte.TryParse(line.Slice(1, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out byte size))
        {
            return false;
        }

        if (line.Length != 5 + size)
        {
            return false;
        }

        if (!CcdiChecksum.IsValid(line[..^2], line[^2..]))
        {
            return false;
        }

        frame = new CcdiFrame(line[0], line.Slice(3, size).ToString());
        return true;
    }
}
