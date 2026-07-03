namespace Packet.Kiss.NinoTnc.Firmware;

/// <summary>
/// The outcome of a successful firmware flash.
/// </summary>
/// <remarks>
/// On success the bootloader reboots the modem into the new firmware
/// immediately; the very first boot after a flash also runs a bootloader
/// self-update (~2 s) before KISS comes up, and the RAM operating mode is
/// cleared (the modem boots mode 0) — callers should wait a few seconds,
/// then re-verify with GETVER and re-apply the desired mode via SETHW.
/// </remarks>
/// <param name="Chip">The chip variant that was flashed (bootloader-confirmed).</param>
/// <param name="BootloaderVersion">The bootloader's one-letter version reply
///   (lowercase = dsPIC33EP256GP, uppercase = dsPIC33EP512GP).</param>
/// <param name="LinesWritten">Intel-HEX lines the bootloader accepted.</param>
/// <param name="TotalLines">Total lines in the image.</param>
/// <param name="Duration">Wall-clock time from opening the port to the
///   bootloader's <c>'Z'</c> completion signal.</param>
/// <param name="ResumedStrandedBootloader">True when the modem was already
///   sitting in the bootloader (e.g. after an interrupted flash) and the
///   KISS-side entry handshake was skipped.</param>
public sealed record NinoTncFlashResult(
    NinoTncChipVariant Chip,
    char BootloaderVersion,
    int LinesWritten,
    int TotalLines,
    TimeSpan Duration,
    bool ResumedStrandedBootloader);
