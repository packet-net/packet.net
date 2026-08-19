using AwesomeAssertions;
using Packet.Tait.Codeplug;
using Xunit;

namespace Packet.Tait.Codeplug.Tests;

public class CodeplugFormatTests
{
    // Real records lifted from the captured wire / saved .m8p (radio s/n 19925328).
    private const string CounterRecord = "2700025E0079";
    private const string SectionZeroStub = "000010002020202000000000005F000000000011";
    private const string ModelRecord = "000010544D414231322D423130305F3032303147";

    [Fact]
    public void Checksum_makes_the_whole_record_sum_to_zero_mod_256()
    {
        // 27 00 02 5E 00 -> checksum 79
        byte[] headerAndData = [0x27, 0x00, 0x02, 0x5E, 0x00];
        CodeplugChecksum.Compute(headerAndData).Should().Be(0x79);

        byte[] whole = [0x27, 0x00, 0x02, 0x5E, 0x00, 0x79];
        CodeplugChecksum.IsWholeRecordValid(whole).Should().BeTrue();
    }

    [Theory]
    [InlineData(CounterRecord)]
    [InlineData(SectionZeroStub)]
    [InlineData(ModelRecord)]
    public void Record_round_trips_through_parse_and_render(string wire)
    {
        CodeplugRecord record = CodeplugRecord.Parse(wire);
        record.ToWireLine().Should().Be(wire);
    }

    [Fact]
    public void Record_decodes_address_as_section_and_index()
    {
        CodeplugRecord record = CodeplugRecord.Parse(CounterRecord);
        record.Section.Should().Be(0x27);
        record.Index.Should().Be(0x00);
        record.Address.Should().Be(0x2700);
        record.Data.Should().Equal(0x5E, 0x00);
    }

    [Fact]
    public void Record_strips_a_command_prefix()
    {
        CodeplugRecord withPrefix = CodeplugRecord.Parse("w" + CounterRecord);
        withPrefix.ToWireLine().Should().Be(CounterRecord);
    }

    [Fact]
    public void Record_rejects_a_corrupt_checksum()
    {
        // flip the last checksum nibble
        string bad = CounterRecord[..^1] + "A";
        Action act = () => CodeplugRecord.Parse(bad);
        act.Should().Throw<FormatException>();
    }

    [Fact]
    public void Record_rejects_a_length_that_disagrees_with_the_body()
    {
        // claim length 0x20 but give a short body (checksum will also fail, but length is the point)
        Action act = () => CodeplugRecord.Parse("010020AB");
        act.Should().Throw<FormatException>();
    }
}
