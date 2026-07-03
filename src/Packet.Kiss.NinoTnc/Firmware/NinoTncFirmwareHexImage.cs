using System.Text;

namespace Packet.Kiss.NinoTnc.Firmware;

/// <summary>
/// A parsed, pre-validated NinoTNC Intel-HEX firmware image: the text lines
/// exactly as they will be sent to the bootloader, plus the chip variant the
/// image targets.
/// </summary>
/// <remarks>
/// <para>
/// The chip classification follows upstream <c>flashtnc.py</c>: the image is
/// scanned for one of the known <em>first bootloader lines</em> — the flash
/// page where each chip variant's bootloader lives is at a variant-specific
/// address, so the line is a reliable fingerprint. An image matching none of
/// the known lines is refused (<see cref="NinoTncFlashFailure.HexTargetUnknown"/>)
/// rather than guessed at: flashing the wrong variant bricks the modem until
/// ICSP recovery.
/// </para>
/// <para>
/// Validation is deliberately strict on the construction side (see the
/// repo-wide "spec-compliant by default" discipline): every line must start
/// with the <c>':'</c> record mark, contain only hex digits after it, and the
/// image must end with the Intel-HEX end-of-file record — the bootloader only
/// signals success (<c>'Z'</c>) when it sees that record, so an image without
/// it could never complete and would strand the modem mid-flash.
/// </para>
/// </remarks>
public sealed class NinoTncFirmwareHexImage
{
    /// <summary>The Intel-HEX end-of-file record (case-insensitive on the wire).</summary>
    public const string EndOfFileRecord = ":00000001FF";

    // The known first-bootloader lines, per upstream flashtnc.py (version f,
    // 2022-05-01). Comparison is exact — the checksum suffix pins each line
    // to its variant. Stored lowercase; matched case-insensitively because
    // hex tools differ in digit case.
    private static readonly (string Line, NinoTncChipVariant Variant)[] KnownBootloaderLines =
    [
        (":108800007a00fa0000002200000f7800c3e8a900f7", NinoTncChipVariant.Dspic33Ep512),
        (":10427c007a00fa0000002200000f7800c3e8a900c1", NinoTncChipVariant.Dspic33Ep256),
        (":10427c007a00fa00403f9800ce389000010f780089", NinoTncChipVariant.Dspic33Ep256),
        (":10427c007c00fa00503f980000002200000f7800ec", NinoTncChipVariant.Dspic33Ep256),
        (":102800007c00fa00503f980000002200000f780082", NinoTncChipVariant.Dspic33Ep256),
        (":102800002f08b000889fbe008a9fbe008c9fbe002c", NinoTncChipVariant.Dspic33Ep256),
        (":108800002f08b000889fbe008a9fbe008c9fbe00cc", NinoTncChipVariant.Dspic33Ep512),
    ];

    private NinoTncFirmwareHexImage(IReadOnlyList<string> lines, NinoTncChipVariant targetChip)
    {
        Lines = lines;
        TargetChip = targetChip;
    }

    /// <summary>The image lines, in file order, without line terminators.</summary>
    public IReadOnlyList<string> Lines { get; }

    /// <summary>The chip variant this image targets.</summary>
    public NinoTncChipVariant TargetChip { get; }

    /// <summary>
    /// Parse and validate raw Intel-HEX file bytes.
    /// </summary>
    /// <exception cref="NinoTncFlashException">
    /// <see cref="NinoTncFlashFailure.HexImageInvalid"/> when the bytes are
    /// not a well-formed Intel-HEX text file;
    /// <see cref="NinoTncFlashFailure.HexTargetUnknown"/> when no known
    /// bootloader line identifies the target chip.
    /// </exception>
    public static NinoTncFirmwareHexImage Parse(ReadOnlyMemory<byte> hexImage)
    {
        if (hexImage.IsEmpty)
        {
            throw new NinoTncFlashException(
                NinoTncFlashFailure.HexImageInvalid, "The hex image is empty.");
        }

        foreach (byte b in hexImage.Span)
        {
            if (b is not ((>= 0x20 and <= 0x7E) or (byte)'\r' or (byte)'\n'))
            {
                throw new NinoTncFlashException(
                    NinoTncFlashFailure.HexImageInvalid,
                    $"The hex image contains non-printable byte 0x{b:X2} — not an Intel-HEX text file.");
            }
        }

        var lines = new List<string>();
        foreach (var raw in Encoding.ASCII.GetString(hexImage.Span)
                     .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            if (raw[0] != ':')
            {
                throw new NinoTncFlashException(
                    NinoTncFlashFailure.HexImageInvalid,
                    $"Line {lines.Count + 1} does not start with the Intel-HEX ':' record mark: \"{Truncate(raw)}\".");
            }
            for (int i = 1; i < raw.Length; i++)
            {
                if (!char.IsAsciiHexDigit(raw[i]))
                {
                    throw new NinoTncFlashException(
                        NinoTncFlashFailure.HexImageInvalid,
                        $"Line {lines.Count + 1} contains non-hex character '{raw[i]}': \"{Truncate(raw)}\".");
                }
            }
            lines.Add(raw);
        }

        if (lines.Count == 0)
        {
            throw new NinoTncFlashException(
                NinoTncFlashFailure.HexImageInvalid, "The hex image contains no records.");
        }
        if (!string.Equals(lines[^1], EndOfFileRecord, StringComparison.OrdinalIgnoreCase))
        {
            throw new NinoTncFlashException(
                NinoTncFlashFailure.HexImageInvalid,
                $"The hex image does not end with the Intel-HEX end-of-file record ({EndOfFileRecord}) — " +
                "the bootloader would never signal completion, stranding the modem mid-flash.");
        }

        var target = ClassifyTargetChip(lines);
        if (target == NinoTncChipVariant.Unknown)
        {
            throw new NinoTncFlashException(
                NinoTncFlashFailure.HexTargetUnknown,
                "The hex image matches none of the known NinoTNC bootloader fingerprints, so the " +
                "target chip variant (dsPIC33EP256GP vs dsPIC33EP512GP) cannot be determined. " +
                "Refusing to flash — the wrong variant bricks the modem until ICSP recovery.");
        }

        return new NinoTncFirmwareHexImage(lines, target);
    }

    /// <summary>
    /// Scan image lines for a known first-bootloader line and return the chip
    /// variant it fingerprints, or <see cref="NinoTncChipVariant.Unknown"/>.
    /// </summary>
    public static NinoTncChipVariant ClassifyTargetChip(IEnumerable<string> lines)
    {
        ArgumentNullException.ThrowIfNull(lines);
        foreach (var line in lines)
        {
            foreach (var (known, variant) in KnownBootloaderLines)
            {
                if (string.Equals(line, known, StringComparison.OrdinalIgnoreCase))
                {
                    return variant;
                }
            }
        }
        return NinoTncChipVariant.Unknown;
    }

    private static string Truncate(string line) =>
        line.Length <= 48 ? line : line[..45] + "...";
}
