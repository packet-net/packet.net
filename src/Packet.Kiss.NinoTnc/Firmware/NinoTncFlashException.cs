namespace Packet.Kiss.NinoTnc.Firmware;

/// <summary>
/// Why a firmware flash terminated without success. Mirrors the terminal
/// states of the dsPIC bootloader protocol (upstream <c>flashtnc.py</c>'s
/// exit codes), plus the pre-flight validation failures this library adds.
/// </summary>
public enum NinoTncFlashFailure
{
    /// <summary>Unclassified failure.</summary>
    Unknown = 0,

    /// <summary>The supplied bytes are not a usable Intel-HEX firmware image
    /// (empty, non-ASCII, a line without the <c>':'</c> start code / non-hex
    /// characters, or no end-of-file record).</summary>
    HexImageInvalid,

    /// <summary>The hex image contains none of the known first-bootloader
    /// lines, so the target chip variant cannot be determined. Flashing an
    /// unclassifiable image is refused — the wrong variant bricks the modem.</summary>
    HexTargetUnknown,

    /// <summary>The serial receive path never went quiet: the TNC kept
    /// producing bytes for longer than the drain budget (typically because
    /// the attached radio channel is busy). Quieten the channel (or detach
    /// the radio) and retry.</summary>
    SerialBufferNeverQuiet,

    /// <summary>The bootloader-entry command was sent but no ready signal
    /// (<c>'K'</c>) arrived within the entry timeout.</summary>
    BootloaderEntryTimeout,

    /// <summary>The bootloader never answered the <c>'V'</c> version query.</summary>
    BootloaderVersionUnreadable,

    /// <summary>The bootloader version reply is not a letter, so the chip
    /// variant convention (lowercase = dsPIC33EP256GP, uppercase =
    /// dsPIC33EP512GP) cannot be applied.</summary>
    BootloaderVersionUnsupported,

    /// <summary>The chip variant reported by the bootloader does not match
    /// the hex image's target. The transfer was refused before any line was
    /// written; the modem was told to return to its application firmware.</summary>
    ChipMismatch,

    /// <summary>The bootloader answered <c>'F'</c>: a flash write failed.
    /// Per upstream guidance the dsPIC may need replacement (or an ICSP
    /// reflash).</summary>
    FlashRejected,

    /// <summary>The bootloader answered <c>'N'</c>: a line's Intel-HEX
    /// checksum did not verify. The hex file may be corrupt.</summary>
    LineChecksumRejected,

    /// <summary>The bootloader answered <c>'X'</c>: a line contained a
    /// character outside the Intel-HEX alphabet. The hex file may be corrupt.</summary>
    InvalidCharacterRejected,

    /// <summary>The bootloader stopped answering mid-transfer. The modem is
    /// most likely stranded in the bootloader — re-running the flash is safe
    /// (the stranded-bootloader probe picks it up); if it keeps happening the
    /// dsPIC may need an ICSP reflash.</summary>
    NoResponse,

    /// <summary>The bootloader answered a line with a byte outside its
    /// documented reply alphabet (<c>K</c>/<c>Z</c>/<c>F</c>/<c>N</c>/<c>X</c>).</summary>
    UnexpectedResponse,

    /// <summary>Every image line was accepted (<c>'K'</c>) but the bootloader
    /// never signalled completion (<c>'Z'</c>). Should be unreachable for a
    /// well-formed image — the end-of-file record triggers <c>'Z'</c>.</summary>
    ImageEndedWithoutCompletion,
}

/// <summary>
/// Thrown when a NinoTNC firmware flash terminates without success, carrying
/// enough context to report precisely what happened and what to do next.
/// </summary>
/// <remarks>
/// The most important recovery fact: failures at or after bootloader entry
/// leave the modem <b>stranded in the bootloader</b> (dark LEDs, KISS
/// silent). That state is recoverable without hardware tools — re-run
/// <see cref="BootloaderNinoTncFirmwareFlasher.FlashAsync"/>, whose
/// stranded-bootloader probe (<c>'R'</c> → <c>'K'</c>) skips straight to the
/// transfer. Only <see cref="NinoTncFlashFailure.FlashRejected"/> and repeated
/// <see cref="NinoTncFlashFailure.NoResponse"/> point at a hardware-level
/// problem (ICSP reflash / chip replacement).
/// </remarks>
public sealed class NinoTncFlashException : Exception
{
    /// <summary>Create a flash failure with its terminal classification.</summary>
    public NinoTncFlashException(
        NinoTncFlashFailure failure,
        string message,
        int? linesWritten = null,
        byte? responseByte = null,
        char? bootloaderVersion = null,
        NinoTncChipVariant? bootloaderChip = null,
        NinoTncChipVariant? hexTargetChip = null)
        : base(message)
    {
        Failure = failure;
        LinesWritten = linesWritten;
        ResponseByte = responseByte;
        BootloaderVersion = bootloaderVersion;
        BootloaderChip = bootloaderChip;
        HexTargetChip = hexTargetChip;
    }

    /// <summary>The terminal-state classification.</summary>
    public NinoTncFlashFailure Failure { get; }

    /// <summary>How many image lines the bootloader had accepted when the
    /// flash failed, or <c>null</c> if the transfer never started.</summary>
    public int? LinesWritten { get; }

    /// <summary>The raw reply byte that terminated the flash, where one exists.</summary>
    public byte? ResponseByte { get; }

    /// <summary>The bootloader's one-letter version reply, once read.</summary>
    public char? BootloaderVersion { get; }

    /// <summary>The chip variant implied by <see cref="BootloaderVersion"/>.</summary>
    public NinoTncChipVariant? BootloaderChip { get; }

    /// <summary>The chip variant the hex image targets.</summary>
    public NinoTncChipVariant? HexTargetChip { get; }
}
