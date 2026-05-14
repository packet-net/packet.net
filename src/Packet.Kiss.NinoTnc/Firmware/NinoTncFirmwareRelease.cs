namespace Packet.Kiss.NinoTnc.Firmware;

/// <summary>
/// A specific firmware image that has been published. Carries the
/// version, the chip variant it's for, and where to download it from.
/// </summary>
/// <param name="Version">The firmware version (e.g. <c>3.44</c>).</param>
/// <param name="ChipVariant">Which chip variant this image is built for.
///   Always matches <c>Version.ChipVariant</c>; carried separately for
///   convenience.</param>
/// <param name="DownloadUrl">Where the Intel-HEX firmware image lives.
///   For the GitHub-backed catalogue this is the raw GitHub URL of the
///   <c>N9600A-v{major}-{minor}.hex</c> file at tip of master.</param>
/// <param name="MplabChecksumUrl">Optional URL of the MPLAB checksum
///   file Nino publishes alongside each release
///   (<c>v{major}-{minor}-mplab-checksums.txt</c>). Consumers can use
///   it to verify a downloaded image before flashing.</param>
public sealed record NinoTncFirmwareRelease(
    NinoTncFirmwareVersion Version,
    NinoTncChipVariant ChipVariant,
    Uri DownloadUrl,
    Uri? MplabChecksumUrl);
