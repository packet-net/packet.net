using Packet.Kiss;
using Packet.Kiss.NinoTnc;

namespace Packet.Kiss.NinoTnc.Tests;

public class NinoTncSetHardwareTests
{
    [Theory]
    [InlineData(0,  true,  0)]   // mode 0, persist  → 0
    [InlineData(0,  false, 16)]  // mode 0, !persist → 0 + 16
    [InlineData(6,  true,  6)]   // mode 6, persist  → 6
    [InlineData(6,  false, 22)]  // mode 6, !persist → 6 + 16
    [InlineData(15, true,  15)]  // mode 15, persist → 15
    [InlineData(15, false, 31)]  // mode 15, !persist → 31
    public void Payload_Byte_Matches_Kissproxy_Arithmetic(byte mode, bool persist, byte expectedPayload)
    {
        NinoTncSetHardware.BuildPayloadByte(mode, persist).ShouldBe(expectedPayload);
    }

    [Fact]
    public void Out_Of_Range_Mode_Throws()
    {
        Should.Throw<ArgumentOutOfRangeException>(() => NinoTncSetHardware.BuildPayloadByte(16, persistToFlash: true));
        Should.Throw<ArgumentOutOfRangeException>(() => NinoTncSetHardware.BuildPayloadByte(255, persistToFlash: false));
    }

    [Fact]
    public void Kiss_Frame_Encodes_As_Sethw_Command_With_Mode_Payload()
    {
        // mode 6 (1200 AFSK AX.25), persist=false → payload byte = 22 (0x16)
        // KISS frame: FEND, cmdByte=0x06 (port 0 + SetHardware 0x06), 0x16, FEND
        var frame = NinoTncSetHardware.BuildKissFrame(mode: 6, persistToFlash: false, port: 0);
        frame.ShouldBe(new byte[] { 0xC0, 0x06, 0x16, 0xC0 });
    }

    [Fact]
    public void Kiss_Frame_Uses_Port_Nibble()
    {
        // port 2, mode 6, persist=true → command byte = (2<<4)|0x06 = 0x26
        var frame = NinoTncSetHardware.BuildKissFrame(mode: 6, persistToFlash: true, port: 2);
        frame.ShouldBe(new byte[] { 0xC0, 0x26, 0x06, 0xC0 });
    }
}
