using Packet.Ax25;
using Packet.Core;
using Packet.Kiss;

namespace Packet.Kiss.Tests;

public class KissFrameClassifierTests
{
    [Fact]
    public void KISS_Data_With_Valid_AX25_Body_Classifies_As_Ax25FrameReceivedEvent()
    {
        var ax25 = Ax25Frame.Ui(
            destination: new Callsign("CQ"),
            source: new Callsign("M0LTE", 1),
            info: "hello"u8);
        var raw = new KissFrame(0, KissCommand.Data, ax25.ToBytes());

        var evt = KissFrameClassifier.Classify(raw);

        evt.Should().BeOfType<Ax25FrameReceivedEvent>();
        var typed = (Ax25FrameReceivedEvent)evt;
        typed.Ax25.Source.Callsign.Should().Be(new Callsign("M0LTE", 1));
        typed.Raw.Should().Be(raw);
    }

    [Fact]
    public void AckMode_Data_Classifies_As_AckModeDataReceivedEvent()
    {
        var payload = new byte[] { 0x12, 0x34, 0x41, 0x42, 0x43 };
        var raw = new KissFrame(0, KissCommand.AckMode, payload);

        var evt = KissFrameClassifier.Classify(raw);

        evt.Should().BeOfType<AckModeDataReceivedEvent>();
        var typed = (AckModeDataReceivedEvent)evt;
        typed.SequenceTag.Should().Be((ushort)0x1234);
        typed.Ax25Payload.ToArray().Should().Equal(new byte[] { 0x41, 0x42, 0x43 });
    }

    [Fact]
    public void AckMode_TX_Completion_Echo_Classifies_As_Unknown_By_Design()
    {
        // The 2-byte-only ACKMODE echo is correlated inside the driver via
        // the sequence-tag dictionary; the classifier deliberately does not
        // expose it as a typed event because the caller already has it via
        // SendFrameWithAckAsync's receipt.
        var raw = new KissFrame(0, KissCommand.AckMode, new byte[] { 0xA5, 0xB6 });

        var evt = KissFrameClassifier.Classify(raw);

        evt.Should().BeOfType<UnknownInboundEvent>();
    }

    [Fact]
    public void KISS_Data_With_Garbage_Payload_Classifies_As_Unknown_Without_Throwing()
    {
        // Random bytes - the AX.25 parse must fail gracefully, not bubble
        // an exception out of the classifier. This is the contract that
        // makes the classifier safe for arbitrary on-wire input.
        var raw = new KissFrame(0, KissCommand.Data, new byte[] { 0x78, 0x01, 0x02, 0x70, 0x72, 0x65, 0x66, 0x69, 0x78, 0x03, 0xF0 });

        var evt = KissFrameClassifier.Classify(raw);

        evt.Should().BeOfType<UnknownInboundEvent>();
    }

    [Fact]
    public void Generic_Classifier_Does_Not_Know_About_NinoTnc_TxTest()
    {
        // Same payload that NinoTncFrameClassifier would upgrade to a TX-Test
        // event must still come back as Ax25-or-Unknown from the generic
        // classifier - modem-specific upgrades happen in modem-specific
        // overlays.
        const string body = "x\x01\x02prefix-garbage=FirmwareVr:3.44=BrdSwchMod:040F0002";
        var raw = new KissFrame(0, KissCommand.Data, System.Text.Encoding.ASCII.GetBytes(body));

        var evt = KissFrameClassifier.Classify(raw);

        // Either Ax25 (if the garbage happened to decode) or Unknown (if it didn't) -
        // but never a NinoTNC-specific subclass, because the generic classifier
        // doesn't know about TX-Test.
        (evt is Ax25FrameReceivedEvent || evt is UnknownInboundEvent).Should().BeTrue();
    }

    [Fact]
    public void Unknown_Command_Classifies_As_Unknown()
    {
        var raw = new KissFrame(0, KissCommand.Poll, Array.Empty<byte>());

        var evt = KissFrameClassifier.Classify(raw);

        evt.Should().BeOfType<UnknownInboundEvent>();
        evt.Raw.Should().Be(raw);
    }
}
