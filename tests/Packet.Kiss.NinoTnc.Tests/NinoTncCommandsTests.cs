using Packet.Kiss;

namespace Packet.Kiss.NinoTnc.Tests;

public class NinoTncCommandsTests
{
    private const byte Fend = 0xC0;

    [Fact]
    public void GetAll_Wire_Frame_Is_Cmd_0x0B_Payload_0x00()
    {
        NinoTncCommands.BuildGetAllKissFrame().Should().Equal(Fend, 0x0B, 0x00, Fend);
    }

    [Fact]
    public void GetVersion_Wire_Frame_Is_Cmd_0x08_Payload_0x00()
    {
        NinoTncCommands.BuildGetVersionKissFrame().Should().Equal(Fend, 0x08, 0x00, Fend);
    }

    [Fact]
    public void StopTx_Wire_Frame_Is_Cmd_0x09_Payload_0x00()
    {
        NinoTncCommands.BuildStopTxKissFrame().Should().Equal(Fend, 0x09, 0x00, Fend);
    }

    [Fact]
    public void SetBeaconInterval_Wire_Frame_Is_Cmd_0x09_Payload_0xF0_Minutes()
    {
        NinoTncCommands.BuildSetBeaconIntervalKissFrame(minutes: 10).Should().Equal(Fend, 0x09, 0xF0, 0x0A, Fend);
    }

    [Fact]
    public void GetRssi_Wire_Frame_Is_Cmd_0x09_Payload_0xA7()
    {
        NinoTncCommands.BuildGetRssiKissFrame().Should().Equal(Fend, 0x09, 0xA7, Fend);
    }

    [Fact]
    public void Bootloader_Entry_Wire_Frame_Is_Cmd_0x0D_Payload_0x37()
    {
        // The exact bytes flashtnc.py sends to reboot the TNC into the
        // dsPIC bootloader — hardware-validated wire form.
        NinoTncCommands.BuildBootloaderEntryKissFrame().Should().Equal(Fend, 0x0D, 0x37, Fend);
    }

    [Fact]
    public void Bare_GetAll_Wire_Frame_Is_Cmd_0x0B_With_No_Payload()
    {
        // The payload-less GETALL flashtnc.py uses as its fill-and-flush
        // probe before entering the bootloader.
        NinoTncCommands.BuildBareGetAllKissFrame().Should().Equal(Fend, 0x0B, Fend);
    }

    [Fact]
    public void IsReply_Requires_The_0xE0_Command_Byte()
    {
        // 0xE0 decodes as port 14 + command 0x0 through the generic decoder.
        NinoTncCommands.IsReply(new KissFrame(14, KissCommand.Data, [])).Should().BeTrue();
        NinoTncCommands.IsReply(new KissFrame(0, KissCommand.Data, [])).Should().BeFalse();
        NinoTncCommands.IsReply(new KissFrame(14, KissCommand.SetHardware, [])).Should().BeFalse();
    }
}
