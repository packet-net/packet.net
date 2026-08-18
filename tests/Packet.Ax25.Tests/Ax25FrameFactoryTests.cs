using AwesomeAssertions;
using Packet.Ax25;
using Packet.Core;

namespace Packet.Ax25.Tests;

/// <summary>
/// Byte-level encoding tests for the non-UI <see cref="Ax25Frame"/> factories.
/// The control-byte values are taken verbatim from AX.25 v2.2 §4.3.2
/// (supervisory) and §4.3.3 (unnumbered) - each test pins the exact octet
/// the factory must produce.
/// </summary>
public class Ax25FrameFactoryTests
{
    private static readonly Callsign Dest = new("M0LTE", 0);
    private static readonly Callsign Src = new("G7XYZ", 7);

    // ─── U-frame control bytes (§4.3.3) ────────────────────────────────

    [Theory]
    [InlineData(false, 0x2F)]
    [InlineData(true, 0x3F)]
    public void Sabm_Encodes_With_P_Bit(bool pollBit, byte expectedControl)
    {
        var frame = Ax25Frame.Sabm(Dest, Src, pollBit);
        frame.Control.Should().Be(expectedControl);
        frame.IsCommand.Should().BeTrue();
        frame.Pid.Should().BeNull();
        frame.Info.Length.Should().Be(0);
    }

    [Theory]
    [InlineData(false, 0x6F)]
    [InlineData(true, 0x7F)]
    public void Sabme_Encodes_With_P_Bit(bool pollBit, byte expectedControl)
    {
        var frame = Ax25Frame.Sabme(Dest, Src, pollBit);
        frame.Control.Should().Be(expectedControl);
        frame.IsCommand.Should().BeTrue();
    }

    [Theory]
    [InlineData(false, 0x43)]
    [InlineData(true, 0x53)]
    public void Disc_Encodes_With_P_Bit(bool pollBit, byte expectedControl)
    {
        var frame = Ax25Frame.Disc(Dest, Src, pollBit);
        frame.Control.Should().Be(expectedControl);
        frame.IsCommand.Should().BeTrue();
    }

    [Theory]
    [InlineData(false, 0x63)]
    [InlineData(true, 0x73)]
    public void Ua_Encodes_With_F_Bit_As_Response(bool finalBit, byte expectedControl)
    {
        var frame = Ax25Frame.Ua(Dest, Src, finalBit);
        frame.Control.Should().Be(expectedControl);
        frame.IsCommand.Should().BeFalse("UA is a response");
    }

    [Theory]
    [InlineData(false, 0x0F)]
    [InlineData(true, 0x1F)]
    public void Dm_Encodes_With_F_Bit_As_Response(bool finalBit, byte expectedControl)
    {
        var frame = Ax25Frame.Dm(Dest, Src, finalBit);
        frame.Control.Should().Be(expectedControl);
        frame.IsCommand.Should().BeFalse();
    }

    [Theory]
    [InlineData(false, 0x87)]
    [InlineData(true, 0x97)]
    public void Frmr_Encodes_With_F_Bit_As_Response(bool finalBit, byte expectedControl)
    {
        var frame = Ax25Frame.Frmr(Dest, Src, info: stackalloc byte[] { 0x00, 0x00, 0x00 }, finalBit);
        frame.Control.Should().Be(expectedControl);
        frame.IsCommand.Should().BeFalse();
        frame.Info.Length.Should().Be(3);
    }

    [Theory]
    [InlineData(false, 0xAF)]
    [InlineData(true, 0xBF)]
    public void Xid_Command_Encodes_With_P_Bit(bool pollFinal, byte expectedControl)
    {
        var frame = Ax25Frame.Xid(Dest, Src, info: ReadOnlySpan<byte>.Empty,
            isCommand: true, pollFinal: pollFinal);
        frame.Control.Should().Be(expectedControl);
        frame.IsCommand.Should().BeTrue();
    }

    [Theory]
    [InlineData(false, 0xE3)]
    [InlineData(true, 0xF3)]
    public void Test_Command_Encodes_With_P_Bit(bool pollFinal, byte expectedControl)
    {
        var frame = Ax25Frame.Test(Dest, Src, info: ReadOnlySpan<byte>.Empty,
            isCommand: true, pollFinal: pollFinal);
        frame.Control.Should().Be(expectedControl);
        frame.IsCommand.Should().BeTrue();
    }

    // ─── S-frame control bytes (§4.3.2) ────────────────────────────────
    //
    // S-frame control = (N(R) << 5) | (P/F << 4) | base
    //   RR   base = 0x01;  RNR base = 0x05
    //   REJ  base = 0x09;  SREJ base = 0x0D

    [Theory]
    [InlineData(0, false, true, 0x01)]  // RR command, N(R)=0, P=0: 0<<5 | 0<<4 | 0x01
    [InlineData(3, false, false, 0x61)]  // RR response, N(R)=3, F=0: 3<<5 | 0<<4 | 0x01
    [InlineData(5, true, false, 0xB1)]  // RR response, N(R)=5, F=1: 5<<5 | 1<<4 | 0x01
    [InlineData(7, true, true, 0xF1)]  // RR command, N(R)=7, P=1: 7<<5 | 1<<4 | 0x01
    public void Rr_Control_Byte_Includes_Nr_And_Pf(byte nr, bool pollFinal, bool isCommand, byte expectedControl)
    {
        var frame = Ax25Frame.Rr(Dest, Src, nr, isCommand, pollFinal);
        frame.Control.Should().Be(expectedControl);
        frame.IsCommand.Should().Be(isCommand);
    }

    [Theory]
    [InlineData(0, false, 0x05)]  // RNR, N(R)=0, F=0
    [InlineData(4, true, 0x95)]  // RNR, N(R)=4, F=1 (4<<5 | 0x10 | 0x05 = 0x95)
    public void Rnr_Control_Byte_Includes_Nr_And_Pf(byte nr, bool pollFinal, byte expectedControl)
    {
        var frame = Ax25Frame.Rnr(Dest, Src, nr, isCommand: false, pollFinal);
        frame.Control.Should().Be(expectedControl);
    }

    [Theory]
    [InlineData(2, true, 0x59)]  // REJ, N(R)=2, F=1 (2<<5 | 0x10 | 0x09 = 0x59)
    [InlineData(6, false, 0xC9)]  // REJ, N(R)=6, F=0 (6<<5 | 0x09 = 0xC9)
    public void Rej_Control_Byte_Includes_Nr_And_Pf(byte nr, bool pollFinal, byte expectedControl)
    {
        var frame = Ax25Frame.Rej(Dest, Src, nr, isCommand: false, pollFinal);
        frame.Control.Should().Be(expectedControl);
    }

    [Theory]
    [InlineData(1, true, 0x3D)]  // SREJ, N(R)=1, F=1 (1<<5 | 0x10 | 0x0D = 0x3D)
    public void Srej_Control_Byte_Includes_Nr_And_Pf(byte nr, bool pollFinal, byte expectedControl)
    {
        var frame = Ax25Frame.Srej(Dest, Src, nr, isCommand: false, pollFinal);
        frame.Control.Should().Be(expectedControl);
    }

    // ─── I-frame control byte (§4.3.1) ─────────────────────────────────
    //
    // I-frame control = (N(R) << 5) | (P << 4) | (N(S) << 1) | 0

    [Theory]
    [InlineData(0, 0, false, 0x00)]  // N(R)=0, N(S)=0, P=0
    [InlineData(3, 2, false, 0x64)]  // N(R)=3, N(S)=2, P=0 → 0x60 | 0x04
    [InlineData(5, 4, true, 0xB8)]  // N(R)=5, N(S)=4, P=1 → 0xA0 | 0x10 | 0x08
    [InlineData(7, 7, true, 0xFE)]  // N(R)=7, N(S)=7, P=1 → 0xE0 | 0x10 | 0x0E
    public void I_Frame_Control_Byte_Composes_Nr_Ns_P(byte nr, byte ns, bool pollBit, byte expectedControl)
    {
        var frame = Ax25Frame.I(Dest, Src, nr, ns, info: "hi"u8, pollBit: pollBit);
        frame.Control.Should().Be(expectedControl);
        frame.IsCommand.Should().BeTrue("I-frames are always commands");
        frame.Pid.Should().Be(Ax25Frame.PidNoLayer3);
        frame.Info.ToArray().Should().Equal("hi"u8.ToArray());
    }

    // ─── Full wire-byte assertions ─────────────────────────────────────
    //
    // Make sure address C-bits, E-bits and the control byte all land in
    // the right slots once serialised.

    [Fact]
    public void Sabm_Command_Without_Digipeaters_Encodes_To_The_Expected_15_Bytes()
    {
        var frame = Ax25Frame.Sabm(Dest, Src, pollBit: true);
        var bytes = frame.ToBytes();

        bytes.Length.Should().Be(15);
        bytes[14].Should().Be(0x3F, "SABM with P=1");

        // Dest "M0LTE" shifted left 1, plus C/H=1, RR=11, SSID=0, E=0 → 0x60 in SSID byte
        // (binary 01100000): top bit = C/H = 1? No - bit pattern is C R R SSID(4) E.
        // For dest command frame: C=1, R bits = "11" (v2.2 default), SSID=0, E=0
        //   → 1_11_0000_0 = 0xE0
        bytes[6].Should().Be(0xE0, "destination SSID byte: C=1 (cmd), R=11, SSID=0, E=0");
        // Source SSID for command frame: C=0, R=11, SSID=7, E=1 (no digis)
        //   → 0_11_0111_1 = 0x6F
        bytes[13].Should().Be(0x6F, "source SSID byte: C=0 (cmd), R=11, SSID=7, E=1 (end of addr list)");
    }

    [Fact]
    public void Ua_Response_Source_C_Bit_Is_1_Destination_C_Bit_Is_0()
    {
        var frame = Ax25Frame.Ua(Dest, Src, finalBit: true);
        var bytes = frame.ToBytes();

        // Response: dest C=0, source C=1.
        // Dest SSID byte: C=0, R=11, SSID=0, E=0 → 0_11_0000_0 = 0x60
        bytes[6].Should().Be(0x60);
        // Source SSID byte: C=1, R=11, SSID=7, E=1 → 1_11_0111_1 = 0xEF
        bytes[13].Should().Be(0xEF);
        bytes[14].Should().Be(0x73, "UA with F=1");
    }

    [Fact]
    public void I_Frame_With_Payload_Includes_Pid_And_Info_In_Wire_Bytes()
    {
        var payload = "hello"u8.ToArray();
        var frame = Ax25Frame.I(Dest, Src, nr: 4, ns: 2, info: payload, pid: 0xCF, pollBit: false);
        var bytes = frame.ToBytes();

        // 14 (dest+src) + 1 (control) + 1 (pid) + 5 (info) = 21
        bytes.Length.Should().Be(21);
        bytes[14].Should().Be(0x84, "I-frame: N(R)=4 (0x80), N(S)=2 (0x04), P=0 → 0x84");
        bytes[15].Should().Be(0xCF, "PID");
        bytes[16..].Should().Equal(payload);
    }

    // ─── Round-trip via TryParse ───────────────────────────────────────

    [Theory]
    [InlineData(nameof(Ax25Frame.Sabm))]
    [InlineData(nameof(Ax25Frame.Sabme))]
    [InlineData(nameof(Ax25Frame.Disc))]
    [InlineData(nameof(Ax25Frame.Ua))]
    [InlineData(nameof(Ax25Frame.Dm))]
    public void U_Frame_Bytes_Round_Trip_Through_TryParse(string factory)
    {
        Ax25Frame original = factory switch
        {
            nameof(Ax25Frame.Sabm) => Ax25Frame.Sabm(Dest, Src, pollBit: true),
            nameof(Ax25Frame.Sabme) => Ax25Frame.Sabme(Dest, Src, pollBit: false),
            nameof(Ax25Frame.Disc) => Ax25Frame.Disc(Dest, Src, pollBit: true),
            nameof(Ax25Frame.Ua) => Ax25Frame.Ua(Dest, Src, finalBit: true),
            nameof(Ax25Frame.Dm) => Ax25Frame.Dm(Dest, Src, finalBit: false),
            _ => throw new ArgumentOutOfRangeException(nameof(factory)),
        };

        var bytes = original.ToBytes();
        Ax25Frame.TryParse(bytes, out var parsed).Should().BeTrue();
        parsed!.Control.Should().Be(original.Control);
        parsed.IsCommand.Should().Be(original.IsCommand);
        parsed.IsResponse.Should().Be(original.IsResponse);
        parsed.Destination.Callsign.Should().Be(original.Destination.Callsign);
        parsed.Source.Callsign.Should().Be(original.Source.Callsign);
    }

    [Fact]
    public void S_Frame_Bytes_Round_Trip_Preserves_Nr_And_Pf()
    {
        var original = Ax25Frame.Rr(Dest, Src, nr: 6, isCommand: false, pollFinal: true);
        var bytes = original.ToBytes();

        Ax25Frame.TryParse(bytes, out var parsed).Should().BeTrue();
        parsed!.Control.Should().Be(original.Control);
        // Decode N(R) back out: (control >> 5) & 0x07
        ((parsed.Control >> 5) & 0x07).Should().Be(6);
        // P/F bit
        ((parsed.Control & 0x10) != 0).Should().BeTrue();
    }

    [Fact]
    public void I_Frame_Bytes_Round_Trip_Preserves_All_Fields()
    {
        var payload = new byte[] { 0xDE, 0xAD, 0xBE, 0xEF };
        var original = Ax25Frame.I(Dest, Src, nr: 5, ns: 3, info: payload, pid: 0xCC, pollBit: true);
        var bytes = original.ToBytes();

        Ax25Frame.TryParse(bytes, out var parsed).Should().BeTrue();
        parsed!.Control.Should().Be(original.Control);
        ((parsed.Control >> 5) & 0x07).Should().Be(5);
        ((parsed.Control >> 1) & 0x07).Should().Be(3);
        ((parsed.Control & 0x10) != 0).Should().BeTrue();
        ((parsed.Control & 0x01) == 0).Should().BeTrue("I-frame low bit is 0");
        parsed.Pid.Should().Be((byte)0xCC);
        parsed.Info.ToArray().Should().Equal(payload);
    }

    // ─── Digipeater-count boundary on the shared assembly path ──────────

    [Fact]
    public void Non_UI_Factory_Accepts_Exactly_Eight_Digipeaters()
    {
        // §3.12.5 caps repeaters at 8; the boundary value must be accepted on
        // the UFrameAt path (used by every non-UI factory), not just on Ui.
        var eight = Enumerable.Range(0, 8).Select(i => new Callsign($"D{i}", 0));
        var frame = Ax25Frame.Sabm(Dest, Src, pollBit: true, digipeaters: eight);

        frame.Digipeaters.Count.Should().Be(8);
        frame.Digipeaters[7].ExtensionBit.Should().BeTrue("E-bit lands on the last digipeater");
        frame.Digipeaters.Should().OnlyContain(d => d.CrhBit == false,
            "has-been-repeated starts clear on every repeater slot");
    }

    [Fact]
    public void Non_UI_Factory_Rejects_Nine_Digipeaters()
    {
        var nine = Enumerable.Range(0, 9).Select(i => new Callsign($"D{i}", 0));
        var act = () => Ax25Frame.Sabm(Dest, Src, pollBit: true, digipeaters: nine);

        act.Should().Throw<ArgumentException>();
    }
}
