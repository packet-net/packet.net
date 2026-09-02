using AwesomeAssertions;
using Packet.Ax25;
using Packet.Ax25.Monitor;

namespace Packet.Ax25.Tests.Monitor;

public class Ax25InfoTextTests
{
    [Fact]
    public void Printable_No_Layer_3_Reads_As_Text_With_Line_Ending_Trimmed()
    {
        Ax25InfoText.TryRead("Welcome to GB7RDG\r"u8, Ax25Frame.PidNoLayer3).Should().Be("Welcome to GB7RDG");
    }

    [Fact]
    public void Inner_Line_Breaks_And_Tabs_Are_Kept()
    {
        Ax25InfoText.TryRead("line one\r\nline\ttwo\r\n"u8, Ax25Frame.PidNoLayer3).Should().Be("line one\r\nline\ttwo");
    }

    [Fact]
    public void Utf8_Is_Accepted()
    {
        Ax25InfoText.TryRead("73 de Tomas é"u8, Ax25Frame.PidNoLayer3).Should().Be("73 de Tomas é");
    }

    [Theory]
    [InlineData(new byte[] { 0x01, 0x02, 0x03 })]
    [InlineData(new byte[] { 0xFF, 0xFE })]
    [InlineData(new byte[] { (byte)'a', 0x00 })]
    [InlineData(new byte[] { })]
    [InlineData(new byte[] { (byte)'\r', (byte)'\n' })]
    public void Binary_Empty_And_Whitespace_Only_Are_Not_Text(byte[] info)
    {
        Ax25InfoText.TryRead(info, Ax25Frame.PidNoLayer3).Should().BeNull();
    }

    [Fact]
    public void Other_Protocols_Are_Not_Text_Even_When_Printable()
    {
        Ax25InfoText.TryRead("hello"u8, Ax25Frame.PidNetRom).Should().BeNull();
        Ax25InfoText.TryRead("hello"u8, null).Should().BeNull();
    }
}
