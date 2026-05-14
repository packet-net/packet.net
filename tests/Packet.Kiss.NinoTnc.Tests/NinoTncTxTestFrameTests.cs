using System.Text;
using Packet.Kiss;
using Packet.Kiss.NinoTnc.Firmware;

namespace Packet.Kiss.NinoTnc.Tests;

public class NinoTncTxTestFrameTests
{
    private static byte[] PayloadFor(string body) =>
        Encoding.ASCII.GetBytes("\x01\x02\x03prefix-garbage" + body);

    [Fact]
    public void Parses_All_Documented_Fields_From_A_Synthetic_Frame()
    {
        // Synthetic: board rev 04, DIP=0F (= 15, "Set from KISS"), firmware-mode
        // ZZZZ = 0023 → low byte 0x23 → catalog mode 14 (300 AFSKPLL IL2P+CRC).
        const string body = "=FirmwareVr:3.44=SerialNmbr:ABC123=UptimeMilS:0001A2B3" +
                            "=BrdSwchMod:040F0023=AX25RxPkts:0000007F=IL2PRxPkts:00000005" +
                            "=IL2PRxUnCr:00000001=TxPktCount:0000003E=PreamblCnt:00000041" +
                            "=LoopCycles:000A28F2=LostADCSmp:00000002";

        NinoTncTxTestFrame.TryParse(PayloadFor(body), out var parsed).Should().BeTrue();
        parsed.Should().NotBeNull();

        parsed!.FirmwareVersionRaw.Should().Be("3.44");
        parsed.FirmwareVersion.Should().Be(new NinoTncFirmwareVersion(3, 44));
        parsed.ChipVariant.Should().Be(NinoTncChipVariant.Dspic33Ep256);
        parsed.SerialNumber.Should().Be("ABC123");
        parsed.UptimeMs.Should().Be(0x0001A2B3);
        parsed.Uptime.Should().Be(TimeSpan.FromMilliseconds(0x0001A2B3));
        parsed.BoardRevision.Should().Be((byte)0x04);
        parsed.DipSwitchPosition.Should().Be((byte)0x0F);
        parsed.FirmwareModeByte.Should().Be((byte)0x23);
        parsed.RunningMode.Should().NotBeNull();
        parsed.RunningMode!.Value.Mode.Should().Be((byte)14);
        parsed.Ax25RxPackets.Should().Be(0x7F);
        parsed.Il2pRxPackets.Should().Be(5);
        parsed.Il2pRxUncorrectable.Should().Be(1);
        parsed.TxPacketCount.Should().Be(0x3E);
        parsed.PreambleCount.Should().Be(0x41);
        parsed.LoopCycles.Should().Be(0x000A28F2);
        parsed.LostAdcSamples.Should().Be(2);
    }

    [Fact]
    public void Returns_False_When_Marker_Missing()
    {
        var bytes = Encoding.ASCII.GetBytes("=SomethingElse:1234=FirmwareWrong:nope");
        NinoTncTxTestFrame.TryParse(bytes, out var parsed).Should().BeFalse();
        parsed.Should().BeNull();
    }

    [Fact]
    public void TryParse_From_KissFrame_Only_Succeeds_For_Data_Command()
    {
        var body = Encoding.ASCII.GetBytes("=FirmwareVr:3.44=BrdSwchMod:040F0002");

        // Right command — succeeds.
        var dataFrame = new KissFrame(0, KissCommand.Data, body);
        NinoTncTxTestFrame.TryParse(dataFrame, out var parsed).Should().BeTrue();
        parsed!.FirmwareVersionRaw.Should().Be("3.44");
        parsed.FirmwareVersion.Should().Be(new NinoTncFirmwareVersion(3, 44));
        parsed.RunningMode!.Value.Mode.Should().Be((byte)6); // 0x02 → mode 6, 1200 AFSK AX.25

        // Wrong command — fails even if payload would parse.
        var paramFrame = new KissFrame(0, KissCommand.SetHardware, body);
        NinoTncTxTestFrame.TryParse(paramFrame, out var paramParsed).Should().BeFalse();
        paramParsed.Should().BeNull();
    }

    [Fact]
    public void Tolerates_Truncated_BrdSwchMod()
    {
        // Only board rev + DIP, no firmware mode bytes.
        const string body = "=FirmwareVr:3.44=BrdSwchMod:0406";
        NinoTncTxTestFrame.TryParse(PayloadFor(body), out var parsed).Should().BeTrue();
        parsed!.BoardRevision.Should().BeNull(); // 4-char field is too short to extract any sub-bytes
        parsed.RunningMode.Should().BeNull();
    }

    [Fact]
    public void Empty_Serial_Number_Becomes_Null()
    {
        const string body = "=FirmwareVr:3.44=SerialNmbr:\0\0\0\0";
        NinoTncTxTestFrame.TryParse(PayloadFor(body), out var parsed).Should().BeTrue();
        parsed!.SerialNumber.Should().BeNull();
    }

    [Fact]
    public void Unparseable_FirmwareVr_Field_Degrades_Gracefully()
    {
        // Firmware emits some garbage in the version field (firmware
        // regression, encoding mishap, whatever). The parser must NOT
        // throw — it preserves the raw text and leaves the strong types
        // null/Unknown so callers can detect and report rather than
        // crash.
        const string body = "=FirmwareVr:banana=BrdSwchMod:040F0002";
        NinoTncTxTestFrame.TryParse(PayloadFor(body), out var parsed).Should().BeTrue();
        parsed!.FirmwareVersionRaw.Should().Be("banana");
        parsed.FirmwareVersion.Should().BeNull();
        parsed.ChipVariant.Should().Be(NinoTncChipVariant.Unknown);
    }

    [Fact]
    public void Missing_FirmwareVr_Field_Leaves_Version_Null()
    {
        // No =FirmwareVr: at all. The marker is the start of *parsing* —
        // without it, TryParse returns false. But if some future frame
        // shape has BrdSwchMod first and no firmware string, we'd want
        // graceful degradation. Today's parser fails outright, which is
        // fine — but the regression test pins the behaviour so we
        // notice if it changes.
        const string body = "=BrdSwchMod:040F0002";
        NinoTncTxTestFrame.TryParse(PayloadFor(body), out var parsed).Should().BeFalse();
        parsed.Should().BeNull();
    }
}
