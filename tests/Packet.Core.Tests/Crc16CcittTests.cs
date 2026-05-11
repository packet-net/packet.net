using Packet.Core;

namespace Packet.Core.Tests;

public class Crc16CcittTests
{
    [Fact]
    public void Standard_CrcCatalog_Vector_123456789()
    {
        // CRC-16/X-25 standard check value per the CRC catalogue.
        var data = "123456789"u8.ToArray();
        Crc16Ccitt.Compute(data).ShouldBe((ushort)0x906E);
    }

    [Fact]
    public void Empty_Input_ProducesXorOut()
    {
        // Init (0xFFFF) XOR'd with XorOut (0xFFFF) yields 0x0000 on empty input.
        Crc16Ccitt.Compute(ReadOnlySpan<byte>.Empty).ShouldBe((ushort)0x0000);
    }

    [Fact]
    public void Single_Byte_Vectors()
    {
        // Sanity values to lock the bit-ordering — small but useful for
        // catching reflected/non-reflected mix-ups.
        Crc16Ccitt.Compute(new byte[] { 0x00 }).ShouldBe((ushort)0xF078);
        Crc16Ccitt.Compute(new byte[] { 0xFF }).ShouldBe((ushort)0xFF00);
    }

    [Fact]
    public void Result_Is_Deterministic()
    {
        // The same input must produce the same FCS regardless of how many
        // times we ask. Cheap protection against accidental state leakage.
        var data = new byte[] { 0xAA, 0x55, 0x12, 0x34, 0x56, 0x78 };
        var first = Crc16Ccitt.Compute(data);
        var second = Crc16Ccitt.Compute(data);
        first.ShouldBe(second);
    }
}
