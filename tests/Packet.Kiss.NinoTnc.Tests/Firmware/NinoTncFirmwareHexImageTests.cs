using System.Text;
using Packet.Kiss.NinoTnc.Firmware;

namespace Packet.Kiss.NinoTnc.Tests.Firmware;

public class NinoTncFirmwareHexImageTests
{
    // A minimal but structurally valid image around one of the known
    // EP256 bootloader fingerprint lines.
    internal const string Ep256Magic = ":102800002f08b000889fbe008a9fbe008c9fbe002c";
    internal const string Ep512Magic = ":108800002f08b000889fbe008a9fbe008c9fbe00cc";

    internal static byte[] TinyImage(string magicLine, string newline = "\n") =>
        Encoding.ASCII.GetBytes(string.Join(newline,
            ":020000040000fa",
            magicLine,
            ":040008008606000068",
            ":00000001FF") + newline);

    [Theory]
    [InlineData(":108800007a00fa0000002200000f7800c3e8a900f7", NinoTncChipVariant.Dspic33Ep512)]
    [InlineData(":10427c007a00fa0000002200000f7800c3e8a900c1", NinoTncChipVariant.Dspic33Ep256)]
    [InlineData(":10427c007a00fa00403f9800ce389000010f780089", NinoTncChipVariant.Dspic33Ep256)]
    [InlineData(":10427c007c00fa00503f980000002200000f7800ec", NinoTncChipVariant.Dspic33Ep256)]
    [InlineData(":102800007c00fa00503f980000002200000f780082", NinoTncChipVariant.Dspic33Ep256)]
    [InlineData(":102800002f08b000889fbe008a9fbe008c9fbe002c", NinoTncChipVariant.Dspic33Ep256)]
    [InlineData(":108800002f08b000889fbe008a9fbe008c9fbe00cc", NinoTncChipVariant.Dspic33Ep512)]
    public void Each_Known_Bootloader_Line_Classifies_Its_Chip(string magic, NinoTncChipVariant expected)
    {
        NinoTncFirmwareHexImage.Parse(TinyImage(magic)).TargetChip.Should().Be(expected);
    }

    [Fact]
    public void Classification_Is_Case_Insensitive()
    {
        NinoTncFirmwareHexImage.Parse(TinyImage(Ep256Magic.ToUpperInvariant()))
            .TargetChip.Should().Be(NinoTncChipVariant.Dspic33Ep256);
    }

    [Fact]
    public void Windows_Line_Endings_Parse_To_The_Same_Lines()
    {
        var image = NinoTncFirmwareHexImage.Parse(TinyImage(Ep256Magic, newline: "\r\n"));
        image.Lines.Should().HaveCount(4);
        image.Lines[^1].Should().Be(":00000001FF");
    }

    [Fact]
    public void An_Image_With_No_Known_Fingerprint_Is_Refused()
    {
        var bytes = Encoding.ASCII.GetBytes(":020000040000fa\n:00000001FF\n");
        var act = () => NinoTncFirmwareHexImage.Parse(bytes);

        act.Should().Throw<NinoTncFlashException>()
            .Which.Failure.Should().Be(NinoTncFlashFailure.HexTargetUnknown);
    }

    [Fact]
    public void An_Empty_Image_Is_Refused()
    {
        var act = () => NinoTncFirmwareHexImage.Parse(ReadOnlyMemory<byte>.Empty);

        act.Should().Throw<NinoTncFlashException>()
            .Which.Failure.Should().Be(NinoTncFlashFailure.HexImageInvalid);
    }

    [Fact]
    public void A_Line_Without_The_Record_Mark_Is_Refused()
    {
        var bytes = Encoding.ASCII.GetBytes($":020000040000fa\ngarbage\n{Ep256Magic}\n:00000001FF\n");
        var act = () => NinoTncFirmwareHexImage.Parse(bytes);

        act.Should().Throw<NinoTncFlashException>()
            .Which.Failure.Should().Be(NinoTncFlashFailure.HexImageInvalid);
    }

    [Fact]
    public void A_Non_Hex_Character_Is_Refused()
    {
        var bytes = Encoding.ASCII.GetBytes($":02000zZ40000fa\n{Ep256Magic}\n:00000001FF\n");
        var act = () => NinoTncFirmwareHexImage.Parse(bytes);

        act.Should().Throw<NinoTncFlashException>()
            .Which.Failure.Should().Be(NinoTncFlashFailure.HexImageInvalid);
    }

    [Fact]
    public void A_Binary_File_Is_Refused()
    {
        var act = () => NinoTncFirmwareHexImage.Parse(new byte[] { 0x00, 0x01, 0xFF, 0xC0 });

        act.Should().Throw<NinoTncFlashException>()
            .Which.Failure.Should().Be(NinoTncFlashFailure.HexImageInvalid);
    }

    [Fact]
    public void An_Image_Without_The_End_Of_File_Record_Is_Refused()
    {
        // Without :00000001FF the bootloader would never send 'Z' — the
        // flash could not complete and the modem would be stranded.
        var bytes = Encoding.ASCII.GetBytes($":020000040000fa\n{Ep256Magic}\n");
        var act = () => NinoTncFirmwareHexImage.Parse(bytes);

        act.Should().Throw<NinoTncFlashException>()
            .Which.Failure.Should().Be(NinoTncFlashFailure.HexImageInvalid);
    }
}
