using Packet.Kiss.NinoTnc;

namespace Packet.Kiss.NinoTnc.Tests;

public class NinoTncCatalogTests
{
    [Fact]
    public void Catalog_Has_All_Sixteen_Modes()
    {
        NinoTncCatalog.ByMode.Count.ShouldBe(16);
        for (byte i = 0; i <= 15; i++)
        {
            NinoTncCatalog.ByMode.ContainsKey(i).ShouldBeTrue($"mode {i} should be in the catalog");
        }
    }

    [Theory]
    [InlineData(0,  "9600 GFSK AX.25",      9600)]
    [InlineData(1,  "19200 4FSK",          19200)]
    [InlineData(2,  "9600 GFSK IL2P+CRC",   9600)]
    [InlineData(6,  "1200 AFSK AX.25",      1200)]
    [InlineData(8,  "300 BPSK IL2P+CRC",     300)]
    [InlineData(14, "300 AFSKPLL IL2P+CRC",  300)]
    [InlineData(15, "Set from KISS",           0)]
    public void Catalog_Matches_Kissproxy_Source(byte mode, string expectedName, int expectedBitRate)
    {
        var entry = NinoTncCatalog.ByMode[mode];
        entry.Name.ShouldBe(expectedName);
        entry.BitRateHz.ShouldBe(expectedBitRate);
    }

    [Theory]
    [InlineData(0x00, 0)]    // 9600 GFSK AX.25
    [InlineData(0x41, 1)]    // 19200 4FSK
    [InlineData(0x02, 6)]    // 1200 AFSK AX.25
    [InlineData(0xF3, 15)]   // Set from KISS
    public void Firmware_Byte_Lookup_Resolves_To_Correct_Mode(byte firmwareByte, byte expectedMode)
    {
        var resolved = NinoTncCatalog.TryGetByFirmwareByte(firmwareByte);
        resolved.ShouldNotBeNull();
        resolved!.Value.Mode.ShouldBe(expectedMode);
    }

    [Fact]
    public void Firmware_Byte_Lookup_Returns_Null_For_Unknown_Byte()
    {
        NinoTncCatalog.TryGetByFirmwareByte(0xFF).ShouldBeNull();
    }

    [Fact]
    public void TransmissionMs_Matches_Kissproxy_Formula()
    {
        // kissproxy: (frameBytes * 8.0 / BitRateHz) * 1000.0
        // 256 bytes at 1200 baud → 256 * 8 / 1200 * 1000 = 1706.666… ms
        var mode = NinoTncCatalog.ByMode[6]; // 1200 AFSK AX.25
        mode.TransmissionMs(256).ShouldBe(1706.666666666666, tolerance: 0.001);
    }

    [Fact]
    public void TransmissionMs_For_Variable_Rate_Mode_Is_Infinity()
    {
        var mode = NinoTncCatalog.ByMode[15]; // Set from KISS, BitRate = 0
        double.IsPositiveInfinity(mode.TransmissionMs(100)).ShouldBeTrue();
    }
}
