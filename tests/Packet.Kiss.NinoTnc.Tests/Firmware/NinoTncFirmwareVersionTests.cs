using Packet.Kiss.NinoTnc.Firmware;

namespace Packet.Kiss.NinoTnc.Tests.Firmware;

public class NinoTncFirmwareVersionTests
{
    [Theory]
    [InlineData("3.44", 3, 44, NinoTncChipVariant.Dspic33Ep256)]
    [InlineData("4.44", 4, 44, NinoTncChipVariant.Dspic33Ep512)]
    [InlineData("3.16", 3, 16, NinoTncChipVariant.Dspic33Ep256)]
    [InlineData("2.71", 2, 71, NinoTncChipVariant.Unknown)] // pre-dual-versioning era
    [InlineData(" 3.44 ", 3, 44, NinoTncChipVariant.Dspic33Ep256)] // whitespace tolerated
    public void Parse_Returns_Expected_Components(string text, int expectedMajor, int expectedMinor, NinoTncChipVariant expectedVariant)
    {
        var version = NinoTncFirmwareVersion.Parse(text);
        version.Major.Should().Be(expectedMajor);
        version.Minor.Should().Be(expectedMinor);
        version.ChipVariant.Should().Be(expectedVariant);
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("3")]
    [InlineData("3.")]
    [InlineData(".44")]
    [InlineData("3.x")]
    [InlineData("-1.44")]
    [InlineData("3.44.5")] // we don't support a three-component shape
    public void Parse_Rejects_Bad_Input(string text)
    {
        NinoTncFirmwareVersion.TryParse(text, out _).Should().BeFalse();
        ((Action)(() => NinoTncFirmwareVersion.Parse(text))).Should().Throw<FormatException>();
    }

    [Fact]
    public void TryParse_Of_Null_Is_False()
    {
        NinoTncFirmwareVersion.TryParse(null, out _).Should().BeFalse();
    }

    [Fact]
    public void Within_Same_Major_Higher_Minor_Sorts_Later()
    {
        var v343 = new NinoTncFirmwareVersion(3, 43);
        var v344 = new NinoTncFirmwareVersion(3, 44);
        v344.Should().BeGreaterThan(v343);
        v343.Should().BeLessThan(v344);
        (v344 > v343).Should().BeTrue();
        (v343 < v344).Should().BeTrue();
    }

    [Fact]
    public void ToString_Pads_Minor_To_Two_Digits_Like_Ninos_Convention()
    {
        new NinoTncFirmwareVersion(3, 44).ToString().Should().Be("3.44");
        new NinoTncFirmwareVersion(3, 5).ToString().Should().Be("3.05");
        new NinoTncFirmwareVersion(4, 100).ToString().Should().Be("4.100");
    }
}
