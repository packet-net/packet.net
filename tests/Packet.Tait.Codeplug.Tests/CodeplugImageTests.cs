using AwesomeAssertions;
using Packet.Tait.Codeplug;
using Xunit;

namespace Packet.Tait.Codeplug.Tests;

public class CodeplugImageTests
{
    private const string Document =
        "***\r\n" +
        "Radio=TM8000\r\n" +
        "Tier=TM8100\r\n" +
        "DBVer=0095\r\n" +
        "Build=3.09.00.0004\r\n" +
        "###\r\n" +
        "\r\n" +
        "---\r\n" +
        "000010002020202000000000005F000000000011\r\n" +
        "2700025E0079\r\n" +
        "2F0002560079\r\n";

    [Fact]
    public void Loads_header_and_records()
    {
        CodeplugImage image = CodeplugImage.LoadM8p(Document);
        image.DatabaseVersion.Should().Be("0095");
        image.HeaderValue("Tier").Should().Be("TM8100");
        image.Records.Should().HaveCount(3);
        image.Sections().Should().Equal((byte)0x00, (byte)0x27, (byte)0x2F);
    }

    [Fact]
    public void Round_trips_back_to_m8p_text()
    {
        CodeplugImage image = CodeplugImage.LoadM8p(Document);
        string rendered = image.ToM8p();
        CodeplugImage reparsed = CodeplugImage.LoadM8p(rendered);

        reparsed.DatabaseVersion.Should().Be("0095");
        reparsed.Records.Select(r => r.ToWireLine())
            .Should().Equal(image.Records.Select(r => r.ToWireLine()));
    }

    [Fact]
    public void Section_map_counts_records_and_bytes()
    {
        CodeplugImage image = CodeplugImage.LoadM8p(Document);
        image.SectionMap().Should().ContainSingle(s => s.Section == 0x00 && s.RecordCount == 1 && s.DataBytes == 16);
    }
}
