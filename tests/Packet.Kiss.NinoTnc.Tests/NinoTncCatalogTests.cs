using Packet.Kiss.NinoTnc;

namespace Packet.Kiss.NinoTnc.Tests;

public class NinoTncCatalogTests
{
    [Fact]
    public void Catalog_Has_All_Sixteen_Modes()
    {
        NinoTncCatalog.ByMode.Count.Should().Be(16);
        for (byte i = 0; i <= 15; i++)
        {
            NinoTncCatalog.ByMode.ContainsKey(i).Should().BeTrue($"mode {i} should be in the catalog");
        }
    }

    [Theory]
    [InlineData(0, "9600 GFSK AX.25", 9600)]
    [InlineData(1, "19200 C4FSK IL2P+CRC", 19200)]
    [InlineData(2, "9600 GFSK IL2P+CRC", 9600)]
    [InlineData(3, "9600 C4FSK IL2P+CRC", 9600)]
    [InlineData(4, "4800 GFSK IL2P+CRC", 4800)]
    [InlineData(5, "3600 QPSK IL2P+CRC", 3600)]
    [InlineData(6, "1200 AFSK AX.25", 1200)]
    [InlineData(7, "1200 AFSK IL2P+CRC", 1200)]
    [InlineData(8, "300 BPSK IL2P+CRC", 300)]
    [InlineData(9, "600 QPSK IL2P+CRC", 600)]
    [InlineData(10, "1200 BPSK IL2P+CRC", 1200)]
    [InlineData(11, "2400 QPSK IL2P+CRC", 2400)]
    [InlineData(12, "300 AFSK AX.25", 300)]
    [InlineData(13, "300 AFSK IL2P", 300)]
    [InlineData(14, "300 AFSK IL2P+CRC", 300)]
    [InlineData(15, "Set from KISS", 0)]
    public void Catalog_Matches_Ninos_V44_Mode_Table(byte mode, string expectedName, int expectedBitRate)
    {
        // Every DIP position, checked against Nino's own v44 table
        // (flashtnc/v44-op-modes.png + the "MODE SWITCH MAPPING v3/4.43"
        // block in release-notes.txt). Two names diverge from the
        // kissproxy source this catalog was ported from, because kissproxy
        // predates firmware 3/4.42: modes 1/3 are C4FSK (coherent 4-level
        // FSK), and modes 13/14 are plain "300 AFSK" — upstream retired the
        // "AFSKPLL" spelling once every 300 AFSK mode gained coherent
        // demodulation. We keep "IL2P+CRC" where upstream writes "IL2Pc":
        // same protocol, and it is what this codebase's filters match on.
        var entry = NinoTncCatalog.ByMode[mode];
        entry.Name.Should().Be(expectedName);
        entry.BitRateHz.Should().Be(expectedBitRate);
    }

    [Fact]
    public void Both_4fsk_Modes_Carry_The_Il2p_Crc_Protocol_In_Their_Name()
    {
        NinoTncCatalog.ByMode[1].Name.Should().Contain("IL2P+CRC");
        NinoTncCatalog.ByMode[3].Name.Should().Contain("IL2P+CRC");
    }

    [Theory]
    [InlineData(0x00, 0)]    // 9600 GFSK AX.25
    [InlineData(0x41, 1)]    // 19200 C4FSK IL2P+CRC
    [InlineData(0x02, 6)]    // 1200 AFSK AX.25
    [InlineData(0x23, 14)]   // 300 AFSK IL2P+CRC (3.44 byte)
    [InlineData(0xF3, 15)]   // Set from KISS
    public void Firmware_Byte_Lookup_Resolves_To_Correct_Mode(byte firmwareByte, byte expectedMode)
    {
        var resolved = NinoTncCatalog.TryGetByFirmwareByte(firmwareByte);
        resolved.Should().NotBeNull();
        resolved!.Value.Mode.Should().Be(expectedMode);
    }

    [Fact]
    public void Firmware_341_Mode14_Alias_0x90_Resolves_Alongside_The_344_Byte()
    {
        // Firmware 3.41 reports mode 14 (300 AFSK IL2P+CRC) as 0x90;
        // 3.44 reports 0x23. Bench evidence: the 2026-07-03 wide-il2pc
        // mode-survey runs — GETALL verify read "unrecognised firmware byte
        // 0x90" while the decoding 300 AFSK traffic proved mode 14 was
        // engaged. Both bytes must resolve to the same catalog entry.
        var via341 = NinoTncCatalog.TryGetByFirmwareByte(0x90);
        via341.Should().NotBeNull();
        via341!.Value.Mode.Should().Be((byte)14);
        via341.Value.Name.Should().Be("300 AFSK IL2P+CRC");
        via341.Should().Be(NinoTncCatalog.TryGetByFirmwareByte(0x23));
    }

    [Fact]
    public void Firmware_Byte_Lookup_Returns_Null_For_Unknown_Byte()
    {
        NinoTncCatalog.TryGetByFirmwareByte(0xFF).Should().BeNull();
    }

    [Fact]
    public void TransmissionMs_Matches_Kissproxy_Formula()
    {
        // kissproxy: (frameBytes * 8.0 / BitRateHz) * 1000.0
        // 256 bytes at 1200 baud → 256 * 8 / 1200 * 1000 = 1706.666… ms
        var mode = NinoTncCatalog.ByMode[6]; // 1200 AFSK AX.25
        mode.TransmissionMs(256).Should().BeApproximately(1706.666666666666, 0.001);
    }

    [Fact]
    public void TransmissionMs_For_Variable_Rate_Mode_Is_Infinity()
    {
        var mode = NinoTncCatalog.ByMode[15]; // Set from KISS, BitRate = 0
        double.IsPositiveInfinity(mode.TransmissionMs(100)).Should().BeTrue();
    }
}
