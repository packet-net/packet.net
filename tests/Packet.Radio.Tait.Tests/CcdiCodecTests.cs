using Packet.Radio.Tait.Ccdi;

namespace Packet.Radio.Tait.Tests;

public class CcdiCodecTests
{
    // §1.8.5 worked example: s0D050800TESTHi! → DA
    [Theory]
    [InlineData("s0D050800TESTHi!", "DA")]
    [InlineData("q00", "2F")]      // §1.8.4 minimum-length example q002F
    [InlineData("f0291", "CE")]    // §1.9.3 example: activate transmitter
    [InlineData("f03041", "A2")]   // §1.9.3 example: enable progress messages
    [InlineData("q045063", "5D")]  // §1.10.1 example: query averaged RSSI
    public void Checksum_Matches_Manual_Examples(string body, string expected)
    {
        CcdiChecksum.Compute(body).Should().Be(expected);
        CcdiChecksum.IsValid(body, expected).Should().BeTrue();
    }

    [Theory]
    [InlineData('q', "", "q002F")]
    [InlineData('q', "5064", "q0450645C")]
    [InlineData('f', "91", "f0291CE")]
    [InlineData('f', "041", "f03041A2")]
    public void Frame_Encodes_To_Wire_Form(char ident, string parameters, string wire)
    {
        new CcdiFrame(ident, parameters).Encode().Should().Be(wire);
    }

    [Fact]
    public void Frame_EncodeToBytes_Appends_Carriage_Return()
    {
        var bytes = new CcdiFrame('q', "").EncodeToBytes();
        bytes.Should().Equal("q002F\r"u8.ToArray());
    }

    [Theory]
    [InlineData("j07064-456C9", 'j', "064-456")] // §1.10.1 example: raw RSSI −45.6 dBm
    [InlineData("m0813203.02A2", 'm', "13203.02")] // live TM8110 capture
    [InlineData("p0205C9", 'p', "05")]           // live capture: receiver busy
    public void Frame_Parses_Valid_Lines(string line, char ident, string parameters)
    {
        CcdiFrame.TryParse(line, out var frame).Should().BeTrue();
        frame.Ident.Should().Be(ident);
        frame.Parameters.Should().Be(parameters);
    }

    [Theory]
    [InlineData("")]
    [InlineData("q002")]           // too short
    [InlineData("j07064-456C8")]   // checksum off by one
    [InlineData("j08064-456C9")]   // size doesn't match parameter length
    [InlineData("jZZ064-456C9")]   // size not hex
    public void Frame_Rejects_Corrupt_Lines(string line)
    {
        CcdiFrame.TryParse(line, out _).Should().BeFalse();
    }

    [Fact]
    public void Model_Message_Decodes_Type_Model_Tier_And_Version()
    {
        CcdiFrame.TryParse("m0813203.02A2", out var frame).Should().BeTrue();
        var message = CcdiMessage.Decode(frame).Should().BeOfType<CcdiModelMessage>().Subject;
        message.RuType.Should().Be('1');
        message.RuModel.Should().Be('3');
        message.RuTier.Should().Be('2');
        message.CcdiVersion.Should().Be("03.02");
    }

    [Fact]
    public void QueryResult_Decodes_Rssi_Tenths_To_Dbm()
    {
        CcdiFrame.TryParse("j07064-456C9", out var frame).Should().BeTrue();
        var message = CcdiMessage.Decode(frame).Should().BeOfType<CcdiQueryResultMessage>().Subject;
        message.CctmCommand.Should().Be(64);
        message.AsDecibels().Should().BeApproximately(-45.6f, 0.001f);
    }

    [Fact]
    public void Progress_Decodes_Receiver_Busy_And_Not_Busy()
    {
        CcdiFrame.TryParse("p0205C9", out var busy).Should().BeTrue();
        CcdiMessage.Decode(busy).Should().BeOfType<CcdiProgressMessage>()
            .Which.Type.Should().Be(CcdiProgressType.ReceiverBusy);

        CcdiFrame.TryParse("p0206C8", out var idle).Should().BeTrue();
        CcdiMessage.Decode(idle).Should().BeOfType<CcdiProgressMessage>()
            .Which.Type.Should().Be(CcdiProgressType.ReceiverNotBusy);
    }

    [Fact]
    public void Error_Decodes_Category_And_Number()
    {
        CcdiFrame.TryParse("e03001A7", out var frame).Should().BeTrue();
        var message = CcdiMessage.Decode(frame).Should().BeOfType<CcdiErrorMessage>().Subject;
        message.Category.Should().Be('0');
        message.ErrorNumber.Should().Be(0x01);
        message.Describe().Should().Be("unsupported command");
    }

    [Fact]
    public void Unknown_Ident_Surfaces_As_Unknown_Message()
    {
        CcdiFrame.TryParse(new CcdiFrame('y', "Hi").Encode(), out var frame).Should().BeTrue();
        CcdiMessage.Decode(frame).Should().BeOfType<CcdiUnknownMessage>()
            .Which.UnknownIdent.Should().Be('y');
    }

    [Fact]
    public void Ccr_Ack_Nak_And_Pulse_Messages_Decode()
    {
        CcdiFrame.TryParse("+01R22", out var ack).Should().BeTrue();
        CcdiMessage.Decode(ack).Should().BeOfType<CcrAckMessage>()
            .Which.EchoedCommand.Should().Be('R');

        // NAK reason 05 (busy) echoing command T: -0305T + checksum
        var nakWire = new CcdiFrame('-', "05T").Encode();
        CcdiFrame.TryParse(nakWire, out var nak).Should().BeTrue();
        var nakMsg = CcdiMessage.Decode(nak).Should().BeOfType<CcrNakMessage>().Subject;
        nakMsg.Reason.Should().Be(0x05);
        nakMsg.EchoedCommand.Should().Be('T');
        nakMsg.Describe().Should().Be("radio busy");

        CcdiFrame.TryParse("Q01PFE", out var pulse).Should().BeTrue();
        CcdiMessage.Decode(pulse).Should().BeOfType<CcrPulseResultMessage>()
            .Which.HasMinimumConfiguration.Should().BeTrue();
    }

    [Fact]
    public void Ccr_Unsolicited_Messages_Decode()
    {
        // Manual §2.9 examples: M01R00 (CCR initialised), V0612345-18 (Selcall decode).
        CcdiFrame.TryParse("M01R00", out var init).Should().BeTrue();
        CcdiMessage.Decode(init).Should().BeOfType<CcrNotificationMessage>()
            .Which.Kind.Should().Be('R');

        CcdiFrame.TryParse("V0612345-18", out var selcall).Should().BeTrue();
        CcdiMessage.Decode(selcall).Should().BeOfType<CcrSelcallDecodeMessage>()
            .Which.Tones.Should().Be("12345-");
    }

    [Fact]
    public void Ring_Message_Decodes_Manual_Example()
    {
        // §1.10.9 example: r0714000FFA6 — an SDM call.
        CcdiFrame.TryParse("r0714000FFA6", out var frame).Should().BeTrue();
        var ring = CcdiMessage.Decode(frame).Should().BeOfType<CcdiRingMessage>().Subject;
        ring.Category.Should().Be('1');
        ring.RingType.Should().Be("4000");
        ring.Status.Should().Be("FF");
        ring.CallerId.Should().BeEmpty();
    }

    [Fact]
    public void Sdm_Message_Decodes_Manual_Examples()
    {
        // §1.10.3 examples: s002D (no data), s02Hi7A (data "Hi").
        CcdiFrame.TryParse("s002D", out var empty).Should().BeTrue();
        CcdiMessage.Decode(empty).Should().BeOfType<CcdiSdmMessage>()
            .Which.Data.Should().BeEmpty();

        CcdiFrame.TryParse("s02Hi7A", out var hi).Should().BeTrue();
        CcdiMessage.Decode(hi).Should().BeOfType<CcdiSdmMessage>()
            .Which.Data.Should().Be("Hi");
    }
}
