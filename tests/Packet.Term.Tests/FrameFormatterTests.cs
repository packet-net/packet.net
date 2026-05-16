using System.Text;
using Packet.Ax25;
using Packet.Core;
using Packet.Term;

namespace Packet.Term.Tests;

/// <summary>
/// Cover the BPQ-monitor-style frame formatter against a representative
/// sample of frame shapes. Asserts on the text output rather than parsing
/// it back — the format is the contract.
/// </summary>
public class FrameFormatterTests
{
    private static readonly DateTimeOffset FixedTs =
        new(2026, 5, 16, 12, 34, 56, TimeSpan.Zero);

    private static readonly Callsign Me = new("M0LTE", 1);
    private static readonly Callsign Peer = new("G1AAA", 0);

    [Fact]
    public void Undecodable_Bytes_Render_With_Length_Note()
    {
        // Three bytes that obviously aren't an AX.25 frame.
        var line = FrameFormatter.Format(FrameDirection.Receive, new byte[] { 0xAA, 0xBB, 0xCC }, FixedTs);
        line.Should().Contain("<undecodable 3B>");
        line.Should().StartWith("12:34:56 R");
    }

    [Fact]
    public void Sabm_Command_With_Poll_Renders_With_P_Marker()
    {
        // SABM command, P=1, dest=Peer (we're calling them), source=Me.
        var sabm = BuildUFrame(dest: Peer, source: Me, uBase: 0x2F, pollFinal: true, isCommand: true);
        var line = FrameFormatter.Format(FrameDirection.Transmit, sabm, FixedTs);
        line.Should().Be("12:34:56 T M0LTE-1>G1AAA <SABM C P>");
    }

    [Fact]
    public void Ua_Response_With_Final_Renders_With_F_Marker()
    {
        var ua = BuildUFrame(dest: Me, source: Peer, uBase: 0x63, pollFinal: true, isCommand: false);
        var line = FrameFormatter.Format(FrameDirection.Receive, ua, FixedTs);
        line.Should().Be("12:34:56 R G1AAA>M0LTE-1 <UA R F>");
    }

    [Fact]
    public void Disc_Command_Renders_With_Command_Marker()
    {
        var disc = BuildUFrame(dest: Peer, source: Me, uBase: 0x43, pollFinal: true, isCommand: true);
        var line = FrameFormatter.Format(FrameDirection.Transmit, disc, FixedTs);
        line.Should().Be("12:34:56 T M0LTE-1>G1AAA <DISC C P>");
    }

    [Fact]
    public void Dm_Response_Renders_As_DM_R()
    {
        var dm = BuildUFrame(dest: Me, source: Peer, uBase: 0x0F, pollFinal: false, isCommand: false);
        var line = FrameFormatter.Format(FrameDirection.Receive, dm, FixedTs);
        line.Should().Be("12:34:56 R G1AAA>M0LTE-1 <DM R>");
    }

    [Fact]
    public void Rr_Frame_Renders_With_N_R()
    {
        // RR response, N(R)=3, F=1. Control byte: NR(7-5) | 0 | F | 0001 = 011 0 1 0001 = 0x71.
        var rr = BuildSFrame(dest: Me, source: Peer, sBase: 0x01, nr: 3, pollFinal: true, isCommand: false);
        var line = FrameFormatter.Format(FrameDirection.Receive, rr, FixedTs);
        line.Should().Be("12:34:56 R G1AAA>M0LTE-1 <RR R R3 F>");
    }

    [Fact]
    public void I_Frame_Renders_With_N_R_N_S_And_Info()
    {
        // I-frame N(R)=2 N(S)=1 P=1. Control: NR(7-5)=010 | P=1 | NS(3-1)=001 | 0
        // = 010 1 0010 = 0x52.
        var info = Encoding.ASCII.GetBytes("hello\r");
        var i = BuildIFrame(dest: Peer, source: Me, ns: 1, nr: 2, pollFinal: true, info: info);
        var line = FrameFormatter.Format(FrameDirection.Transmit, i, FixedTs);
        line.Should().Contain("12:34:56 T M0LTE-1>G1AAA <I C R2 S1 P>");
        // Info field shown on next line, indented, trailing CR stripped.
        line.Should().Contain("\n    hello");
    }

    private static Ax25Frame BuildUFrame(Callsign dest, Callsign source, byte uBase, bool pollFinal, bool isCommand)
    {
        byte control = (byte)(uBase | (pollFinal ? 0x10 : 0x00));
        return BuildFrame(dest, source, control, pid: null, info: ReadOnlySpan<byte>.Empty, isCommand: isCommand);
    }

    private static Ax25Frame BuildSFrame(Callsign dest, Callsign source, byte sBase, byte nr, bool pollFinal, bool isCommand)
    {
        // S-frame: NR(7-5) | P/F(4) | SS(3-2) | 0 | 1.  sBase already encodes "ss01".
        byte control = (byte)(((nr & 0x07) << 5) | (pollFinal ? 0x10 : 0x00) | (sBase & 0x0F));
        return BuildFrame(dest, source, control, pid: null, info: ReadOnlySpan<byte>.Empty, isCommand: isCommand);
    }

    private static Ax25Frame BuildIFrame(Callsign dest, Callsign source, byte ns, byte nr, bool pollFinal, byte[] info)
    {
        // I-frame: NR(7-5) | P(4) | NS(3-1) | 0
        byte control = (byte)(((nr & 0x07) << 5) | (pollFinal ? 0x10 : 0x00) | ((ns & 0x07) << 1));
        return BuildFrame(dest, source, control, pid: Ax25Frame.PidNoLayer3, info: info, isCommand: true);
    }

    private static Ax25Frame BuildFrame(Callsign dest, Callsign source, byte control, byte? pid, ReadOnlySpan<byte> info, bool isCommand)
    {
        // Hand-construct the wire bytes and re-parse so we get an
        // address-field-correct Ax25Frame without depending on a
        // frame-factory we don't control.
        // §6.1.2: command has dest C=1, source C=0; response has the inverse.
        var destAddr = new Ax25Address(dest, CrhBit: isCommand, ExtensionBit: false);
        var srcAddr  = new Ax25Address(source, CrhBit: !isCommand, ExtensionBit: true);

        int infoLen = info.Length;
        int len = 14 + 1 + (pid.HasValue ? 1 : 0) + infoLen;
        var buf = new byte[len];
        destAddr.Write(buf.AsSpan(0));
        srcAddr.Write(buf.AsSpan(7));
        buf[14] = control;
        int offset = 15;
        if (pid.HasValue) { buf[offset++] = pid.Value; }
        info.CopyTo(buf.AsSpan(offset));

        Ax25Frame.TryParse(buf, out var parsed).Should().BeTrue("hand-built bytes must round-trip through the parser");
        return parsed!;
    }
}
