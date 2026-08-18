using AwesomeAssertions;
using Packet.Ax25;
using Packet.Ax25.Session;
using Packet.Core;

namespace Packet.Ax25.Tests;

/// <summary>
/// Codec tests for the extended (modulo-128) 2-octet control field on I and S
/// frames (AX.25 v2.2 §4.2.1 Fig 4.1b, §4.3.1 Fig 4.2b, §4.3.2 Fig 4.3b).
/// Each octet value is pinned against the spec bit layout:
/// <list type="bullet">
/// <item>I frame: octet0 = (N(S) &lt;&lt; 1) | 0; octet1 = (N(R) &lt;&lt; 1) | P.</item>
/// <item>S frame: octet0 = base (SS bits + "01", high nibble 0); octet1 = (N(R) &lt;&lt; 1) | P/F.</item>
/// <item>U frames stay 1 octet in both modulos.</item>
/// </list>
/// The control-field width is not derivable from the octets alone, so parsing
/// requires the link's negotiated modulo - these tests exercise both the
/// correct (extended) parse and the deliberately-wrong modulo-8 parse to show
/// why the receive path must be mode-aware.
/// </summary>
public class Ax25FrameExtendedControlTests
{
    private static readonly Callsign Dest = new("M0LTE", 0);
    private static readonly Callsign Src = new("G7XYZ", 7);

    // ─── Encode: spec-pinned octets ─────────────────────────────────────

    [Theory]
    // ns, nr, p,    expected octet0,            expected octet1
    [InlineData(0, 0, false, 0x00, 0x00)]
    [InlineData(5, 3, true, 0x0A, 0x07)]   // 5<<1=0x0A ; (3<<1)|1=0x07
    [InlineData(1, 0, false, 0x02, 0x00)]
    [InlineData(100, 70, false, 0xC8, 0x8C)]   // 100<<1=200=0xC8 ; 70<<1=140=0x8C
    [InlineData(127, 127, true, 0xFE, 0xFF)]   // 127<<1=0xFE ; (127<<1)|1=0xFF
    public void IFrame_Extended_Encodes_Spec_Octets(int ns, int nr, bool poll, byte octet0, byte octet1)
    {
        var frame = Ax25Frame.I(Dest, Src, nr: (byte)nr, ns: (byte)ns,
            info: new byte[] { 0xAA }, pollBit: poll, extended: true);

        frame.IsExtendedControl.Should().BeTrue();
        frame.Control.Should().Be(octet0, "octet0 carries 7-bit N(S) with bit 0 = 0");
        frame.ControlExtension.Should().Be(octet1, "octet1 carries 7-bit N(R) with bit 0 = P");
    }

    [Theory]
    [InlineData(SupervisoryFrameType.Rr, 0x01)]
    [InlineData(SupervisoryFrameType.Rnr, 0x05)]
    [InlineData(SupervisoryFrameType.Rej, 0x09)]
    [InlineData(SupervisoryFrameType.Srej, 0x0D)]
    public void SFrame_Extended_Encodes_Spec_Octets(SupervisoryFrameType type, byte expectedBase)
    {
        // N(R) = 100, F = 1 → octet1 = (100<<1)|1 = 201 = 0xC9.
        var frame = Build(type, nr: 100, isCommand: false, pollFinal: true, extended: true);

        frame.IsExtendedControl.Should().BeTrue();
        frame.Control.Should().Be(expectedBase, "octet0 is the supervisory base (SS bits + 01, high nibble 0)");
        frame.ControlExtension.Should().Be(0xC9, "octet1 carries 7-bit N(R) with bit 0 = P/F");
    }

    // ─── Round-trip: encode → bytes → mode-aware parse → fields/classify ─

    [Theory]
    [InlineData(0, 0)]
    [InlineData(1, 2)]
    [InlineData(7, 8)]    // straddles the mod-8 3-bit boundary
    [InlineData(63, 64)]
    [InlineData(100, 27)]
    [InlineData(126, 125)]
    [InlineData(127, 127)]
    public void IFrame_Extended_RoundTrips_7bit_Sequence_Numbers(int ns, int nr)
    {
        var payload = new byte[] { 0xDE, 0xAD, 0xBE, 0xEF };
        var sent = Ax25Frame.I(Dest, Src, nr: (byte)nr, ns: (byte)ns,
            info: payload, pollBit: true, extended: true);

        Ax25Frame.TryParse(sent.ToBytes(), Ax25ParseOptions.Lenient, extended: true, out var got)
            .Should().BeTrue();

        got!.IsExtendedControl.Should().BeTrue();
        got.Ns.Should().Be((byte)ns, "7-bit N(S) survives the round-trip");
        got.Nr.Should().Be((byte)nr, "7-bit N(R) survives the round-trip");
        got.PollFinal.Should().BeTrue("P lives in octet1 bit 0 under mod-128");
        got.Pid.Should().Be(Ax25Frame.PidNoLayer3);
        got.Info.ToArray().Should().Equal(payload);
        Ax25FrameClassifier.Classify(got).Should().BeOfType<IFrameReceived>();
    }

    [Theory]
    [InlineData(SupervisoryFrameType.Rr, typeof(RrReceived))]
    [InlineData(SupervisoryFrameType.Rnr, typeof(RnrReceived))]
    [InlineData(SupervisoryFrameType.Rej, typeof(RejReceived))]
    [InlineData(SupervisoryFrameType.Srej, typeof(SrejReceived))]
    public void SFrame_Extended_RoundTrips_Nr_And_Classifies(SupervisoryFrameType type, Type expectedEvent)
    {
        var sent = Build(type, nr: 99, isCommand: true, pollFinal: false, extended: true);

        Ax25Frame.TryParse(sent.ToBytes(), Ax25ParseOptions.Lenient, extended: true, out var got)
            .Should().BeTrue();

        got!.IsExtendedControl.Should().BeTrue();
        got.Nr.Should().Be(99, "7-bit N(R) survives the round-trip");
        got.PollFinal.Should().BeFalse();
        got.Info.IsEmpty.Should().BeTrue("S frames carry no information field");
        Ax25FrameClassifier.Classify(got).Should().BeOfType(expectedEvent);
    }

    // ─── Sizing ─────────────────────────────────────────────────────────

    [Fact]
    public void Extended_IFrame_Is_Exactly_One_Octet_Longer_Than_Mod8()
    {
        var payload = new byte[] { 1, 2, 3 };
        var mod8 = Ax25Frame.I(Dest, Src, nr: 3, ns: 5, info: payload, extended: false);
        var ext = Ax25Frame.I(Dest, Src, nr: 3, ns: 5, info: payload, extended: true);

        ext.RequiredBytes.Should().Be(mod8.RequiredBytes + 1, "the second control octet adds one byte");
        ext.ToBytes().Length.Should().Be(mod8.ToBytes().Length + 1);
    }

    // ─── U frames are modulo-independent ────────────────────────────────

    [Fact]
    public void UFrame_Stays_One_Octet_Even_When_Parsed_Extended()
    {
        // SABME is a U frame: 1-octet control in both modulos. Parsing it on an
        // extended link must NOT consume a second control octet.
        var sabme = Ax25Frame.Sabme(Dest, Src, pollBit: true);

        Ax25Frame.TryParse(sabme.ToBytes(), Ax25ParseOptions.Lenient, extended: true, out var got)
            .Should().BeTrue();

        got!.IsExtendedControl.Should().BeFalse("U frames have no extended control octet");
        got.Control.Should().Be(sabme.Control);
        Ax25FrameClassifier.Classify(got).Should().BeOfType<SabmeReceived>();
    }

    // ─── Why mode-awareness is required ─────────────────────────────────

    [Fact]
    public void Extended_IFrame_Parsed_As_Mod8_Misframes()
    {
        // An extended I-frame decoded at the wrong modulo (mod-8) swallows the
        // second control octet as the PID and mis-reads N(S) - demonstrating the
        // width genuinely can't be inferred from the bytes.
        var sent = Ax25Frame.I(Dest, Src, nr: 9, ns: 70, info: new byte[] { 0xAA }, pollBit: true, extended: true);
        var bytes = sent.ToBytes();

        Ax25Frame.TryParse(bytes, Ax25ParseOptions.Lenient, extended: false, out var wrong).Should().BeTrue();
        wrong!.IsExtendedControl.Should().BeFalse();
        // octet1 = (9<<1)|1 = 0x13 gets read as the PID rather than N(R)/P.
        wrong.Pid.Should().Be(0x13, "the second control octet is mistaken for the PID under mod-8");
        wrong.Ns.Should().NotBe(70, "N(S) is mis-read at 3-bit width");

        // The same bytes, parsed at the correct modulo, decode cleanly.
        Ax25Frame.TryParse(bytes, Ax25ParseOptions.Lenient, extended: true, out var right).Should().BeTrue();
        right!.Ns.Should().Be(70);
        right.Nr.Should().Be(9);
    }

    // ─── mod-8 regression: extended flag off is unchanged ───────────────

    [Fact]
    public void Mod8_IFrame_Unaffected_By_Extended_Support()
    {
        var frame = Ax25Frame.I(Dest, Src, nr: 3, ns: 5, info: new byte[] { 0x42 }, pollBit: true, extended: false);

        frame.IsExtendedControl.Should().BeFalse();
        frame.ControlExtension.Should().BeNull();
        // mod-8 I control = (N(R)<<5) | (P<<4) | (N(S)<<1) = 0x60|0x10|0x0A = 0x7A.
        frame.Control.Should().Be(0x7A);
        frame.Ns.Should().Be(5);
        frame.Nr.Should().Be(3);
        frame.PollFinal.Should().BeTrue();
    }

    private static Ax25Frame Build(SupervisoryFrameType type, byte nr, bool isCommand, bool pollFinal, bool extended)
        => type switch
        {
            SupervisoryFrameType.Rr => Ax25Frame.Rr(Dest, Src, nr, isCommand, pollFinal, extended: extended),
            SupervisoryFrameType.Rnr => Ax25Frame.Rnr(Dest, Src, nr, isCommand, pollFinal, extended: extended),
            SupervisoryFrameType.Rej => Ax25Frame.Rej(Dest, Src, nr, isCommand, pollFinal, extended: extended),
            SupervisoryFrameType.Srej => Ax25Frame.Srej(Dest, Src, nr, isCommand, pollFinal, extended: extended),
            _ => throw new ArgumentOutOfRangeException(nameof(type)),
        };
}
