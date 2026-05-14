using System.Text;
using Packet.Ax25;
using Packet.Core;
using Packet.Kiss;
using Packet.Kiss.NinoTnc;
using Packet.Kiss.NinoTnc.Firmware;

namespace Packet.Kiss.NinoTnc.Tests;

public class NinoTncFrameClassifierTests
{
    [Fact]
    public void KISS_Data_Frame_With_Valid_AX25_Body_Classifies_As_Ax25FrameReceivedEvent()
    {
        var ax25 = Ax25Frame.Ui(
            destination: new Callsign("CQ"),
            source: new Callsign("M0LTE", 1),
            info: "hello"u8);
        var raw = new KissFrame(0, KissCommand.Data, ax25.ToBytes());

        var evt = NinoTncFrameClassifier.Classify(raw);

        evt.Should().BeOfType<Ax25FrameReceivedEvent>();
        var typed = (Ax25FrameReceivedEvent)evt;
        typed.Ax25.Source.Callsign.Should().Be(new Callsign("M0LTE", 1));
        typed.Ax25.Destination.Callsign.Should().Be(new Callsign("CQ"));
        typed.Ax25.Info.Span.SequenceEqual("hello"u8).Should().BeTrue();
        typed.Raw.Should().Be(raw);
    }

    [Fact]
    public void KISS_Data_Frame_With_FirmwareVr_Marker_Classifies_As_NinoTncTxTestFrameReceivedEvent()
    {
        // The TX-Test parser scans for the "=FirmwareVr:" marker anywhere in
        // the KISS Data payload; the prefix bytes are firmware-generated and
        // not a valid AX.25 header. The classifier should prefer this shape.
        const string body = "x\x01\x02prefix-garbage=FirmwareVr:3.44=BrdSwchMod:040F0002";
        var payload = Encoding.ASCII.GetBytes(body);
        var raw = new KissFrame(0, KissCommand.Data, payload);

        var evt = NinoTncFrameClassifier.Classify(raw);

        evt.Should().BeOfType<NinoTncTxTestFrameReceivedEvent>();
        var typed = (NinoTncTxTestFrameReceivedEvent)evt;
        typed.Diagnostic.FirmwareVersionRaw.Should().Be("3.44");
        typed.Diagnostic.FirmwareVersion.Should().Be(new NinoTncFirmwareVersion(3, 44));
        typed.Diagnostic.RunningMode!.Value.Mode.Should().Be((byte)6); // 0x02 → mode 6
    }

    [Fact]
    public void TX_Test_Shape_Wins_Over_AX25_Parse_When_Both_Match()
    {
        // Craft a payload that decodes as a sketchy AX.25 frame AND contains
        // the =FirmwareVr: marker. The classifier must prefer the TX-Test
        // shape because the AX.25 parse here is incidental noise.
        var ax25 = Ax25Frame.Ui(
            destination: new Callsign("CQ"),
            source: new Callsign("M0LTE", 1),
            info: Encoding.ASCII.GetBytes("=FirmwareVr:3.44=BrdSwchMod:040F0002"));
        var raw = new KissFrame(0, KissCommand.Data, ax25.ToBytes());

        var evt = NinoTncFrameClassifier.Classify(raw);

        evt.Should().BeOfType<NinoTncTxTestFrameReceivedEvent>();
    }

    [Fact]
    public void AckMode_Data_Frame_Classifies_As_AckModeDataReceivedEvent()
    {
        // ACKMODE Data: command 0x0C with 2-byte seq tag + AX.25-shaped payload.
        var payload = new byte[] { 0xA5, 0xB6, 0x41, 0x42, 0x43 };
        var raw = new KissFrame(0, KissCommand.AckMode, payload);

        var evt = NinoTncFrameClassifier.Classify(raw);

        evt.Should().BeOfType<AckModeDataReceivedEvent>();
        var typed = (AckModeDataReceivedEvent)evt;
        typed.SequenceTag.Should().Be((ushort)0xA5B6);
        typed.Ax25Payload.ToArray().Should().Equal(new byte[] { 0x41, 0x42, 0x43 });
    }

    [Fact]
    public void AckMode_TX_Completion_Echo_Classifies_As_Unknown()
    {
        // The 2-byte-payload echo is correlated inside NinoTncSerialPort by
        // sequence tag and surfaces via SendFrameWithAckAsync's return value.
        // The classifier does not generate a typed event for it — by design.
        var raw = new KissFrame(0, KissCommand.AckMode, new byte[] { 0x12, 0x34 });

        var evt = NinoTncFrameClassifier.Classify(raw);

        evt.Should().BeOfType<UnknownInboundEvent>();
    }

    [Fact]
    public void Unrecognised_Command_Classifies_As_Unknown()
    {
        // KISS Poll (0x0E) — not part of our supported subset; classifier
        // gives the caller raw access via Unknown so they can decide what
        // to do.
        var raw = new KissFrame(0, KissCommand.Poll, Array.Empty<byte>());

        var evt = NinoTncFrameClassifier.Classify(raw);

        evt.Should().BeOfType<UnknownInboundEvent>();
        evt.Raw.Should().Be(raw);
    }

    [Fact]
    public void KISS_Data_With_Garbage_Body_Classifies_As_Unknown()
    {
        // 8 bytes — too short for an AX.25 header, no FirmwareVr marker.
        var raw = new KissFrame(0, KissCommand.Data, new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 });

        var evt = NinoTncFrameClassifier.Classify(raw);

        evt.Should().BeOfType<UnknownInboundEvent>();
    }
}
