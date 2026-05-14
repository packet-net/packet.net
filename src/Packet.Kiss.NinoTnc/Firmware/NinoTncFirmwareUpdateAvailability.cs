namespace Packet.Kiss.NinoTnc.Firmware;

/// <summary>
/// The answer to "the modem at COM6 is running firmware X; is there
/// a newer firmware available for its chip?"
/// </summary>
/// <param name="CurrentVersion">The version currently running on the
///   modem, as reported in its TX-Test diagnostic frame.</param>
/// <param name="ChipVariant">Which chip variant the modem runs.
///   Derived from the firmware version's major component.</param>
/// <param name="LatestAvailable">The most recent firmware release the
///   catalogue knows about for this chip variant. <c>null</c> if the
///   catalogue could not find any release for the variant — usually
///   because the variant is <see cref="NinoTncChipVariant.Unknown"/>
///   or because the upstream filename convention changed.</param>
public sealed record NinoTncFirmwareUpdateAvailability(
    NinoTncFirmwareVersion CurrentVersion,
    NinoTncChipVariant ChipVariant,
    NinoTncFirmwareRelease? LatestAvailable)
{
    /// <summary>
    /// True if the catalogue knows about a release strictly newer than
    /// what's running. False if the modem is up to date, the variant
    /// is unknown, or the catalogue is empty.
    /// </summary>
    public bool UpdateAvailable =>
        LatestAvailable is not null &&
        LatestAvailable.Version.CompareTo(CurrentVersion) > 0;
}
