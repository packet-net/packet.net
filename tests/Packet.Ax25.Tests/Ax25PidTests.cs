using AwesomeAssertions;
using Packet.Ax25;

namespace Packet.Ax25.Tests;

public class Ax25PidTests
{
    [Theory]
    [InlineData(0xF0, "no layer 3")]
    [InlineData(0xCF, "NET/ROM")]
    [InlineData(0xCC, "IP")]
    [InlineData(0x08, "segment")]
    [InlineData(0x42, "0x42")]
    [InlineData(0x10, "layer 3 (0x10)")]
    [InlineData(0x2A, "layer 3 (0x2A)")]
    public void Name_Is_A_Monitor_Label(byte pid, string expected)
    {
        Ax25Pid.Name(pid).Should().Be(expected);
    }
}
