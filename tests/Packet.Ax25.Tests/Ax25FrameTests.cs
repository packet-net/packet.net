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

        frame.ToBytes().ShouldBe(GoldenUi_AprsCommand_NoDigi);
    }

    [Fact]
    public void TryParse_Decodes_Golden_Vector()
    {
        Ax25Frame.TryParse(GoldenUi_AprsCommand_NoDigi, out var frame).ShouldBeTrue();
        frame!.Destination.Callsign.ShouldBe(new Callsign("APRS", 0));
        frame.Source.Callsign.ShouldBe(new Callsign("G7XYZ", 7));
        frame.Digipeaters.ShouldBeEmpty();
        frame.Control.ShouldBe((byte)0x03);
        frame.Pid.ShouldBe((byte?)0xF0);
        frame.Info.ToArray().ShouldBe("hello"u8.ToArray());
        frame.IsUi.ShouldBeTrue();
        frame.PollFinal.ShouldBeFalse();
        frame.IsCommand.ShouldBeTrue();
        frame.IsResponse.ShouldBeFalse();
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
        Ax25Frame.TryParse(bytes, out var decoded).ShouldBeTrue();

        decoded!.Destination.Callsign.ShouldBe(new Callsign("APRS", 0));
        decoded.Source.Callsign.ShouldBe(new Callsign("M0LTE", 9));
        decoded.Digipeaters.Count.ShouldBe(2);
        decoded.Digipeaters[0].Callsign.ShouldBe(new Callsign("WIDE1", 1));
        decoded.Digipeaters[1].Callsign.ShouldBe(new Callsign("WIDE2", 2));
        decoded.Digipeaters[0].ExtensionBit.ShouldBeFalse();
        decoded.Digipeaters[1].ExtensionBit.ShouldBeTrue("E bit migrates to last digipeater");
        decoded.Source.ExtensionBit.ShouldBeFalse("source E bit is clear when digipeaters follow");
        decoded.Info.ToArray().ShouldBe(new byte[] { 0x21, 0x22, 0x23 });
    }

    [Fact]
    public void Response_Frame_Sets_C_Bits_Per_Spec()
    {
        var frame = Ax25Frame.Ui(
            destination: new Callsign("APRS", 0),
            source: new Callsign("G7XYZ", 7),
            info: ReadOnlySpan<byte>.Empty,
            isCommand: false);

        frame.Destination.CrhBit.ShouldBeFalse("dest C=0 for response");
        frame.Source.CrhBit.ShouldBeTrue("source C=1 for response");
        frame.IsResponse.ShouldBeTrue();
        frame.IsCommand.ShouldBeFalse();
    }

    [Fact]
    public void Poll_Final_Bit_Is_Reflected_In_Control_Byte()
    {
        var frame = Ax25Frame.Ui(
            destination: new Callsign("APRS", 0),
            source: new Callsign("G7XYZ", 7),
            info: ReadOnlySpan<byte>.Empty,
            pollFinal: true);

        frame.Control.ShouldBe(Ax25Frame.ControlUiPollFinal);
        frame.PollFinal.ShouldBeTrue();
    }

    [Fact]
    public void TryParse_Rejects_Truncated_Input()
    {
        Ax25Frame.TryParse(GoldenUi_AprsCommand_NoDigi.AsSpan(0, 10), out _).ShouldBeFalse();
    }

    [Fact]
    public void TryParse_Rejects_Empty()
    {
        Ax25Frame.TryParse(ReadOnlySpan<byte>.Empty, out _).ShouldBeFalse();
    }

    [Fact]
    public void Ui_Rejects_More_Than_Eight_Digipeaters()
    {
        var nine = Enumerable.Range(0, 9).Select(i => new Callsign($"D{i}", 0));
        Should.Throw<ArgumentException>(() =>
            Ax25Frame.Ui(
                destination: new Callsign("APRS", 0),
                source: new Callsign("G7XYZ", 0),
                info: ReadOnlySpan<byte>.Empty,
                digipeaters: nine));
    }
}
