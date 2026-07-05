namespace Packet.Radio.Tait.Tests;

/// <summary>
/// The Tait band catalogue + product-code parser (<see cref="TaitBandCatalog"/>): reading the
/// <c>[A-Z][0-9]</c> band designator off a product code (the character pair after the first <c>-</c>),
/// mapping it to a UK amateur band, and the null cases (unknown / malformed / absent). The band split
/// — hence the amateur band — is CCDI-readable even though the tuned frequency is not.
/// </summary>
public class TaitBandCatalogTests
{
    private static TaitRadioIdentity IdentityWithProductCode(string? productCode) => new(
        RuType: '1', RuModel: '3', RuTier: '2', CcdiVersion: "03.02", SerialNumber: "1G000123",
        Versions: productCode is null
            ? new Dictionary<string, string>()
            : new Dictionary<string, string> { ["00"] = productCode });

    [Theory]
    [InlineData("TMAB12-B100_0201", "B1", "2m")]      // the live 2m bench rig
    [InlineData("TMAB12-A400_0201", "A4", "4m")]      // 66–88 MHz = UK 4m (OARC wiki says "none"; wrong for UK)
    [InlineData("TMAB12-H500_0201", "H5", "70cm")]    // 70cm fixture
    [InlineData("TMAB12-H600_0201", "H6", "70cm")]
    [InlineData("TMAB12-H700_0201", "H7", "70cm")]
    public void An_amateur_band_designator_parses_to_its_uk_band(string productCode, string code, string amateurBand)
    {
        TaitBandCatalog.TryParseProductCode(productCode, out var band).Should().BeTrue();
        band.Code.Should().Be(code);
        band.AmateurBand.Should().Be(amateurBand);
    }

    [Theory]
    [InlineData("TMAB12-C000_0201", "C0")]
    [InlineData("TMAB12-D100_0201", "D1")]
    [InlineData("TMAB12-G200_0201", "G2")]
    [InlineData("TMAB12-K500_0201", "K5")]
    public void A_non_amateur_designator_parses_but_has_no_uk_band(string productCode, string code)
    {
        TaitBandCatalog.TryParseProductCode(productCode, out var band).Should().BeTrue();
        band.Code.Should().Be(code);
        band.AmateurBand.Should().BeNull();
    }

    [Fact]
    public void The_band_carries_its_frequency_split()
    {
        TaitBandCatalog.TryParseProductCode("TMAB12-B100_0201", out var band).Should().BeTrue();
        band.MinHz.Should().Be(136_000_000);
        band.MaxHz.Should().Be(174_000_000);
    }

    [Fact]
    public void A_well_formed_but_unknown_designator_is_not_matched()
    {
        // Z9 is a syntactically valid [A-Z][0-9] designator but names no known Tait split.
        TaitBandCatalog.TryParseProductCode("TMAB12-Z900_0201", out var band).Should().BeFalse();
        band.Should().BeNull();
    }

    [Theory]
    [InlineData("")]                     // blank
    [InlineData("   ")]                  // whitespace
    [InlineData("TMAB12B100")]           // no '-' at all
    [InlineData("TMAB12-")]              // '-' with nothing after it
    [InlineData("TMAB12-B")]             // only one char after '-'
    [InlineData("TMAB12-11")]            // digit-digit, not [A-Z][0-9]
    [InlineData("TMAB12-bx")]            // lowercase / non-digit
    public void A_malformed_product_code_does_not_parse(string productCode)
    {
        TaitBandCatalog.TryParseProductCode(productCode, out var band).Should().BeFalse();
        band.Should().BeNull();
    }

    [Fact]
    public void TryParse_reads_the_band_off_an_identity_record_00()
    {
        var identity = IdentityWithProductCode("TMAB12-B100_0201");

        TaitBandCatalog.TryParse(identity, out var band).Should().BeTrue();
        band.AmateurBand.Should().Be("2m");
    }

    [Fact]
    public void The_computed_identity_band_property_matches_the_parser()
    {
        IdentityWithProductCode("TMAB12-H500_0201").Band!.AmateurBand.Should().Be("70cm");
        IdentityWithProductCode("TMAB12-A400_0201").Band!.AmateurBand.Should().Be("4m");
    }

    [Fact]
    public void An_identity_with_no_product_code_record_has_no_band()
    {
        var identity = IdentityWithProductCode(productCode: null);

        TaitBandCatalog.TryParse(identity, out var band).Should().BeFalse();
        band.Should().BeNull();
        identity.Band.Should().BeNull();
    }
}
