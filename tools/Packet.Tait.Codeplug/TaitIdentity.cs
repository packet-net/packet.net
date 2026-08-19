using System.Text;

namespace Packet.Tait.Codeplug;

/// <summary>
/// The radio identity the interrogate pulls from section 0. Each record's data bytes are
/// themselves ASCII text (the record is ASCII-hex of an ASCII string), so the model, firmware and
/// serial come straight out of the decoded bytes.
/// </summary>
public sealed class TaitIdentity
{
    /// <summary>Record 0: model, e.g. <c>TMAB12-B100_0201</c>.</summary>
    public string? Model { get; init; }

    /// <summary>Record 1: firmware, e.g. <c>QMA1F_std_02.18.00.00</c>.</summary>
    public string? Firmware { get; init; }

    /// <summary>Record 2: database / build versions, e.g. <c>0094,0086</c>.</summary>
    public string? Versions { get; init; }

    /// <summary>Record 5: serial number, e.g. <c>19925328</c>.</summary>
    public string? Serial { get; init; }

    /// <summary>Assemble an identity from the section-0 records.</summary>
    public static TaitIdentity FromSectionZero(IReadOnlyList<CodeplugRecord> sectionZero)
    {
        ArgumentNullException.ThrowIfNull(sectionZero);
        return new TaitIdentity
        {
            Model = Ascii(sectionZero, 0),
            Firmware = Ascii(sectionZero, 1),
            Versions = Ascii(sectionZero, 2),
            Serial = Ascii(sectionZero, 5),
        };
    }

    private static string? Ascii(IReadOnlyList<CodeplugRecord> records, byte index)
    {
        CodeplugRecord? r = records.FirstOrDefault(x => x.Index == index);
        return r is null ? null : Encoding.ASCII.GetString(r.Data).TrimEnd('\0', ' ');
    }

    /// <summary>A one-line summary for the CLI.</summary>
    public override string ToString() =>
        $"model={Model} firmware={Firmware} serial={Serial} versions={Versions}";
}
