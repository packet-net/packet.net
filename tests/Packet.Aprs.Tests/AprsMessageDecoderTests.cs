using Packet.Aprs;

namespace Packet.Aprs.Tests;

public class AprsMessageDecoderTests
{
    [Fact]
    public void Decodes_Plain_Message()
    {
        // APRS101 §14 example: hello to KH6XYZ from anyone.
        var info = System.Text.Encoding.ASCII.GetBytes(":KH6XYZ   :hello world");
        AprsMessageDecoder.TryDecode(info, out var m).Should().BeTrue();
        m.Addressee.Should().Be("KH6XYZ");
        m.Text.Should().Be("hello world");
        m.MessageId.Should().BeNull();
    }

    [Fact]
    public void Decodes_Message_With_Sequence_Id()
    {
        var info = System.Text.Encoding.ASCII.GetBytes(":N0CALL   :ping{042");
        AprsMessageDecoder.TryDecode(info, out var m).Should().BeTrue();
        m.Addressee.Should().Be("N0CALL");
        m.Text.Should().Be("ping");
        m.MessageId.Should().Be("042");
    }

    [Theory]
    // Real corpus telemetry-definition messages.
    [InlineData(":ER-DK5OH :UNIT.RX Erlang,TX Erlang,RXcount/10m,TXcount/10m,none1,STxxxxxx,logic",
                "ER-DK5OH", "UNIT.RX Erlang,TX Erlang,RXcount/10m,TXcount/10m,none1,STxxxxxx,logic")]
    [InlineData(":EL-PI3ZUT:PARM.RX Avg 10m,TX Avg 10m,RX Count 10m,TX Count 10m,,RX,TX",
                "EL-PI3ZUT", "PARM.RX Avg 10m,TX Avg 10m,RX Count 10m,TX Count 10m,,RX,TX")]
    [InlineData(":EL-PI3ZUT:BITS.11111111,SvxLink RepeaterLogic",
                "EL-PI3ZUT", "BITS.11111111,SvxLink RepeaterLogic")]
    [InlineData(":BG5VIG-10:PARM.CPUTemp,DiGi,Tx,Rx",
                "BG5VIG-10", "PARM.CPUTemp,DiGi,Tx,Rx")]
    public void Decodes_Real_Corpus_Telemetry_Definitions(string infoText, string expectedAddr, string expectedText)
    {
        var info = System.Text.Encoding.ASCII.GetBytes(infoText);
        AprsMessageDecoder.TryDecode(info, out var m).Should().BeTrue();
        m.Addressee.Should().Be(expectedAddr);
        m.Text.Should().Be(expectedText);
        m.MessageId.Should().BeNull();
    }

    [Fact]
    public void Decodes_Ack_Message()
    {
        // Per spec: an ack is just a message body that starts with "ack"
        // followed by the ID. Decoder surfaces it as text; classification
        // is a caller concern.
        var info = System.Text.Encoding.ASCII.GetBytes(":N0CALL   :ack042");
        AprsMessageDecoder.TryDecode(info, out var m).Should().BeTrue();
        m.Addressee.Should().Be("N0CALL");
        m.Text.Should().Be("ack042");
    }

    [Fact]
    public void Decodes_Bulletin()
    {
        // Bulletins are addressed to BLNn - same envelope.
        var info = System.Text.Encoding.ASCII.GetBytes(":BLN1     :General bulletin text");
        AprsMessageDecoder.TryDecode(info, out var m).Should().BeTrue();
        m.Addressee.Should().Be("BLN1");
        m.Text.Should().Be("General bulletin text");
    }

    [Theory]
    [InlineData(":short    ")]                              // missing body separator
    [InlineData(":TOOLONG12X:body")]                        // addressee separator at wrong offset
    [InlineData(":SHORT")]                                  // truncated
    public void Rejects_Malformed(string infoText)
    {
        var info = System.Text.Encoding.ASCII.GetBytes(infoText);
        AprsMessageDecoder.TryDecode(info, out _).Should().BeFalse();
    }

    [Fact]
    public void Empty_Message_Body_Is_Allowed()
    {
        // An empty body is unusual but not malformed per §14.
        var info = System.Text.Encoding.ASCII.GetBytes(":KH6XYZ   :");
        AprsMessageDecoder.TryDecode(info, out var m).Should().BeTrue();
        m.Addressee.Should().Be("KH6XYZ");
        m.Text.Should().Be("");
    }

    [Fact]
    public void Strips_DTI_If_Present()
    {
        var with = System.Text.Encoding.ASCII.GetBytes(":KH6XYZ   :hi");
        var without = System.Text.Encoding.ASCII.GetBytes("KH6XYZ   :hi");
        AprsMessageDecoder.TryDecode(with, out var a).Should().BeTrue();
        AprsMessageDecoder.TryDecode(without, out var b).Should().BeTrue();
        a.Should().Be(b);
    }

    [Fact]
    public void Brace_Without_Following_Id_Is_Treated_As_Text()
    {
        // A '{' at the very end isn't a valid message-ID prefix.
        var info = System.Text.Encoding.ASCII.GetBytes(":KH6XYZ   :hello{");
        AprsMessageDecoder.TryDecode(info, out var m).Should().BeTrue();
        m.Text.Should().Be("hello{");
        m.MessageId.Should().BeNull();
    }
}
