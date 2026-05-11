using Packet.Core;

namespace Packet.Core.Tests;

public class Ax25AddressTests
{
    [Fact]
    public void Write_Then_Read_RoundTrips_For_Common_Cases()
    {
        var inputs = new[]
        {
            new Ax25Address(new Callsign("G7XYZ", 0), CrhBit: false, ExtensionBit: false),
            new Ax25Address(new Callsign("G7XYZ", 7), CrhBit: true,  ExtensionBit: false),
            new Ax25Address(new Callsign("M0LTE", 1), CrhBit: false, ExtensionBit: true),
            new Ax25Address(new Callsign("WB2OSZ", 0), CrhBit: true, ExtensionBit: true),
            new Ax25Address(new Callsign("K1A", 15),  CrhBit: false, ExtensionBit: false),
        };

        Span<byte> buf = stackalloc byte[Ax25Address.EncodedLength];
        foreach (var input in inputs)
        {
            buf.Clear();
            input.Write(buf);
            var roundTripped = Ax25Address.Read(buf);
            roundTripped.ShouldBe(input);
        }
    }

    [Fact]
    public void Encoded_Callsign_Chars_Are_Left_Shifted_By_1()
    {
        // Per §3.12, each callsign character is left-shifted by 1, so
        // 'A' (0x41) encodes as 0x82, 'B' (0x42) as 0x84, etc.
        var addr = new Ax25Address(new Callsign("AB", 0), CrhBit: false, ExtensionBit: false);
        Span<byte> buf = stackalloc byte[Ax25Address.EncodedLength];
        addr.Write(buf);

        buf[0].ShouldBe((byte)('A' << 1));
        buf[1].ShouldBe((byte)('B' << 1));
        // padding spaces
        for (int i = 2; i < 6; i++)
        {
            buf[i].ShouldBe((byte)(' ' << 1));
        }
    }

    [Fact]
    public void Ssid_Byte_Layout_Per_Spec()
    {
        // C/H bit: bit 7
        // Reserved bits: bits 6, 5 — default "11" on write
        // SSID: bits 4..1
        // E bit: bit 0
        var addr = new Ax25Address(new Callsign("G7XYZ", 7), CrhBit: true, ExtensionBit: true);
        Span<byte> buf = stackalloc byte[Ax25Address.EncodedLength];
        addr.Write(buf);

        byte ssidByte = buf[6];
        ((ssidByte & 0x80) != 0).ShouldBeTrue("C/H bit");
        ((ssidByte & 0x60) == 0x60).ShouldBeTrue("reserved bits default 11");
        ((ssidByte >> 1) & 0x0F).ShouldBe(7);
        ((ssidByte & 0x01) != 0).ShouldBeTrue("E bit");
    }

    [Fact]
    public void Read_Rejects_Short_Span()
    {
        var buf = new byte[6];
        Should.Throw<ArgumentException>(() => Ax25Address.Read(buf));
    }

    [Fact]
    public void Write_Rejects_Short_Span()
    {
        var addr = new Ax25Address(new Callsign("G7XYZ", 0), false, false);
        Should.Throw<ArgumentException>(() =>
        {
            var buf = new byte[6];
            addr.Write(buf);
        });
    }
}
