using Packet.Kiss.NinoTnc.Firmware;

namespace Packet.Kiss.NinoTnc.Tests.Firmware;

public class NinoTncFirmwareUpdateAvailabilityTests
{
    [Fact]
    public async Task UpdateAvailable_When_Catalogue_Has_Strictly_Newer_Release()
    {
        var current = new NinoTncFirmwareVersion(3, 43);
        INinoTncFirmwareCatalogue catalogue = new FakeCatalogue
        {
            LatestFor256 = new NinoTncFirmwareRelease(
                Version: new NinoTncFirmwareVersion(3, 44),
                ChipVariant: NinoTncChipVariant.Dspic33Ep256,
                DownloadUrl: new Uri("https://example.test/N9600A-v3-44.hex"),
                MplabChecksumUrl: null),
        };

        var availability = await catalogue.CheckForUpdateAsync(current);

        availability.UpdateAvailable.Should().BeTrue();
        availability.CurrentVersion.Should().Be(current);
        availability.LatestAvailable!.Version.Should().Be(new NinoTncFirmwareVersion(3, 44));
    }

    [Fact]
    public async Task UpdateAvailable_Is_False_When_Modem_Already_On_Latest()
    {
        var current = new NinoTncFirmwareVersion(3, 44);
        INinoTncFirmwareCatalogue catalogue = new FakeCatalogue
        {
            LatestFor256 = new NinoTncFirmwareRelease(
                Version: new NinoTncFirmwareVersion(3, 44),
                ChipVariant: NinoTncChipVariant.Dspic33Ep256,
                DownloadUrl: new Uri("https://example.test/N9600A-v3-44.hex"),
                MplabChecksumUrl: null),
        };

        var availability = await catalogue.CheckForUpdateAsync(current);

        availability.UpdateAvailable.Should().BeFalse("modem is on the latest");
    }

    [Fact]
    public async Task UpdateAvailable_Is_False_When_Catalogue_Is_Empty_For_Variant()
    {
        var current = new NinoTncFirmwareVersion(3, 44);
        INinoTncFirmwareCatalogue catalogue = new FakeCatalogue();    // no releases at all

        var availability = await catalogue.CheckForUpdateAsync(current);

        availability.UpdateAvailable.Should().BeFalse();
        availability.LatestAvailable.Should().BeNull();
    }

    [Fact]
    public async Task Variants_Are_Independent_In_The_Catalogue()
    {
        INinoTncFirmwareCatalogue catalogue = new FakeCatalogue
        {
            LatestFor256 = new NinoTncFirmwareRelease(
                Version: new NinoTncFirmwareVersion(3, 44),
                ChipVariant: NinoTncChipVariant.Dspic33Ep256,
                DownloadUrl: new Uri("https://example.test/N9600A-v3-44.hex"),
                MplabChecksumUrl: null),
            LatestFor512 = new NinoTncFirmwareRelease(
                Version: new NinoTncFirmwareVersion(4, 44),
                ChipVariant: NinoTncChipVariant.Dspic33Ep512,
                DownloadUrl: new Uri("https://example.test/N9600A-v4-44.hex"),
                MplabChecksumUrl: null),
        };

        var availability256 = await catalogue.CheckForUpdateAsync(new NinoTncFirmwareVersion(3, 43));
        availability256.LatestAvailable!.ChipVariant.Should().Be(NinoTncChipVariant.Dspic33Ep256);

        var availability512 = await catalogue.CheckForUpdateAsync(new NinoTncFirmwareVersion(4, 43));
        availability512.LatestAvailable!.ChipVariant.Should().Be(NinoTncChipVariant.Dspic33Ep512);
    }

    [Fact]
    public async Task Unknown_Variant_Returns_No_Latest()
    {
        // A pre-2.90 firmware (e.g. 2.71) has Unknown chip variant by
        // our heuristic. The catalogue shouldn't recommend anything in
        // that case — the operator should figure out the chip manually.
        var current = new NinoTncFirmwareVersion(2, 71);
        INinoTncFirmwareCatalogue catalogue = new FakeCatalogue
        {
            LatestFor256 = new NinoTncFirmwareRelease(
                Version: new NinoTncFirmwareVersion(3, 44),
                ChipVariant: NinoTncChipVariant.Dspic33Ep256,
                DownloadUrl: new Uri("https://example.test/N9600A-v3-44.hex"),
                MplabChecksumUrl: null),
        };

        var availability = await catalogue.CheckForUpdateAsync(current);

        availability.UpdateAvailable.Should().BeFalse();
        availability.LatestAvailable.Should().BeNull();
        availability.ChipVariant.Should().Be(NinoTncChipVariant.Unknown);
    }

    [Fact]
    public async Task UpdateAvailable_Is_False_When_Catalogue_Has_An_Older_Release()
    {
        // Defensive case: the catalogue somehow surfaces a release older
        // than what the modem is running (e.g. somebody downgraded the
        // upstream tip of master while a host was already on a newer
        // version). UpdateAvailable should be false — never recommend
        // a downgrade.
        var current = new NinoTncFirmwareVersion(3, 44);
        INinoTncFirmwareCatalogue catalogue = new FakeCatalogue
        {
            LatestFor256 = new NinoTncFirmwareRelease(
                Version: new NinoTncFirmwareVersion(3, 43),
                ChipVariant: NinoTncChipVariant.Dspic33Ep256,
                DownloadUrl: new Uri("https://example.test/N9600A-v3-43.hex"),
                MplabChecksumUrl: null),
        };

        var availability = await catalogue.CheckForUpdateAsync(current);

        availability.UpdateAvailable.Should().BeFalse("downgrades are not 'updates'");
        availability.LatestAvailable!.Version.Should().Be(new NinoTncFirmwareVersion(3, 43));
    }

    private sealed class FakeCatalogue : INinoTncFirmwareCatalogue
    {
        public NinoTncFirmwareRelease? LatestFor256 { get; init; }
        public NinoTncFirmwareRelease? LatestFor512 { get; init; }

        public Task<NinoTncFirmwareRelease?> GetLatestForVariantAsync(
            NinoTncChipVariant variant,
            CancellationToken cancellationToken = default) => variant switch
            {
                NinoTncChipVariant.Dspic33Ep256 => Task.FromResult(LatestFor256),
                NinoTncChipVariant.Dspic33Ep512 => Task.FromResult(LatestFor512),
                _ => Task.FromResult<NinoTncFirmwareRelease?>(null),
            };
    }
}
