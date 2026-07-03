namespace Packet.Kiss.NinoTnc.Firmware;

/// <summary>
/// The seam where the firmware-flash operation lives. The real
/// implementation is <see cref="BootloaderNinoTncFirmwareFlasher"/> — a
/// native C# port of the dsPIC bootloader protocol that upstream
/// <c>flashtnc.py</c> speaks, hardware-validated on the bench rig.
/// <see cref="UnsupportedFirmwareFlasher"/> remains for hosts that must not
/// flash (it throws, so callers can wire the seam and disable the operation
/// by substitution).
/// </summary>
public interface INinoTncFirmwareFlasher
{
    /// <summary>
    /// Flash <paramref name="hexImage"/> onto the modem at
    /// <paramref name="portName"/>. The Intel-HEX image bytes are validated
    /// and classified (target chip variant) before the modem is touched; the
    /// implementation drives the bootloader handshake, verifies the
    /// bootloader's chip variant against the image, and transfers the image.
    /// </summary>
    /// <param name="portName">Serial port (e.g. <c>"COM6"</c> on
    ///   Windows, <c>"/dev/ttyACM1"</c> on Linux). The port must not be held
    ///   by another process — in particular, close any KISS connection to the
    ///   same modem first.</param>
    /// <param name="hexImage">Raw Intel-HEX file bytes.</param>
    /// <param name="progress">Optional progress reporter; called once per
    ///   accepted image line (~16–17 k lines over 2–4 minutes for a full
    ///   image — throttle on <see cref="NinoTncFlashProgress.Percent"/> if
    ///   that is too chatty).</param>
    /// <param name="cancellationToken">Cancellation. Safe before bootloader
    ///   entry; cancelling after entry strands the modem in the bootloader —
    ///   recoverable by re-running (the stranded-bootloader probe resumes),
    ///   see <see cref="BootloaderNinoTncFirmwareFlasher"/>.</param>
    /// <returns>A summary of the successful flash. The modem reboots into
    ///   the new firmware immediately after (first boot: bootloader
    ///   self-update, ~2 s; the RAM operating mode resets to 0).</returns>
    /// <exception cref="NinoTncFlashException">The flash terminated without
    ///   success — <see cref="NinoTncFlashException.Failure"/> classifies the
    ///   terminal state.</exception>
    Task<NinoTncFlashResult> FlashAsync(
        string portName,
        ReadOnlyMemory<byte> hexImage,
        IProgress<NinoTncFlashProgress>? progress = null,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// The refusing flasher: every call throws <see cref="NotSupportedException"/>.
/// Substitute it for <see cref="BootloaderNinoTncFirmwareFlasher"/> on hosts
/// where firmware flashing must be unavailable (e.g. a deployment whose
/// modems are managed out-of-band).
/// </summary>
public sealed class UnsupportedFirmwareFlasher : INinoTncFirmwareFlasher
{
    /// <inheritdoc/>
    public Task<NinoTncFlashResult> FlashAsync(
        string portName,
        ReadOnlyMemory<byte> hexImage,
        IProgress<NinoTncFlashProgress>? progress = null,
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException(
            "Firmware flashing is not supported on this host. The real implementation is " +
            "Packet.Kiss.NinoTnc.Firmware.BootloaderNinoTncFirmwareFlasher — wire that instead " +
            "if this host is allowed to flash modems.");
}
