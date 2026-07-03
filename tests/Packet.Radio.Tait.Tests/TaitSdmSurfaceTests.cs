using Packet.Radio.Tait.Ccdi;

namespace Packet.Radio.Tait.Tests;

/// <summary>
/// The backlog-#5 CCDI surface: SDM variants (extended / binary / legacy), the FUNCTION
/// extras, the CCR-over-SDM builder, and the extended side-channel budget. Wire literals
/// marked "bench capture" were taken verbatim from the 2026-07-03 TM8110 hardware session.
/// </summary>
public class TaitSdmSurfaceTests
{
    private const string Prompt = ".";

    // ---------- SDM sends ----------

    [Fact]
    public async Task Plain_Sdm_Sends_Gfi2_Sfi00()
    {
        using var io = new FakeSerialIo();
        io.RespondTo("a1205200PDN00001HELLOFE", Prompt);
        await using var radio = TaitCcdiRadio.OpenForTest(io);

        await radio.SendSdmAsync("PDN00001", "HELLO");

        io.WrittenAscii.Should().Contain("a1205200PDN00001HELLOFE\r");
    }

    [Fact]
    public async Task Extended_Sdm_Sends_Sfi04_In_One_Command()
    {
        // Radio-splits-and-reassembles semantics, hardware-verified (100 and 128 chars).
        string message = new('X', 100);
        string wire = new CcdiFrame('a', $"05204PDN00001{message}").Encode();
        using var io = new FakeSerialIo();
        io.RespondTo(wire, Prompt);
        await using var radio = TaitCcdiRadio.OpenForTest(io);

        await radio.SendExtendedSdmAsync("PDN00001", message);

        io.WrittenAscii.Should().Contain(wire + "\r");
    }

    [Fact]
    public async Task Extended_Sdm_Rejects_Over_128_Characters()
    {
        using var io = new FakeSerialIo();
        await using var radio = TaitCcdiRadio.OpenForTest(io);

        var act = async () => await radio.SendExtendedSdmAsync("PDN00001", new string('X', 129));

        await act.Should().ThrowAsync<ArgumentException>();
        io.WrittenAscii.Should().BeEmpty();
    }

    [Fact]
    public async Task Binary_Sdm_Sends_Gfi1_Bytes_Verbatim()
    {
        // Bench capture: a1105100PDN00001<01>B<7F><FE>B4 — delivered verbatim over air.
        using var io = new FakeSerialIo();
        io.RespondTo("a1105100PDN00001\u0001B\u007F\u00FEB4", Prompt);
        await using var radio = TaitCcdiRadio.OpenForTest(io);

        await radio.SendBinarySdmAsync("PDN00001", new byte[] { 0x01, 0x42, 0x7F, 0xFE });

        io.WrittenAscii.Should().Contain("a1105100PDN00001\u0001B\u007F\u00FEB4\r");
    }

    [Fact]
    public async Task Binary_Sdm_Over_32_Bytes_Uses_Extended_Sfi04()
    {
        var payload = new byte[33];
        Array.Fill(payload, (byte)'A');
        string wire = new CcdiFrame('a', $"05104PDN00001{new string('A', 33)}").Encode();
        using var io = new FakeSerialIo();
        io.RespondTo(wire, Prompt);
        await using var radio = TaitCcdiRadio.OpenForTest(io);

        await radio.SendBinarySdmAsync("PDN00001", payload);

        io.WrittenAscii.Should().Contain(wire + "\r");
    }

    [Theory]
    [InlineData(0x0D)] // CR — CCDI frame terminator
    [InlineData(0x0A)] // LF
    [InlineData(0x11)] // XON
    [InlineData(0x13)] // XOFF
    public async Task Binary_Sdm_Refuses_Framing_Hazard_Bytes(byte hazardous)
    {
        using var io = new FakeSerialIo();
        await using var radio = TaitCcdiRadio.OpenForTest(io);

        var act = async () => await radio.SendBinarySdmAsync("PDN00001", new byte[] { 0x41, hazardous });

        (await act.Should().ThrowAsync<ArgumentException>())
            .Which.Message.Should().Contain("framing");
        io.WrittenAscii.Should().BeEmpty();
    }

    [Fact]
    public async Task Legacy_Sdm_Sends_The_S_Command()
    {
        // Bench capture: s1205PDN00001LEGACY-SBD — delivered, RINGed, auto-acknowledged.
        using var io = new FakeSerialIo();
        io.RespondTo("s1205PDN00001LEGACY-SBD", Prompt);
        await using var radio = TaitCcdiRadio.OpenForTest(io);

        await radio.SendLegacySdmAsync("PDN00001", "LEGACY-S");

        io.WrittenAscii.Should().Contain("s1205PDN00001LEGACY-SBD\r");
    }

    [Theory]
    [InlineData("12345678", "Hi", 5100, "s0CFF12345678Hi39")] // §1.9.7 worked example
    [InlineData("12345678", "", 100, "s0A051234567813")]      // §1.9.7 worked example (no message)
    public async Task Legacy_Sdm_Matches_Manual_Examples(string id, string message, int leadInMs, string wire)
    {
        using var io = new FakeSerialIo();
        io.RespondTo(wire, Prompt);
        await using var radio = TaitCcdiRadio.OpenForTest(io);

        await radio.SendLegacySdmAsync(id, message, TimeSpan.FromMilliseconds(leadInMs));

        io.WrittenAscii.Should().Contain(wire + "\r");
    }

    // ---------- CCR-over-SDM (construction + explicitly-unsafe send) ----------

#pragma warning disable PKTTAIT001 // experimental: exercised deliberately by these tests

    [Fact]
    public void CcrOverSdm_Frame_Matches_The_Manual_Example()
    {
        // §1.9.8 worked example: a130520312345678M01D0E36 carries CCR command M01D0E
        // (monitor on) to ID 12345678 with a 100 ms lead-in.
        var frame = TaitCcdiRadio.UnsafeBuildCcrOverSdmFrame("12345678", new CcdiFrame('M', "D"));

        frame.Encode().Should().Be("a130520312345678M01D0E36");
    }

    [Fact]
    public void CcrOverSdm_Frame_Rejects_Oversize_Commands()
    {
        var act = () => TaitCcdiRadio.UnsafeBuildCcrOverSdmFrame(
            "12345678", new CcdiFrame('S', new string('1', 33)));

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public async Task CcrOverSdm_Send_Transmits_The_Built_Frame()
    {
        using var io = new FakeSerialIo();
        io.RespondTo("a130520312345678M01D0E36", Prompt);
        await using var radio = TaitCcdiRadio.OpenForTest(io);

        await radio.UnsafeSendCcrOverSdmAsync("12345678", new CcdiFrame('M', "D"));

        io.WrittenAscii.Should().Contain("a130520312345678M01D0E36\r");
    }

#pragma warning restore PKTTAIT001

    // ---------- FUNCTION extras ----------

    [Theory]
    [InlineData("volume-control on", "f03011A5")]
    [InlineData("volume-control off", "f03010A6")]
    [InlineData("volume 0", "f03020A5")]
    [InlineData("volume 5", "f03025A0")]
    [InlineData("volume 25", "f0402256D")]
    [InlineData("selcall-ring on", "f03031A3")]
    [InlineData("selcall-ring off", "f03030A4")]
    [InlineData("keypress on", "f03042A1")]
    [InlineData("keypress off", "f03043A0")]
    [InlineData("channel-progress on", "f03051A1")]
    [InlineData("channel-progress off", "f03050A2")]
    [InlineData("sdm-output on", "f03101A5")]
    [InlineData("sdm-output off", "f03100A6")]
    [InlineData("callerid-encode on", "f03111A4")]
    [InlineData("callerid-encode off", "f03110A5")]
    [InlineData("callerid-decode on", "f03121A3")]
    [InlineData("callerid-decode off", "f03120A4")]
    [InlineData("controls disable-all", "f0240D4")]
    [InlineData("controls input-only", "f0241D3")]
    [InlineData("controls enable-all", "f0242D2")]
    [InlineData("subaudible on", "f0271D0")]
    [InlineData("subaudible off", "f0270D1")]
    [InlineData("keypress PTT 3/8s", "f04300370")]
    [InlineData("keypress softkey-left hold", "f0431E954")]
    public async Task Function_Extras_Send_The_Documented_Wire_Forms(string which, string wire)
    {
        using var io = new FakeSerialIo();
        io.RespondTo(wire, Prompt);
        await using var radio = TaitCcdiRadio.OpenForTest(io);

        Task call = which switch
        {
            "volume-control on" => radio.SetVolumeControlAsync(true),
            "volume-control off" => radio.SetVolumeControlAsync(false),
            "volume 0" => radio.SetVolumeAsync(0),
            "volume 5" => radio.SetVolumeAsync(5),
            "volume 25" => radio.SetVolumeAsync(25),
            "selcall-ring on" => radio.SetSelcallRingOutputAsync(true),
            "selcall-ring off" => radio.SetSelcallRingOutputAsync(false),
            "keypress on" => radio.SetKeypressProgressMessagesAsync(true),
            "keypress off" => radio.SetKeypressProgressMessagesAsync(false),
            "channel-progress on" => radio.SetChannelProgressMessagesAsync(true),
            "channel-progress off" => radio.SetChannelProgressMessagesAsync(false),
            "sdm-output on" => radio.SetSdmOutputOnReceptionAsync(true),
            "sdm-output off" => radio.SetSdmOutputOnReceptionAsync(false),
            "callerid-encode on" => radio.SetSdmCallerIdEncodeAsync(true),
            "callerid-encode off" => radio.SetSdmCallerIdEncodeAsync(false),
            "callerid-decode on" => radio.SetSdmCallerIdDecodeAsync(true),
            "callerid-decode off" => radio.SetSdmCallerIdDecodeAsync(false),
            "controls disable-all" => radio.SetUserControlsAsync(TaitUserControls.DisableAll),
            "controls input-only" => radio.SetUserControlsAsync(TaitUserControls.DisableInputOnly),
            "controls enable-all" => radio.SetUserControlsAsync(TaitUserControls.EnableAll),
            "subaudible on" => radio.SetSubaudibleValidationAsync(true),
            "subaudible off" => radio.SetSubaudibleValidationAsync(false),
            "keypress PTT 3/8s" => radio.SimulateKeyPressAsync(TaitKey.Ptt, 3),
            "keypress softkey-left hold" => radio.SimulateKeyPressAsync(TaitKey.SoftkeyLeft, 9),
            _ => throw new InvalidOperationException(which),
        };
        await call;

        io.WrittenAscii.Should().Contain(wire + "\r");
    }

    [Fact]
    public async Task Volume_And_KeyPress_Validate_Ranges()
    {
        using var io = new FakeSerialIo();
        await using var radio = TaitCcdiRadio.OpenForTest(io);

        await ((Func<Task>)(() => radio.SetVolumeAsync(26))).Should().ThrowAsync<ArgumentOutOfRangeException>();
        await ((Func<Task>)(() => radio.SetVolumeAsync(-1))).Should().ThrowAsync<ArgumentOutOfRangeException>();
        await ((Func<Task>)(() => radio.SimulateKeyPressAsync(TaitKey.Ptt, 10))).Should().ThrowAsync<ArgumentOutOfRangeException>();
        io.WrittenAscii.Should().BeEmpty();
    }

    // ---------- unsolicited pushed SDMs (FUNCTION 1/0) ----------

    [Fact]
    public async Task Unsolicited_GetSdm_Message_Raises_SdmReceived()
    {
        using var io = new FakeSerialIo();
        await using var radio = TaitCcdiRadio.OpenForTest(io);
        var received = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        radio.SdmReceived += (_, sdm) => received.TrySetResult(sdm.Data);

        io.Enqueue(".s02Hi7A\r.");

        (await received.Task.WaitAsync(TimeSpan.FromSeconds(2))).Should().Be("Hi");
    }

    // ---------- side channel: extended budget ----------

    [Fact]
    public async Task SideChannel_Budget_Is_32_By_Default_And_128_With_Extended_Enabled()
    {
        using var io = new FakeSerialIo();
        await using var radio = TaitCcdiRadio.OpenForTest(io);

        using var plain = new TaitSdmSideChannel(radio);
        plain.MaxPayloadLength.Should().Be(32);

        using var extended = new TaitSdmSideChannel(
            radio, new TaitSdmSideChannelOptions { EnableExtendedSdm = true });
        extended.MaxPayloadLength.Should().Be(128);
    }

    [Fact]
    public async Task SideChannel_Routes_Oversize_Payloads_Through_Extended_Sdm()
    {
        string payload = new('Y', 33);
        string wire = new CcdiFrame('a', $"05204PDN00001{payload}").Encode();
        using var io = new FakeSerialIo();
        io.RespondTo(wire, Prompt);
        await using var radio = TaitCcdiRadio.OpenForTest(io);
        using var channel = new TaitSdmSideChannel(
            radio, new TaitSdmSideChannelOptions { EnableExtendedSdm = true });

        await channel.SendAsync("PDN00001", payload);

        io.WrittenAscii.Should().Contain(wire + "\r");
    }

    [Fact]
    public async Task SideChannel_Keeps_Short_Payloads_On_Plain_Sdm()
    {
        using var io = new FakeSerialIo();
        io.RespondTo("a1205200PDN00001HELLOFE", Prompt);
        await using var radio = TaitCcdiRadio.OpenForTest(io);
        using var channel = new TaitSdmSideChannel(
            radio, new TaitSdmSideChannelOptions { EnableExtendedSdm = true });

        await channel.SendAsync("PDN00001", "HELLO");

        io.WrittenAscii.Should().Contain("a1205200PDN00001HELLOFE\r");
    }

    [Fact]
    public async Task SideChannel_Rejects_Payloads_Over_Its_Budget()
    {
        using var io = new FakeSerialIo();
        await using var radio = TaitCcdiRadio.OpenForTest(io);

        using var plain = new TaitSdmSideChannel(radio);
        var actPlain = async () => await plain.SendAsync("PDN00001", new string('Y', 33));
        await actPlain.Should().ThrowAsync<ArgumentException>();

        using var extended = new TaitSdmSideChannel(
            radio, new TaitSdmSideChannelOptions { EnableExtendedSdm = true });
        var actExtended = async () => await extended.SendAsync("PDN00001", new string('Y', 129));
        await actExtended.Should().ThrowAsync<ArgumentException>();

        io.WrittenAscii.Should().BeEmpty();
    }
}
