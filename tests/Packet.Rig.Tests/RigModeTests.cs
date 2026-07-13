using Packet.Rig;

namespace Packet.Rig.Tests;

public class RigModeTests
{
    [Fact]
    public void From_Normalises_To_Upper_Invariant_And_Trims()
    {
        RigMode.From(" usb ").Should().Be(RigMode.Usb);
        RigMode.From("pktusb").Token.Should().Be("PKTUSB");
    }

    [Fact]
    public void From_Preserves_Backend_Native_Tokens_It_Does_Not_Know()
    {
        var mode = RigMode.From("DATA-U");
        mode.Token.Should().Be("DATA-U");
        mode.Should().NotBe(RigMode.PktUsb);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void From_Rejects_Empty(string token)
    {
        var act = () => RigMode.From(token);
        act.Should().Throw<ArgumentException>();
    }

    [Theory]
    [InlineData("US B")]
    [InlineData("USB\nF 14074000")] // wire tokens travel on line protocols — injection guard
    [InlineData("USB\t2400")]
    public void From_Rejects_Embedded_Whitespace_And_Control_Characters(string token)
    {
        var act = () => RigMode.From(token);
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Equality_Is_By_Token()
    {
        RigMode.From("USB").Should().Be(RigMode.Usb);
        RigMode.From("LSB").Should().NotBe(RigMode.Usb);
        RigMode.Usb.ToString().Should().Be("USB");
    }

    [Fact]
    public void Well_Known_Modes_Use_Hamlib_Vocabulary()
    {
        RigMode.PktUsb.Token.Should().Be("PKTUSB");
        RigMode.CwR.Token.Should().Be("CWR");
        RigMode.FmN.Token.Should().Be("FMN");
        RigMode.WFm.Token.Should().Be("WFM");
    }

    [Fact]
    public void RigModeState_Carries_Optional_Passband()
    {
        var state = new RigModeState(RigMode.Usb, 2400);
        state.PassbandHz.Should().Be(2400);
        new RigModeState(RigMode.Fm, null).PassbandHz.Should().BeNull();
    }
}
