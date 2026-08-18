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
            roundTripped.Should().Be(input);
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

        buf[0].Should().Be((byte)('A' << 1));
        buf[1].Should().Be((byte)('B' << 1));
        // padding spaces
        for (int i = 2; i < 6; i++)
        {
            buf[i].Should().Be((byte)(' ' << 1));
        }
    }

    [Fact]
    public void Ssid_Byte_Layout_Per_Spec()
    {
        // C/H bit: bit 7
        // Reserved bits: bits 6, 5 - default "11" on write
        // SSID: bits 4..1
        // E bit: bit 0
        var addr = new Ax25Address(new Callsign("G7XYZ", 7), CrhBit: true, ExtensionBit: true);
        Span<byte> buf = stackalloc byte[Ax25Address.EncodedLength];
        addr.Write(buf);

        byte ssidByte = buf[6];
        ((ssidByte & 0x80) != 0).Should().BeTrue("C/H bit");
        ((ssidByte & 0x60) == 0x60).Should().BeTrue("reserved bits default 11");
        ((ssidByte >> 1) & 0x0F).Should().Be(7);
        ((ssidByte & 0x01) != 0).Should().BeTrue("E bit");
    }

    [Fact]
    public void Read_Accepts_All_Space_Address_Slot()
    {
        // Per AX.25 v2.2 §6.1.1 "operation with destination addresses other
        // than actual amateur call signs is a subject for further study" -
        // some implementations (BPQ's own ID beacon `>IS`, PD4R-12's QRV
        // broadcast) emit UI frames with an all-space dest or source slot.
        // The receive path accepts this as Callsign with Base="".
        Span<byte> buf = stackalloc byte[Ax25Address.EncodedLength];
        for (int i = 0; i < 6; i++)
        {
            buf[i] = 0x40;   // ' ' << 1
        }

        buf[6] = 0x60;   // C=0, R=11, SSID=0, E=0

        var addr = Ax25Address.Read(buf);
        addr.Callsign.Base.Should().Be("");
        addr.Callsign.Ssid.Should().Be((byte)0);
    }

    [Fact]
    public void Empty_Callsign_RoundTrips_Through_Wire_Form()
    {
        var input = new Ax25Address(new Callsign("", 12), CrhBit: false, ExtensionBit: true);
        Span<byte> buf = stackalloc byte[Ax25Address.EncodedLength];
        input.Write(buf);
        // All 6 callsign bytes should be the space-shifted padding byte.
        for (int i = 0; i < 6; i++)
        {
            buf[i].Should().Be((byte)(' ' << 1));
        }

        Ax25Address.Read(buf).Should().Be(input);
    }

    [Fact]
    public void Read_Rejects_Short_Span()
    {
        var buf = new byte[6];
        ((Action)(() => Ax25Address.Read(buf))).Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Write_Rejects_Short_Span()
    {
        var addr = new Ax25Address(new Callsign("G7XYZ", 0), false, false);
        var act = () =>
        {
            var buf = new byte[6];
            addr.Write(buf);
        };
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Read_Rejects_Non_Space_Character_After_Padding()
    {
        // Callsign characters are left-justified and space-padded; a non-space
        // appearing after a padding space (here 'A', space, 'B') is malformed.
        var buf = new byte[Ax25Address.EncodedLength];
        buf[0] = (byte)('A' << 1);
        buf[1] = (byte)(' ' << 1);   // padding starts
        buf[2] = (byte)('B' << 1);   // non-space after padding, invalid
        for (int i = 3; i < 6; i++)
        {
            buf[i] = (byte)(' ' << 1);
        }

        buf[6] = 0x61;   // C=0, R=11, SSID=0, E=1

        ((Action)(() => Ax25Address.Read(buf))).Should().Throw<ArgumentException>();
    }
}
