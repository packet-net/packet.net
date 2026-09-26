using System.Buffers;
using System.Globalization;
using System.Text;

namespace Packet.Aprs.Internal;

/// <summary>Builds information-field bytes.</summary>
internal sealed class InfoWriter
{
    private readonly ArrayBufferWriter<byte> buffer = new(64);

    public int Length => buffer.WrittenCount;

    /// <summary>
    /// Set by a comment check that cannot tell from the text alone whether the comment will read
    /// back unchanged, because that depends on what is written around it. <see cref="AprsData.ToInformationField"/>
    /// then decodes the finished field to find out, and refuses with this message if it does not.
    /// </summary>
    public string? CommentCheck { get; set; }

    public ReadOnlySpan<byte> Written => buffer.WrittenSpan;

    public InfoWriter Byte(byte b)
    {
        buffer.GetSpan(1)[0] = b;
        buffer.Advance(1);
        return this;
    }

    public InfoWriter Char(char c)
    {
        if (c > 0xFF)
        {
            throw new ArgumentException($"'{c}' is not a single-byte character", nameof(c));
        }

        return Byte((byte)c);
    }

    public InfoWriter Bytes(ReadOnlySpan<byte> bytes)
    {
        buffer.Write(bytes);
        return this;
    }

    /// <summary>Writes ASCII; throws on anything outside 0x20-0x7E.</summary>
    public InfoWriter Ascii(string text)
    {
        foreach (char c in text)
        {
            if (c is < ' ' or > '~')
            {
                throw new ArgumentException($"'{text}' contains a character that is not printable ASCII");
            }

            Byte((byte)c);
        }

        return this;
    }

    /// <summary>Writes free text as UTF-8 (APRS 1.2 allows UTF-8 in comments and message text).</summary>
    public InfoWriter Utf8(string text)
    {
        buffer.Write(Encoding.UTF8.GetBytes(text));
        return this;
    }

    /// <summary>Writes a non-negative integer zero-padded to exactly <paramref name="width"/> digits.</summary>
    public InfoWriter Digits(int value, int width)
    {
        string s = value.ToString(CultureInfo.InvariantCulture).PadLeft(width, '0');
        if (value < 0 || s.Length != width)
        {
            throw new ArgumentOutOfRangeException(nameof(value), $"{value} does not fit in {width} digits");
        }

        return Ascii(s);
    }

    public InfoWriter Base91(long value, int digits)
    {
        Span<byte> tmp = stackalloc byte[digits];
        Internal.Base91.Encode(value, tmp);
        return Bytes(tmp);
    }

    public byte[] ToArray() => buffer.WrittenSpan.ToArray();
}
