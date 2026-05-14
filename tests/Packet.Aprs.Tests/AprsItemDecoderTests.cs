using Packet.Aprs;

namespace Packet.Aprs.Tests;

public class AprsItemDecoderTests
{
    // Real corpus samples (info field with DTI). Lat/lon are direwolf's
    // decoded references.

    [Theory]
    [InlineData(")Temp-JHN!4344.50NT00122.50WWSOUSTONS: TEMPERE-SATURE HUMIDITE",
                "Temp-JHN", true,  43.7417, -1.3750, 'T', 'W')]
    [InlineData(")PPV-JHN!4344.00N/00122.50WGRENDEMENT",
                "PPV-JHN",  true,  43.7333, -1.3750, '/', 'G')]
    [InlineData(")SP2JP-2!5306.79N/01759.22E`QO-100  Satellite Station",
                "SP2JP-2",  true,  53.1132, 17.9870, '/', '`')]
    [InlineData(")DB0IDS B!5013.21ND00814.89E&438.412MHz D-STAR",
                "DB0IDS B", true,  50.2202,  8.2482, 'D', '&')]
    [InlineData(")HL1RR-DP!0100.00N/00100.00ErRNG0034",
                "HL1RR-DP", true,   1.0000,  1.0000, '/', 'r')]
    public void Decodes_Live_Item_With_Uncompressed_Position(
        string infoText, string expectedName, bool expectedAlive,
        double expectedLat, double expectedLon,
        char expectedSymTable, char expectedSymCode)
    {
        var info = System.Text.Encoding.ASCII.GetBytes(infoText);
        AprsItemDecoder.TryDecode(info, out var item).Should().BeTrue();
        item.Name.Should().Be(expectedName);
        item.IsAlive.Should().Be(expectedAlive);
        item.Position.Latitude.Should().BeApproximately(expectedLat, 1e-3);
        item.Position.Longitude.Should().BeApproximately(expectedLon, 1e-3);
        item.Position.SymbolTable.Should().Be(expectedSymTable);
        item.Position.SymbolCode.Should().Be(expectedSymCode);
    }

    [Fact]
    public void Decodes_Killed_Item()
    {
        // APRS101 §11: `)NAME_POSITION` — killed item, indicator byte is '_'.
        var info = System.Text.Encoding.ASCII.GetBytes(")AIDV#2_4903.50N/07201.75WA");
        AprsItemDecoder.TryDecode(info, out var item).Should().BeTrue();
        item.Name.Should().Be("AIDV#2");
        item.IsAlive.Should().BeFalse();
    }

    [Fact]
    public void Decodes_Compressed_Item()
    {
        // Spec §11 example.
        var info = System.Text.Encoding.ASCII.GetBytes(@")MOBIL!\5L!!<*e79VsT");
        AprsItemDecoder.TryDecode(info, out var item).Should().BeTrue();
        item.Name.Should().Be("MOBIL");
        item.IsAlive.Should().BeTrue();
        item.Position.Format.Should().Be(AprsPositionFormat.Compressed);
        item.Position.SymbolTable.Should().Be('\\');
    }

    [Theory]
    [InlineData(")XY!4903.50N/07201.75WA")]              // name only 2 chars
    [InlineData(")ABCDEFGHIJ!4903.50N/07201.75WA")]      // name 10 chars
    [InlineData(")NoTerminator4903.50N/07201.75WA")]     // no '!' or '_' in window
    public void Rejects_Bad_Name_Length(string infoText)
    {
        var info = System.Text.Encoding.ASCII.GetBytes(infoText);
        AprsItemDecoder.TryDecode(info, out _).Should().BeFalse();
    }

    [Fact]
    public void Strips_DTI_If_Present()
    {
        var withDti    = System.Text.Encoding.ASCII.GetBytes(")MOBIL!4903.50N/07201.75WA");
        var withoutDti = System.Text.Encoding.ASCII.GetBytes("MOBIL!4903.50N/07201.75WA");
        AprsItemDecoder.TryDecode(withDti,    out var a).Should().BeTrue();
        AprsItemDecoder.TryDecode(withoutDti, out var b).Should().BeTrue();
        a.Should().Be(b);
    }
}
