using System.Globalization;

namespace Packet.Radio.Tait.Ccdi;

/// <summary>
/// CCDI wire framing (manual §1.8.3): <c>[IDENT][SIZE][PARAMETERS][CHECKSUM]&lt;CR&gt;</c> where
/// IDENT is one lower-case ASCII character, SIZE is the PARAMETERS length as two ASCII hex
/// digits, and CHECKSUM is <see cref="CcdiChecksum"/> over everything before it.
/// </summary>
public readonly record struct CcdiFrame(char Ident, string Parameters)
{
    /// <summary>The longest PARAMETERS field a CCDI frame can carry: SIZE is two hex digits
    /// (§1.8.3), so 0xFF characters is the hard ceiling.</summary>
    public const int MaxParameterLength = 255;

    /// <summary>Render the frame as its on-wire ASCII form, without the trailing CR.</summary>
    /// <exception cref="ArgumentException"><see cref="Parameters"/> is longer than
    /// <see cref="MaxParameterLength"/> or holds a character that would break line framing.</exception>
    public string Encode()
    {
        ValidateParameters(Parameters);
        string body = string.Create(
            CultureInfo.InvariantCulture, $"{Ident}{Parameters.Length:X2}{Parameters}");
        return body + CcdiChecksum.Compute(body);
    }

    /// <summary>
    /// The outbound strictness gate (#698): refuse to render a frame no CCDI receiver could read
    /// back. Over 255 parameter characters the <c>:X2</c> SIZE overflows to three hex digits and
    /// the frame becomes unparseable (this library's own <see cref="TryParse"/> rejects it), and
    /// an embedded CR/LF splits one frame into two unparseable lines on the wire. Deliberately
    /// not applied on the receive path: <see cref="TryParse"/> stays lenient about what arrives,
    /// while construction stays strict about what we emit.
    /// </summary>
    /// <param name="parameters">The PARAMETERS field about to go out.</param>
    internal static void ValidateParameters(string parameters)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        if (parameters.Length > MaxParameterLength)
        {
            throw new ArgumentException(
                "CCDI SIZE is two hex digits (§1.8.3), so a frame carries at most " +
                $"{MaxParameterLength} parameter characters, not {parameters.Length}",
                nameof(parameters));
        }
        for (int i = 0; i < parameters.Length; i++)
        {
            char c = parameters[i];
            if (c is '\r' or '\n' or '\u0011' or '\u0013')
            {
                throw new ArgumentException(
                    $"parameter character 0x{(int)c:X2} at offset {i} would corrupt CCDI line framing " +
                    "(CR/LF terminate frames; XON/XOFF may be software flow control, §1.6.1)",
                    nameof(parameters));
            }
        }
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
