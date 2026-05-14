using Packet.Core;

namespace Packet.Core.Tests;

public class Ax25AddressOptionsTests
{
    [Fact]
    public void Strict_Rejects_All_Space_Callsign_Slot()
    {
        var buf = new byte[Ax25Address.EncodedLength];
        for (int i = 0; i < 6; i++) buf[i] = 0x40;   // ' ' << 1
        buf[6] = 0x60;                                // C=0, R=11, SSID=0, E=0

        Action act = () => { _ = Ax25Address.Read(buf, Ax25ParseOptions.Strict); };
        act.Should().Throw<ArgumentException>().WithMessage("*empty callsign*");
    }

    [Fact]
    public void Lenient_Accepts_All_Space_Callsign_Slot()
    {
        // The BPQ-style `>IS` beacon shape. Default behaviour.
        Span<byte> buf = stackalloc byte[Ax25Address.EncodedLength];
        for (int i = 0; i < 6; i++) buf[i] = 0x40;
        buf[6] = 0x60;

        var addr = Ax25Address.Read(buf, Ax25ParseOptions.Lenient);
        addr.Callsign.Base.Should().Be("");
    }

    [Fact]
    public void Parameterless_Overload_Uses_Lenient()
    {
        // Back-compat path. Same input as the Lenient case above.
        Span<byte> buf = stackalloc byte[Ax25Address.EncodedLength];
        for (int i = 0; i < 6; i++) buf[i] = 0x40;
        buf[6] = 0x60;

        var addr = Ax25Address.Read(buf);
        addr.Callsign.Base.Should().Be("");
    }

    [Fact]
    public void Strict_Still_Accepts_Normal_Callsign()
    {
        // 'M0LTE' / SSID 1 / E=0, C=0.
        Span<byte> buf = stackalloc byte[Ax25Address.EncodedLength];
        buf[0] = (byte)('M' << 1);
        buf[1] = (byte)('0' << 1);
        buf[2] = (byte)('L' << 1);
        buf[3] = (byte)('T' << 1);
        buf[4] = (byte)('E' << 1);
        buf[5] = (byte)(' ' << 1);   // padding (only 5 callsign chars)
        buf[6] = 0x62;                // C=0, R=11, SSID=1, E=0

        var addr = Ax25Address.Read(buf, Ax25ParseOptions.Strict);
        addr.Callsign.Base.Should().Be("M0LTE");
        addr.Callsign.Ssid.Should().Be((byte)1);
    }
}
