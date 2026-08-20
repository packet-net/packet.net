using System.Globalization;
using System.Text;

namespace Packet.Tait.Codeplug;

/// <summary>
/// One codeplug record. On the wire and in the .m8p a record is the ASCII-hex string
/// <c>&lt;addr:4 hex&gt;&lt;len:2 hex&gt;&lt;data:2*len hex&gt;&lt;checksum:2 hex&gt;</c>. The
/// 16-bit address is not a flat byte offset: it is <c>(Section &lt;&lt; 8) | Index</c>, so the
/// codeplug is a set of numbered sections each holding a run of indexed records.
/// </summary>
public sealed class CodeplugRecord
{
    /// <summary>Create a record from its section, index within the section, and decoded data.</summary>
    public CodeplugRecord(byte section, byte index, byte[] data)
    {
        ArgumentNullException.ThrowIfNull(data);
        if (data.Length > 255)
        {
            throw new ArgumentException("a record carries at most 255 data bytes", nameof(data));
        }

        Section = section;
        Index = index;
        Data = data;
    }

    /// <summary>The high address byte: which logical section this record belongs to.</summary>
    public byte Section { get; }

    /// <summary>The low address byte: this record's index within its section.</summary>
    public byte Index { get; }

    /// <summary>The decoded data bytes (length is the record's <c>len</c> field).</summary>
    public byte[] Data { get; }

    /// <summary>The full 16-bit address, <c>(Section &lt;&lt; 8) | Index</c>.</summary>
    public int Address => (Section << 8) | Index;

    /// <summary>The checksum byte over address + length + data.</summary>
    public byte Checksum => CodeplugChecksum.Compute(HeaderAndData());

    private byte[] HeaderAndData()
    {
        var bytes = new byte[3 + Data.Length];
        bytes[0] = Section;
        bytes[1] = Index;
        bytes[2] = (byte)Data.Length;
        Array.Copy(Data, 0, bytes, 3, Data.Length);
        return bytes;
    }

    /// <summary>Render the record as the ASCII-hex wire/file line (no command prefix, no CR).</summary>
    public string ToWireLine()
    {
        var sb = new StringBuilder(6 + (Data.Length * 2) + 2);
        sb.Append(Address.ToString("X4", CultureInfo.InvariantCulture));
        sb.Append(Data.Length.ToString("X2", CultureInfo.InvariantCulture));
        foreach (byte b in Data)
        {
            sb.Append(b.ToString("X2", CultureInfo.InvariantCulture));
        }

        sb.Append(Checksum.ToString("X2", CultureInfo.InvariantCulture));
        return sb.ToString();
    }

    /// <summary>Parse and validate an ASCII-hex record line (an optional single-letter command
    /// prefix such as <c>w</c> is stripped). Throws <see cref="FormatException"/> on a malformed
    /// or checksum-failing line.</summary>
    public static CodeplugRecord Parse(string line)
    {
        ArgumentNullException.ThrowIfNull(line);
        string s = line.Trim();
        if (s.Length > 0 && !Uri.IsHexDigit(s[0]))
        {
            s = s[1..]; // drop a command prefix like 'w' or 'r'
        }

        if (s.Length < 8 || (s.Length % 2) != 0)
        {
            throw new FormatException($"record too short or odd length: '{line}'");
        }

        byte[] all = FromHex(s);
        if (!CodeplugChecksum.IsWholeRecordValid(all))
        {
            throw new FormatException($"record checksum does not verify: '{line}'");
        }

        byte section = all[0];
        byte index = all[1];
        int len = all[2];
        if (all.Length != 3 + len + 1)
        {
            throw new FormatException(
                $"declared length {len} does not match record body: '{line}'");
        }

        var data = new byte[len];
        Array.Copy(all, 3, data, 0, len);
        return new CodeplugRecord(section, index, data);
    }

    private static byte[] FromHex(string hex)
    {
        var bytes = new byte[hex.Length / 2];
        for (int i = 0; i < bytes.Length; i++)
        {
            bytes[i] = byte.Parse(hex.AsSpan(i * 2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        }

        return bytes;
    }
}
