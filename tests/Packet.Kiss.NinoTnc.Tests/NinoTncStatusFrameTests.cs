using System.Text;
using Packet.Kiss;
using Packet.Kiss.NinoTnc.Firmware;

namespace Packet.Kiss.NinoTnc.Tests;

public class NinoTncStatusFrameTests
{
    /// <summary>
    /// A complete periodic status report captured from a real NinoTNC
    /// (firmware 3.41, /dev/ttyACM0, 2026-07-02 bench session): a KISS Data
    /// frame with a fake AX.25 UI header <c>TNC&gt;USB</c> followed by the
    /// numeric <c>=II:</c> register run. Register 01 is eight raw zero
    /// bytes (identity unset).
    /// </summary>
    private static readonly byte[] CapturedStatusPayload = Convert.FromHexString(
        "AAA684404040E0A89C864040406103F0" +
        "3D30303A332E34313D30313A0000000000000000" +
        "3D30323A30304143384630383D30333A30303030303030343D30343A3030303030303046" +
        "3D30363A30303030303030323D30373A30303030303030443D30383A3030303030303034" +
        "3D30393A30303030303030303D30413A30303030303034393D30423A3030303030303136" +
        "3D30433A30343833434138323D30443A30303030463446363D30453A3030303134444241" +
        "3D30463A30303030303145333D31303A30303030304345343D31313A3030303030303030");

    /// <summary>
    /// A truncated report from the same bench session (the firmware cut the
    /// frame short mid-run, ending on a dangling <c>=</c> after register 0A).
    /// The parser must degrade gracefully, not throw.
    /// </summary>
    private static readonly byte[] CapturedTruncatedPayload = Convert.FromHexString(
        "AAA684404040E0A89C864040406103F0" +
        "3D30303A332E34313D30313A0000000000000000" +
        "3D30323A30303837454645363D30333A30303030303030343D30343A3030303030303046" +
        "3D30363A30303030303030323D30373A30303030303030443D30383A3030303030303034" +
        "3D30393A30303030303030303D30413A30303030303034393D");

    [Fact]
    public void Parses_Every_Register_From_A_Captured_Frame()
    {
        NinoTncStatusFrame.TryParse(CapturedStatusPayload, out var parsed).Should().BeTrue();
        parsed.Should().NotBeNull();

        parsed!.FirmwareVersionRaw.Should().Be("3.41");
        parsed.FirmwareVersion.Should().Be(new NinoTncFirmwareVersion(3, 41));
        parsed.SerialNumber.Should().BeNull("register 01 was all zero bytes");
        parsed.UptimeMs.Should().Be(0x00AC8F08);
        parsed.Uptime.Should().Be(TimeSpan.FromMilliseconds(0x00AC8F08));
        parsed.BoardId.Should().Be(4);
        parsed.DipSwitches.Should().Be((byte)0x0F);
        parsed.DipSwitchesBinary.Should().Be("1111");
        parsed.IsSoftwareControlMode.Should().BeTrue();
        parsed.FirmwareModeByte.Should().Be((byte)0x02);
        parsed.RunningMode!.Value.Mode.Should().Be((byte)6); // 0x02 → mode 6, 1200 AFSK AX.25
        parsed.Ax25RxPackets.Should().Be(0x0D);
        parsed.Il2pRxCorrectable.Should().Be(0x04);
        parsed.Il2pRxUncorrectable.Should().Be(0);
        parsed.TxPackets.Should().Be(0x49);
        parsed.PreambleWordCount.Should().Be(0x16);
        parsed.LoopCycles.Should().Be(0x0483CA82);
        parsed.PttOnMs.Should().Be(0xF4F6);
        parsed.DcdOnMs.Should().Be(0x14DBA);
        parsed.RxBytes.Should().Be(0x1E3);
        parsed.TxBytes.Should().Be(0xCE4);
        parsed.Il2pFecCorrectedBytes.Should().Be(0);
        parsed.RawRegisters.Should().HaveCount(17, "registers 00–11 minus the absent 05");
        parsed.RawRegisters.Should().NotContainKey(0x05);
    }

    [Fact]
    public void Firmware_341_Mode14_Byte_0x90_Resolves_To_A_Running_Mode()
    {
        // Firmware 3.41 reports mode 14 (300 AFSKPLL IL2P+CRC) in register
        // 06 as 0x90 (3.44 uses 0x23) — bench evidence: the 2026-07-03
        // wide-il2pc mode-survey runs. Status parsing must resolve it, not
        // report an unrecognised firmware byte.
        byte[] payload = [
            .. Encoding.ASCII.GetBytes("=00:3.41"),
            .. Encoding.ASCII.GetBytes("=06:00000090"),
        ];

        NinoTncStatusFrame.TryParse(payload, out var parsed).Should().BeTrue();
        parsed!.FirmwareModeByte.Should().Be((byte)0x90);
        parsed.RunningMode.Should().NotBeNull();
        parsed.RunningMode!.Value.Mode.Should().Be((byte)14);
        parsed.RunningMode.Value.Name.Should().Be("300 AFSKPLL IL2P+CRC");
    }

    [Fact]
    public void Truncated_Captured_Frame_Degrades_Gracefully()
    {
        NinoTncStatusFrame.TryParse(CapturedTruncatedPayload, out var parsed).Should().BeTrue();

        parsed!.FirmwareVersionRaw.Should().Be("3.41");
        parsed.UptimeMs.Should().Be(0x0087EFE6);
        parsed.Il2pRxUncorrectable.Should().Be(0);
        // Register 0A's value carries the dangling '=' the firmware cut the
        // frame on, so its hex parse fails and the typed value stays null.
        parsed.TxPackets.Should().BeNull();
        // Registers after the cut never arrived.
        parsed.PreambleWordCount.Should().BeNull();
        parsed.PttOnMs.Should().BeNull();
    }

    [Fact]
    public void Serial_Register_Is_Read_As_Eight_Raw_Bytes()
    {
        // Identity bytes may contain '=' / ':' — the parser must take them
        // positionally, not scan for the next separator.
        var payload = Encoding.ASCII.GetBytes("garbage=00:3.41=01:AB=:CD42=02:000003E8");

        NinoTncStatusFrame.TryParse(payload, out var parsed).Should().BeTrue();
        parsed!.SerialNumber.Should().Be("AB=:CD42");
        parsed.UptimeMs.Should().Be(1000);
    }

    [Fact]
    public void Payload_Without_The_Register_Zero_Marker_Is_Rejected()
    {
        var payload = Encoding.ASCII.GetBytes("=FirmwareVr:3.41=BrdSwchMod:040F0002");
        NinoTncStatusFrame.TryParse(payload, out var parsed).Should().BeFalse();
        parsed.Should().BeNull();
    }

    [Fact]
    public void TryParse_From_KissFrame_Only_Succeeds_For_Data_Command()
    {
        var dataFrame = new KissFrame(0, KissCommand.Data, CapturedStatusPayload);
        NinoTncStatusFrame.TryParse(dataFrame, out var parsed).Should().BeTrue();
        parsed!.FirmwareVersionRaw.Should().Be("3.41");

        var paramFrame = new KissFrame(0, KissCommand.SetHardware, CapturedStatusPayload);
        NinoTncStatusFrame.TryParse(paramFrame, out var rejected).Should().BeFalse();
        rejected.Should().BeNull();
    }

    [Fact]
    public void FromDiagnostic_Maps_The_Labelled_Fields_And_Leaves_The_Rest_Null()
    {
        // Firmware 3.41 answers GETALL with the labelled diagnostic; the
        // mapping is what lets GetAllAsync return one shape on any firmware.
        const string body = "=FirmwareVr:3.41=SerialNmbr:ABC123\0\0=UptimeMilS:00AA45B6" +
                            "=BrdSwchMod:040F0002=AX25RxPkts:0000000D=IL2PRxPkts:00000004" +
                            "=IL2PRxUnCr:00000000=TxPktCount:00000049=PreamblCnt:00000016" +
                            "=LoopCycles:0479B77A=LostADCSmp:00000000";
        NinoTncTxTestFrame.TryParse(Encoding.ASCII.GetBytes(body), out var diagnostic).Should().BeTrue();

        var status = NinoTncStatusFrame.FromDiagnostic(diagnostic!);

        status.FirmwareVersionRaw.Should().Be("3.41");
        status.SerialNumber.Should().Be("ABC123");
        status.UptimeMs.Should().Be(0x00AA45B6);
        status.BoardId.Should().Be(4);
        status.DipSwitches.Should().Be((byte)0x0F);
        status.IsSoftwareControlMode.Should().BeTrue();
        status.FirmwareModeByte.Should().Be((byte)0x02);
        status.RunningMode!.Value.Mode.Should().Be((byte)6);
        status.Ax25RxPackets.Should().Be(0x0D);
        status.Il2pRxCorrectable.Should().Be(4);
        status.Il2pRxUncorrectable.Should().Be(0);
        status.TxPackets.Should().Be(0x49);
        status.PreambleWordCount.Should().Be(0x16);
        status.LoopCycles.Should().Be(0x0479B77A);

        status.PttOnMs.Should().BeNull("the labelled diagnostic has no PTT-on register");
        status.DcdOnMs.Should().BeNull();
        status.RxBytes.Should().BeNull();
        status.TxBytes.Should().BeNull();
        status.Il2pFecCorrectedBytes.Should().BeNull();
    }

    [Fact]
    public void Delta_Between_Two_Snapshots_Is_Per_Register()
    {
        var before = new NinoTncStatusFrame
        {
            UptimeMs = 1_000,
            TxPackets = 10,
            PreambleWordCount = 100,
            RxBytes = 500,
        };
        var after = new NinoTncStatusFrame
        {
            UptimeMs = 4_000,
            TxPackets = 12,
            PreambleWordCount = 130,
            // RxBytes missing on the second snapshot.
        };

        var delta = NinoTncStatusDelta.Between(before, after);

        delta.UptimeMs.Should().Be(3_000);
        delta.TxPackets.Should().Be(2);
        delta.PreambleWordCount.Should().Be(30);
        delta.RxBytes.Should().BeNull("the register must be present on both snapshots");
        delta.Ax25RxPackets.Should().BeNull();
    }

    [Fact]
    public void Delta_PreambleSeconds_Applies_The_Words_Times_16_Over_Baud_Formula()
    {
        var delta = NinoTncStatusDelta.Between(
            new NinoTncStatusFrame { PreambleWordCount = 100 },
            new NinoTncStatusFrame { PreambleWordCount = 115 });

        // 15 words × 16 bits ÷ 1200 bit/s = 200 ms.
        delta.PreambleSeconds(1200).Should().BeApproximately(0.2, 1e-9);

        var act = () => delta.PreambleSeconds(0);
        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void Delta_PreambleSeconds_Is_Null_When_The_Register_Was_Missing()
    {
        var delta = NinoTncStatusDelta.Between(new NinoTncStatusFrame(), new NinoTncStatusFrame());
        delta.PreambleSeconds(1200).Should().BeNull();
    }

    [Fact]
    public void Classifier_Upgrades_A_Status_Frame()
    {
        var frame = new KissFrame(0, KissCommand.Data, CapturedStatusPayload);

        var evt = NinoTncFrameClassifier.Classify(frame);

        evt.Should().BeOfType<NinoTncStatusFrameReceivedEvent>();
        ((NinoTncStatusFrameReceivedEvent)evt).Status.UptimeMs.Should().Be(0x00AC8F08);
    }
}
