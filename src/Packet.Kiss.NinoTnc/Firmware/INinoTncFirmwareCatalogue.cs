namespace Packet.Kiss.NinoTnc.Firmware;

/// <summary>
/// Where current NinoTNC firmware releases live. The intended
/// implementation is GitHub-backed (<see cref="GitHubNinoTncFirmwareCatalogue"/>)
/// reading from the tip-of-master of Nino's repository, but tests
/// can supply a fake.
/// </summary>
/// <remarks>
/// Models only <em>current</em> releases — i.e. whatever is sitting at
/// tip of master right now. Old firmware versions live in git history;
/// we don't surface them. The use case for "I want to install firmware
/// 3.41 from two years ago" is rare enough that it's not worth the
/// complexity, and the upstream repo doesn't host old hex files
/// alongside new ones anyway.
/// </remarks>
public interface INinoTncFirmwareCatalogue
{
    /// <summary>
    /// Return the latest release available for the given chip variant,
    /// or <c>null</c> if the catalogue has no release for that variant.
    /// </summary>
    Task<NinoTncFirmwareRelease?> GetLatestForVariantAsync(
        NinoTncChipVariant variant,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Convenience: combine the modem's running firmware version with
    /// the catalogue's latest into an update-availability answer.
    /// </summary>
    async Task<NinoTncFirmwareUpdateAvailability> CheckForUpdateAsync(
        NinoTncFirmwareVersion currentVersion,
        CancellationToken cancellationToken = default)
    {
        var variant = currentVersion.ChipVariant;
        var latest = await GetLatestForVariantAsync(variant, cancellationToken).ConfigureAwait(false);
        return new NinoTncFirmwareUpdateAvailability(currentVersion, variant, latest);
    }
}
