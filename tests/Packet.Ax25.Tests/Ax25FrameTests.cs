using Packet.Ax25;
using Packet.Core;

namespace Packet.Ax25.Tests;

public class Ax25FrameTests
{
    /// <summary>
    /// Manually constructed UI frame, hand-encoded per §3.12 + §4.3.3.6:
    /// <code>
    ///   Destination: APRS-0   (C=1, command)  → 82 A0 A4 A6 40 40 E0
    ///   Source:      G7XYZ-7  (C=0, command;
    ///                          E=1, no digipeaters)
    ///                                          → 8E 6E B0 B2 B4 40 6F
    ///   Control:     0x03   (UI, P=0)
    ///   PID:         0xF0   (no Layer 3)
    ///   Info:        "hello"
    /// </code>
    /// </summary>
    private static readonly byte[] GoldenUi_AprsCommand_NoDigi =
    {
        0x82, 0xA0, 0xA4, 0xA6, 0x40, 0x40, 0xE0,   // APRS-0 dst, C=1, E=0
        0x8E, 0x6E, 0xB0, 0xB2, 0xB4, 0x40, 0x6F,   // G7XYZ-7 src, C=0, E=1
        0x03,                                       // UI control
        0xF0,                                       // PID no L3
        0x68, 0x65, 0x6C, 0x6C, 0x6F,               // "hello"
    };

    [Fact]
    public void Ui_Builds_Frame_That_Matches_Golden_Vector()
    {
        var frame = Ax25Frame.Ui(
            destination: new Callsign("APRS", 0),
            source: new Callsign("G7XYZ", 7),
            info: "hello"u8,
            pid: Ax25Frame.PidNoLayer3,
            isCommand: true,
            pollFinal: false);

        frame.ToBytes().Should().Equal(GoldenUi_AprsCommand_NoDigi);
    }

    [Fact]
    public void TryParse_Decodes_Golden_Vector()
    {
        Ax25Frame.TryParse(GoldenUi_AprsCommand_NoDigi, out var frame).Should().BeTrue();
        frame!.Destination.Callsign.Should().Be(new Callsign("APRS", 0));
        frame.Source.Callsign.Should().Be(new Callsign("G7XYZ", 7));
        frame.Digipeaters.Should().BeEmpty();
        frame.Control.Should().Be((byte)0x03);
        frame.Pid.Should().Be((byte?)0xF0);
        frame.Info.ToArray().Should().Equal("hello"u8.ToArray());
        frame.IsUi.Should().BeTrue();
        frame.PollFinal.Should().BeFalse();
        frame.IsCommand.Should().BeTrue();
        frame.IsResponse.Should().BeFalse();
    }

    [Fact]
    public void RoundTrip_With_Digipeaters()
    {
        var frame = Ax25Frame.Ui(
            destination: new Callsign("APRS", 0),
            source: new Callsign("M0LTE", 9),
            info: new byte[] { 0x21, 0x22, 0x23 },
            digipeaters: new[] { new Callsign("WIDE1", 1), new Callsign("WIDE2", 2) });

        var bytes = frame.ToBytes();
        Ax25Frame.TryParse(bytes, out var decoded).Should().BeTrue();

        decoded!.Destination.Callsign.Should().Be(new Callsign("APRS", 0));
        decoded.Source.Callsign.Should().Be(new Callsign("M0LTE", 9));
        decoded.Digipeaters.Count.Should().Be(2);
        decoded.Digipeaters[0].Callsign.Should().Be(new Callsign("WIDE1", 1));
        decoded.Digipeaters[1].Callsign.Should().Be(new Callsign("WIDE2", 2));
        decoded.Digipeaters[0].ExtensionBit.Should().BeFalse();
        decoded.Digipeaters[1].ExtensionBit.Should().BeTrue("E bit migrates to last digipeater");
        decoded.Source.ExtensionBit.Should().BeFalse("source E bit is clear when digipeaters follow");
        decoded.Info.ToArray().Should().Equal(new byte[] { 0x21, 0x22, 0x23 });
    }

    [Fact]
    public void Response_Frame_Sets_C_Bits_Per_Spec()
    {
        var frame = Ax25Frame.Ui(
            destination: new Callsign("APRS", 0),
            source: new Callsign("G7XYZ", 7),
            info: ReadOnlySpan<byte>.Empty,
            isCommand: false);

        frame.Destination.CrhBit.Should().BeFalse("dest C=0 for response");
        frame.Source.CrhBit.Should().BeTrue("source C=1 for response");
        frame.IsResponse.Should().BeTrue();
        frame.IsCommand.Should().BeFalse();
    }

    [Fact]
    public void Poll_Final_Bit_Is_Reflected_In_Control_Byte()
    {
        var frame = Ax25Frame.Ui(
            destination: new Callsign("APRS", 0),
            source: new Callsign("G7XYZ", 7),
            info: ReadOnlySpan<byte>.Empty,
            pollFinal: true);

        frame.Control.Should().Be(Ax25Frame.ControlUiPollFinal);
        frame.PollFinal.Should().BeTrue();
    }

    [Fact]
    public void TryParse_Rejects_Truncated_Input()
    {
        Ax25Frame.TryParse(GoldenUi_AprsCommand_NoDigi.AsSpan(0, 10), out _).Should().BeFalse();
    }

    [Fact]
    public void TryParse_Rejects_Empty()
    {
        Ax25Frame.TryParse(ReadOnlySpan<byte>.Empty, out _).Should().BeFalse();
    }

    [Fact]
    public void ToBytesWithFcs_Appends_LowByte_Then_HighByte()
    {
        // Empirically verified against XRouter's AXUDP listener: it accepts
        // FCS as { low_byte, high_byte } and rejects { high_byte, low_byte }
        // as "not AXUDP". This matches the convention on real HDLC wire too
        // (the §3.8 "MSB-first" wording refers to bit-stream order, not byte
        // order of the serialised octets).
        var frame = Ax25Frame.Ui(
            destination: new Callsign("APRS", 0),
            source: new Callsign("G7XYZ", 7),
            info: "hello"u8);

        var withFcs = frame.ToBytesWithFcs();
        var body = frame.ToBytes();

        withFcs.Length.Should().Be(body.Length + 2, "FCS adds exactly 2 bytes");
        withFcs.AsSpan(0, body.Length).SequenceEqual(body).Should().BeTrue("body must be unchanged");

        var expectedCrc = Crc16Ccitt.Compute(body);
        withFcs[^2].Should().Be((byte)(expectedCrc & 0xFF), "low byte first");
        withFcs[^1].Should().Be((byte)((expectedCrc >> 8) & 0xFF), "high byte second");
    }

    [Fact]
    public void WriteToWithFcs_Returns_Total_Bytes_Written()
    {
        var frame = Ax25Frame.Ui(
            destination: new Callsign("APRS", 0),
            source: new Callsign("G7XYZ", 0),
            info: new byte[] { 0xAA });

        var buf = new byte[frame.RequiredBytesWithFcs];
        var written = frame.WriteToWithFcs(buf);

        written.Should().Be(frame.RequiredBytesWithFcs);
        written.Should().Be(frame.RequiredBytes + 2);
    }

    [Fact]
    public void Ui_Rejects_More_Than_Eight_Digipeaters()
    {
        var nine = Enumerable.Range(0, 9).Select(i => new Callsign($"D{i}", 0));
        var act = () => Ax25Frame.Ui(
            destination: new Callsign("APRS", 0),
            source: new Callsign("G7XYZ", 0),
            info: ReadOnlySpan<byte>.Empty,
            digipeaters: nine);
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Ui_Accepts_Exactly_Eight_Digipeaters()
    {
        // §3.12.5 allows up to 8 repeaters, so the boundary value itself must
        // be accepted (only the 9th is rejected).
        var eight = Enumerable.Range(0, 8).Select(i => new Callsign($"D{i}", 0));
        var frame = Ax25Frame.Ui(
            destination: new Callsign("APRS", 0),
            source: new Callsign("G7XYZ", 0),
            info: ReadOnlySpan<byte>.Empty,
            digipeaters: eight);

        frame.Digipeaters.Count.Should().Be(8);
        frame.Digipeaters[7].ExtensionBit.Should().BeTrue("E-bit lands on the last digipeater");
    }

    [Fact]
    public void Ui_Constructs_Digipeaters_With_Has_Been_Repeated_Bit_Clear()
    {
        // §3.12.4: the repeater slot C/H bit is the has-been-repeated flag. A
        // freshly built frame has not been repeated anywhere, so every
        // digipeater slot must start with it clear.
        var frame = Ax25Frame.Ui(
            destination: new Callsign("APRS", 0),
            source: new Callsign("G7XYZ", 0),
            info: ReadOnlySpan<byte>.Empty,
            digipeaters: new[] { new Callsign("WIDE1", 1), new Callsign("WIDE2", 2) });

        frame.Digipeaters.Should().OnlyContain(d => d.CrhBit == false,
            "no digipeater has repeated the frame yet");
    }
}
