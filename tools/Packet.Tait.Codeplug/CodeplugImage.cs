using System.Globalization;
using System.Text;

namespace Packet.Tait.Codeplug;

/// <summary>
/// A whole codeplug: the CPS .m8p header key/values plus the ordered records. The .m8p layout is
/// a <c>***</c> ... <c>###</c> header block of <c>Key=Value</c> lines, a blank line, a <c>---</c>
/// marker, then one ASCII-hex record per line.
/// </summary>
public sealed class CodeplugImage
{
    /// <summary>Header key/value pairs (Radio, Tier, DBVer, Build, Date, ...), in file order.</summary>
    public IReadOnlyList<KeyValuePair<string, string>> Header { get; }

    /// <summary>The records, in file/wire order.</summary>
    public IReadOnlyList<CodeplugRecord> Records { get; }

    /// <summary>Create an image from a header and records.</summary>
    public CodeplugImage(
        IReadOnlyList<KeyValuePair<string, string>> header,
        IReadOnlyList<CodeplugRecord> records)
    {
        Header = header ?? throw new ArgumentNullException(nameof(header));
        Records = records ?? throw new ArgumentNullException(nameof(records));
    }

    /// <summary>Look up a header value by key, or null if absent.</summary>
    public string? HeaderValue(string key) =>
        Header.FirstOrDefault(kv => string.Equals(kv.Key, key, StringComparison.OrdinalIgnoreCase)).Value;

    /// <summary>The database version the field-offset map must be pinned against (the DBVer
    /// header), or null if the header does not carry one.</summary>
    public string? DatabaseVersion => HeaderValue("DBVer");

    /// <summary>The database version read from the codeplug itself (record 0x27 = a 12-bit field),
    /// as a zero-padded 4-digit string, or null if that record is absent. Authoritative: the header
    /// value is what the CPS chose to save, this is what the codeplug actually carries.</summary>
    public string? DatabaseVersionFromRecord
    {
        get
        {
            CodeplugRecord? r = Find(0x27, 0);
            if (r is null || r.Data.Length < 2)
            {
                return null;
            }

            int value = r.Data[0] | ((r.Data[1] & 0x0F) << 8); // low 12 bits, LSB first
            return value.ToString("D4", CultureInfo.InvariantCulture);
        }
    }

    /// <summary>Find the record with the given type and index, or null.</summary>
    public CodeplugRecord? Find(byte type, byte index) =>
        Records.FirstOrDefault(r => r.Section == type && r.Index == index);

    /// <summary>Find the record with the given type and index, or throw if it is absent.</summary>
    public CodeplugRecord Require(byte type, byte index) =>
        Find(type, index) ?? throw new InvalidOperationException(
            $"codeplug has no record 0x{type:X2}/{index}");

    /// <summary>Parse a .m8p document.</summary>
    public static CodeplugImage LoadM8p(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var header = new List<KeyValuePair<string, string>>();
        var records = new List<CodeplugRecord>();
        bool inRecords = false;

        foreach (string raw in text.Split('\n'))
        {
            string line = raw.TrimEnd('\r').Trim();
            if (line.Length == 0)
            {
                continue;
            }

            if (!inRecords)
            {
                if (line == "---")
                {
                    inRecords = true;
                    continue;
                }

                if (line is "***" or "###")
                {
                    continue;
                }

                int eq = line.IndexOf('=', StringComparison.Ordinal);
                if (eq > 0)
                {
                    header.Add(new KeyValuePair<string, string>(line[..eq], line[(eq + 1)..]));
                }

                continue;
            }

            records.Add(CodeplugRecord.Parse(line));
        }

        return new CodeplugImage(header, records);
    }

    /// <summary>Render the image back to .m8p text (CRLF line endings, matching the CPS).</summary>
    public string ToM8p()
    {
        var sb = new StringBuilder();
        sb.Append("***\r\n");
        foreach (var kv in Header)
        {
            sb.Append(kv.Key).Append('=').Append(kv.Value).Append("\r\n");
        }

        sb.Append("###\r\n\r\n---\r\n");
        foreach (CodeplugRecord r in Records)
        {
            sb.Append(r.ToWireLine()).Append("\r\n");
        }

        return sb.ToString();
    }

    /// <summary>A section-by-section summary: record count and total data bytes per section.</summary>
    public IReadOnlyList<(byte Section, int RecordCount, int DataBytes)> SectionMap()
    {
        return Records
            .GroupBy(r => r.Section)
            .OrderBy(g => g.Key)
            .Select(g => (g.Key, g.Count(), g.Sum(r => r.Data.Length)))
            .ToList();
    }

    /// <summary>Every distinct section number present, ascending. This is the read plan: the
    /// programmer issues an <c>r&lt;section&gt;</c> for each.</summary>
    public IReadOnlyList<byte> Sections() =>
        Records.Select(r => r.Section).Distinct().OrderBy(s => s).ToList();
}
