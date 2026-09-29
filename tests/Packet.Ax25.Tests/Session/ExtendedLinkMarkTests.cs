using Packet.Ax25.Session;
using Packet.Core;
using Xunit;

namespace Packet.Ax25.Tests.Session;

public class ExtendedLinkMarkTests
{
    private static Ax25SessionContext Ctx(bool extended) => new()
    {
        Local = new Callsign("M0LTE", 1),
        Remote = new Callsign("G8PZT", 2),
        IsExtended = extended,
    };

    private static byte[][] Frames(Ax25SessionContext c) =>
    [
        new UFrameSpec(UFrameType.Sabme, true, true).ToAx25Frame(c).ToBytes(),
        new SupervisoryFrameSpec(SupervisoryFrameType.Rr, true, 3, true).ToAx25Frame(c).ToBytes(),
    ];

    [Theory]
    [InlineData(true, 0x20)]
    [InlineData(false, 0x60)]
    public void SourceReservedBitsFollowModulus(bool extended, int expected)
    {
        foreach (var b in Frames(Ctx(extended)))
        {
            Assert.Equal(expected, b[13] & 0x60);
            Assert.Equal(0x60, b[6] & 0x60); // destination stays 11
        }
    }
}
