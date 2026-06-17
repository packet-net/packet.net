using AwesomeAssertions;
using Packet.Ax25;
using Packet.Ax25.Xid;
using Packet.Core;

namespace Packet.Ax25.Tests.Xid;

/// <summary>
/// Codec tests for the AX.25 v2.2 XID information field (§4.3.3.7 / Figure 4.5,
/// worked example Figure 4.6). Mirrors the rigor of
/// <c>Ax25FrameExtendedControlTests</c>: spec-pinned bytes, per-parameter
/// round-trips, absent/optional-field handling, malformed/short-buffer cases,
/// and the strict-vs-lenient pairing the repo requires for any parser leniency.
/// </summary>
public class XidInfoFieldTests
{
    // The information field from Figure 4.6 (NJ7P → N7LEM), parameter field
    // GL = 0x17 (23 octets):
    //   FI GI  GL----  P2 ----------  P3 ---------------  P6 ----------  P8 ----  P9 ----------  PA ----
    //   82 80  00 17   02 02 22 00    03 03 22 A8 82      06 02 04 00    08 01 02 09 02 10 00    0A 01 03
    //
    // NOTE on the HDLC Optional Functions PV (P3 = 03 03 ..): AX.25 v2.2 §3.8 ("Order of
    // Octet and Bit Transmission") sends multiple-octet fields HIGH-ORDER OCTET FIRST, so
    // we transmit/parse the 3-octet value MOST-SIGNIFICANT OCTET FIRST — the order
    // direwolf's xid.c and LinBPQ's L2Code.c both use (verified on the wire: BPQ accepts
    // the MSB-first PV and negotiates SREJ, and silently drops the historical LSB-first
    // one). The printed Figure 4.6 octets `82 A8 22` are LSB-octet first and so CONTRADICT
    // §3.8 — a figure-rendering error (like the same figure's documented ABM off-by-one).
    // Our HdlcOptionalFunctions bit constants are numbered in the LSB-octet value space,
    // so the SAME logical selection (REJ + modulo-128 + SREJ-multiframe + the always-1
    // bits) serialises §3.8-correct to `22 A8 82` here (the byte-reverse of the figure's
    // print). The decode below recovers the identical selection. See
    // docs/strict-vs-pragmatic-audit.md (HDLC-Opt-Functions octet order).
    private static readonly byte[] Figure46Info =
    {
        0x82, 0x80, 0x00, 0x17,
        0x02, 0x02, 0x22, 0x00,
        0x03, 0x03, 0x22, 0xA8, 0x82,
        0x06, 0x02, 0x04, 0x00,
        0x08, 0x01, 0x02,
        0x09, 0x02, 0x10, 0x00,
        0x0A, 0x01, 0x03,
    };

    // ─── Header constants (§4.3.3.7 ¶1019–1021) ─────────────────────────

    [Fact]
    public void Header_Constants_Match_Spec()
    {
        XidInfoField.FormatIdentifier.Should().Be(0x82, "FI = 82 hex, general-purpose XID (¶1019)");
        XidInfoField.GroupIdentifier.Should().Be(0x80, "GI = 80 hex, parameter negotiation (¶1020)");
        // PI numbers per Figure 4.5.
        XidInfoField.PiClassesOfProcedures.Should().Be(2);
        XidInfoField.PiHdlcOptionalFunctions.Should().Be(3);
        XidInfoField.PiIFieldLengthRx.Should().Be(6);
        XidInfoField.PiWindowSizeRx.Should().Be(8);
        XidInfoField.PiAckTimer.Should().Be(9);
        XidInfoField.PiRetries.Should().Be(0x0A);
    }

    // ─── Parse: the full Figure 4.6 worked example ──────────────────────

    [Fact]
    public void Parses_Figure_4_6_WorkedExample()
    {
        XidInfoField.TryParse(Figure46Info, out var p).Should().BeTrue();

        // Classes of Procedures: PV 0x22 0x00 ⇒ ABM + half-duplex.
        p!.ClassesOfProcedures.Should().NotBeNull();
        p.ClassesOfProcedures!.HalfDuplex.Should().BeTrue("PV 0x22 0x00 sets bit 5 (half-duplex)");

        // HDLC Optional Functions: PV 0x22 0xA8 0x82 (MSB-octet first — see the
        // Figure46Info note). Decoded, these are REJ (bit 1) + modulo-128 (bit 11)
        // + the always-1 bits + SREJ-multiframe (bit 21). NOTE: Figure 4.6's caption
        // claims "SREJ/REJ"; the selection is REJ (bit 1 set, bit 2 clear),
        // contradicting the caption. We decode the bytes faithfully.
        p.HdlcOptionalFunctions.Should().NotBeNull();
        p.HdlcOptionalFunctions!.Reject.Should().Be(RejectMode.ImplicitReject,
            "Fig 4.6 bytes set bit 1 (REJ), not bit 2 (SREJ) — the caption is loose");
        p.HdlcOptionalFunctions.Modulo128.Should().BeTrue("bit 11 set ⇒ modulo 128");
        p.HdlcOptionalFunctions.SrejMultiframe.Should().BeTrue("bit 21 set in Fig 4.6");
        p.HdlcOptionalFunctions.SegmenterReassembler.Should().BeFalse("bit 22 clear ⇒ no segmenter");

        // N1 Rx: PV 0x04 0x00 = 1024 bits = 128 octets.
        p.IFieldLengthRxBits.Should().Be(1024);
        p.IFieldLengthRxOctets.Should().Be(128);

        // Window k Rx: PV 0x02 = 2 frames.
        p.WindowSizeRx.Should().Be(2);

        // T1: PV 0x10 0x00 = 4096 ms.
        p.AckTimerMillis.Should().Be(4096);
        p.AckTimer.Should().Be(TimeSpan.FromMilliseconds(4096));

        // N2: PV 0x03 = 3 retries.
        p.Retries.Should().Be(3);
    }

    [Fact]
    public void Encode_Reproduces_Figure_4_6_ExceptForFigureAbmAnomaly()
    {
        // Build the parameters the Figure 4.6 bytes encode (REJ + mod128 +
        // SREJ-multiframe, half-duplex, N1=1024 bits, k=2, T1=4096, N2=3).
        var parameters = new XidParameters
        {
            ClassesOfProcedures = ClassesOfProcedures.HalfDuplexDefault,
            HdlcOptionalFunctions = new HdlcOptionalFunctions
            {
                Reject = RejectMode.ImplicitReject,
                Modulo128 = true,
                SrejMultiframe = true,
                SegmenterReassembler = false,
            },
            IFieldLengthRxBits = 1024,
            WindowSizeRx = 2,
            AckTimerMillis = 4096,
            Retries = 3,
        };

        var encoded = XidInfoField.Encode(parameters);

        // KNOWN SPEC DEFECT (Figure 4.6 vs Figure 4.5 table / §6.3.2 prose):
        // the Classes-of-Procedures Balanced-ABM bit is "Bit 0" per the Figure
        // 4.5 table (and ¶1077 "Bit 0 is always a 1"), so half-duplex ABM
        // encodes as 0x21 0x00. Figure 4.6's worked example instead shows
        // 0x22 0x00 — it has placed the always-1 ABM bit at position 1, NOT 0.
        // (The HDLC field's PV is serialised MSB-octet-first per §3.8 — `22 A8 82` for
        // this selection — matching direwolf / BPQ on the wire; the figure's printed
        // `82 A8 22` is the LSB-first contradiction, see the Figure46Info note.) Per the
        // repo's spec-compliant-by-default rule we
        // follow the normative table for the ABM bit,
        // so byte index 6 is 0x21, not the figure's 0x22. Everything else
        // reproduces Figure 4.6 byte-for-byte. The duplex selection (bit 5)
        // — the only field a peer actually reads — is identical either way.
        const int abmAnomalyIndex = 6;
        encoded[abmAnomalyIndex].Should().Be(0x21,
            "table/prose put Balanced-ABM at bit 0 (0x21); Fig 4.6's 0x22 is the figure's off-by-one");
        Figure46Info[abmAnomalyIndex].Should().Be(0x22, "the literal figure byte, for contrast");

        // Splice the figure's anomalous byte in and the rest must match exactly.
        var encodedWithFigureAbm = (byte[])encoded.Clone();
        encodedWithFigureAbm[abmAnomalyIndex] = 0x22;
        encodedWithFigureAbm.Should().Equal(Figure46Info,
            "every other octet reproduces the Figure 4.6 worked example exactly");
    }

    // ─── Encode: header + group-length framing ──────────────────────────

    [Fact]
    public void Encode_Empty_Parameters_Emits_Bare_Header_With_Zero_GroupLength()
    {
        // No fields set ⇒ FI GI GL=0000, an "all defaults" XID info field (¶1021).
        var bytes = XidInfoField.Encode(new XidParameters());
        bytes.Should().Equal(0x82, 0x80, 0x00, 0x00);
    }

    [Fact]
    public void Encode_Sets_GroupLength_To_ParameterFieldLength_Only()
    {
        // GL excludes FI/GI/GL themselves. One window param (PI+PL+1 = 3 bytes).
        var bytes = XidInfoField.Encode(new XidParameters { WindowSizeRx = 7 });
        bytes[0].Should().Be(0x82);
        bytes[1].Should().Be(0x80);
        bytes[2].Should().Be(0x00);
        bytes[3].Should().Be(0x03, "GL counts only the PI/PL/PV bytes (3) — not the 4-byte header");
        bytes[4..].Should().Equal(0x08, 0x01, 0x07);
    }

    [Fact]
    public void Encode_Orders_Parameters_By_Ascending_PI()
    {
        // Set them out of order; the wire must be ascending PI (¶1024).
        var bytes = XidInfoField.Encode(new XidParameters
        {
            Retries = 5,                                   // PI 0x0A
            ClassesOfProcedures = ClassesOfProcedures.HalfDuplexDefault, // PI 0x02
            WindowSizeRx = 4,                              // PI 0x08
            AckTimerMillis = 3000,                         // PI 0x09
        });

        // Collect the PI octets in wire order.
        var pis = new List<byte>();
        int pos = XidInfoField.HeaderLength;
        while (pos < bytes.Length)
        {
            pis.Add(bytes[pos]);
            int pl = bytes[pos + 1];
            pos += 2 + pl;
        }

        pis.Should().Equal(0x02, 0x08, 0x09, 0x0A);
        pis.Should().BeInAscendingOrder();
    }

    // ─── Round-trip: each parameter individually ────────────────────────

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void RoundTrip_ClassesOfProcedures_Duplex(bool halfDuplex)
    {
        var p = new XidParameters
        {
            ClassesOfProcedures = new ClassesOfProcedures { HalfDuplex = halfDuplex },
        };

        XidInfoField.TryParse(XidInfoField.Encode(p), out var got).Should().BeTrue();
        got!.ClassesOfProcedures!.HalfDuplex.Should().Be(halfDuplex);
    }

    [Fact]
    public void ClassesOfProcedures_Always_Sets_Abm_Bit()
    {
        // bit 0 (ABM) is always 1 (Figure 4.5); half-duplex sets bit 5 ⇒ 0x21.
        ClassesOfProcedures.HalfDuplexDefault.ToOctets().Should().Equal(0x21, 0x00);
        // full-duplex sets bit 6 ⇒ 0x41, ABM still set.
        ClassesOfProcedures.FullDuplexCapable.ToOctets().Should().Equal(0x41, 0x00);
    }

    [Theory]
    [InlineData(RejectMode.ImplicitReject, true)]
    [InlineData(RejectMode.ImplicitReject, false)]
    [InlineData(RejectMode.SelectiveReject, true)]
    [InlineData(RejectMode.SelectiveReject, false)]
    public void RoundTrip_HdlcOptionalFunctions_RejectAndModulo(RejectMode reject, bool mod128)
    {
        var p = new XidParameters
        {
            HdlcOptionalFunctions = new HdlcOptionalFunctions { Reject = reject, Modulo128 = mod128 },
        };

        XidInfoField.TryParse(XidInfoField.Encode(p), out var got).Should().BeTrue();
        got!.HdlcOptionalFunctions!.Reject.Should().Be(reject);
        got.HdlcOptionalFunctions.Modulo128.Should().Be(mod128);
    }

    [Fact]
    public void HdlcOptionalFunctions_Forces_AlwaysOne_Bits()
    {
        // Default = SREJ + mod128. Verify the always-1 bits (7=ext addr,
        // 13=TEST, 15=16fcs, 17=sync tx) are set and the SREJ/mod bits encode
        // as the prose prescribes (SREJ ⇒ bit2; mod128 ⇒ bit11).
        // ToOctets() serialises MSB-octet first (octets[0] is bits 16-23); rebuild
        // the 24-bit field accordingly before checking the (order-independent) bit
        // positions. See the Figure46Info octet-order note.
        var octets = HdlcOptionalFunctions.Default.ToOctets();
        long field = ((long)octets[0] << 16) | ((long)octets[1] << 8) | octets[2];

        ((field >> 7) & 1).Should().Be(1, "bit 7 extended address always 1");
        ((field >> 13) & 1).Should().Be(1, "bit 13 TEST always 1");
        ((field >> 15) & 1).Should().Be(1, "bit 15 16-bit FCS always 1");
        ((field >> 17) & 1).Should().Be(1, "bit 17 synchronous Tx always 1");
        ((field >> 1) & 1).Should().Be(0, "SREJ selected ⇒ bit 1 (REJ) reset");
        ((field >> 2) & 1).Should().Be(1, "SREJ selected ⇒ bit 2 set");
        ((field >> 10) & 1).Should().Be(0, "mod128 ⇒ bit 10 (mod8) reset");
        ((field >> 11) & 1).Should().Be(1, "mod128 ⇒ bit 11 set");
    }

    [Fact]
    public void RoundTrip_HdlcOptionalFunctions_SegmenterAndSrejMultiframe()
    {
        var p = new XidParameters
        {
            HdlcOptionalFunctions = new HdlcOptionalFunctions
            {
                Reject = RejectMode.SelectiveReject,
                Modulo128 = true,
                SrejMultiframe = true,
                SegmenterReassembler = true,
            },
        };

        XidInfoField.TryParse(XidInfoField.Encode(p), out var got).Should().BeTrue();
        got!.HdlcOptionalFunctions!.SrejMultiframe.Should().BeTrue();
        got.HdlcOptionalFunctions.SegmenterReassembler.Should().BeTrue();
    }

    [Theory]
    [InlineData(2048)]   // default N1 (256 octets)
    [InlineData(1024)]   // Fig 4.6
    [InlineData(8)]      // 1 octet — exercises the single-byte numeric encoding
    [InlineData(65535)]  // two-octet boundary
    public void RoundTrip_IFieldLengthRx_Bits(int bits)
    {
        var p = new XidParameters { IFieldLengthRxBits = bits };
        XidInfoField.TryParse(XidInfoField.Encode(p), out var got).Should().BeTrue();
        got!.IFieldLengthRxBits.Should().Be(bits);
        got.IFieldLengthRxOctets.Should().Be(bits / 8);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(4)]      // mod-8 default
    [InlineData(32)]     // mod-128 default
    [InlineData(127)]    // max
    public void RoundTrip_WindowSizeRx(int k)
    {
        var p = new XidParameters { WindowSizeRx = k };
        XidInfoField.TryParse(XidInfoField.Encode(p), out var got).Should().BeTrue();
        got!.WindowSizeRx.Should().Be(k);
    }

    [Theory]
    [InlineData(3000)]   // default T1
    [InlineData(4096)]   // Fig 4.6
    [InlineData(255)]    // single octet
    [InlineData(60000)]  // two octets
    public void RoundTrip_AckTimer(int millis)
    {
        var p = new XidParameters { AckTimerMillis = millis };
        XidInfoField.TryParse(XidInfoField.Encode(p), out var got).Should().BeTrue();
        got!.AckTimerMillis.Should().Be(millis);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(10)]     // default N2
    [InlineData(255)]
    public void RoundTrip_Retries(int n2)
    {
        var p = new XidParameters { Retries = n2 };
        XidInfoField.TryParse(XidInfoField.Encode(p), out var got).Should().BeTrue();
        got!.Retries.Should().Be(n2);
    }

    [Fact]
    public void RoundTrip_All_Parameters_Together()
    {
        var p = new XidParameters
        {
            ClassesOfProcedures = ClassesOfProcedures.FullDuplexCapable,
            HdlcOptionalFunctions = new HdlcOptionalFunctions
            {
                Reject = RejectMode.SelectiveReject,
                Modulo128 = true,
                SegmenterReassembler = true,
            },
            IFieldLengthRxBits = XidParameters.OctetsToBits(256),
            WindowSizeRx = 32,
            AckTimerMillis = 3000,
            Retries = 10,
        };

        XidInfoField.TryParse(XidInfoField.Encode(p), out var got).Should().BeTrue();
        got.Should().Be(p, "the full parameter set round-trips through encode→parse");
    }

    // ─── Absent / optional fields (¶1024) ───────────────────────────────

    [Fact]
    public void Absent_Fields_Parse_As_Null_Not_Default()
    {
        // Only window present; every other field must be null (= "use current"),
        // distinct from a present-but-default value.
        XidInfoField.TryParse(XidInfoField.Encode(new XidParameters { WindowSizeRx = 4 }), out var got)
            .Should().BeTrue();

        got!.WindowSizeRx.Should().Be(4);
        got.ClassesOfProcedures.Should().BeNull();
        got.HdlcOptionalFunctions.Should().BeNull();
        got.IFieldLengthRxBits.Should().BeNull();
        got.AckTimerMillis.Should().BeNull();
        got.Retries.Should().BeNull();
    }

    [Fact]
    public void Empty_Parameter_Field_Parses_To_All_Null()
    {
        // GL=0 ⇒ all defaults ⇒ every decoded field null.
        XidInfoField.TryParse(new byte[] { 0x82, 0x80, 0x00, 0x00 }, out var got).Should().BeTrue();
        got.Should().Be(new XidParameters());
    }

    [Fact]
    public void Zero_Length_PV_Is_Absent_Parameter()
    {
        // A PI with PL=0 means "PV absent, take default" (¶1024) ⇒ field stays null.
        var info = new byte[] { 0x82, 0x80, 0x00, 0x02, XidInfoField.PiWindowSizeRx, 0x00 };
        XidInfoField.TryParse(info, out var got).Should().BeTrue();
        got!.WindowSizeRx.Should().BeNull("PL=0 ⇒ PV absent ⇒ parameter takes its default (null here)");
    }

    [Fact]
    public void Unrecognised_Pi_Is_Skipped()
    {
        // PI 0x42 (not an AX.25 parameter) followed by a real window param.
        // The unknown one is ignored (¶1024); the window still decodes.
        var info = new byte[]
        {
            0x82, 0x80, 0x00, 0x07,
            0x42, 0x02, 0xDE, 0xAD,            // unknown PI, 2-byte PV — skipped
            0x08, 0x01, 0x05,                  // window k = 5
        };
        XidInfoField.TryParse(info, out var got).Should().BeTrue();
        got!.WindowSizeRx.Should().Be(5);
    }

    [Fact]
    public void Tx_Variants_Pi5_Pi7_Are_Skipped()
    {
        // PI=5 (I-field length Tx) and PI=7 (window Tx) are ISO-8885-only and
        // not surfaced; a following Rx window still decodes.
        var info = new byte[]
        {
            0x82, 0x80, 0x00, 0x0A,
            0x05, 0x02, 0x08, 0x00,            // PI=5 Tx N1 — skipped
            0x07, 0x01, 0x10,                  // PI=7 Tx window — skipped
            0x08, 0x01, 0x05,                  // PI=8 Rx window = 5
        };
        XidInfoField.TryParse(info, out var got).Should().BeTrue();
        got!.WindowSizeRx.Should().Be(5);
    }

    // ─── Malformed / short buffers ──────────────────────────────────────

    [Theory]
    [InlineData(new byte[] { })]                                  // empty
    [InlineData(new byte[] { 0x82 })]                             // FI only
    [InlineData(new byte[] { 0x82, 0x80, 0x00 })]                 // header truncated (no 2nd GL byte)
    public void TryParse_Rejects_Short_Header(byte[] info)
    {
        XidInfoField.TryParse(info, out var got).Should().BeFalse();
        got.Should().BeNull();
    }

    [Fact]
    public void TryParse_Rejects_Wrong_FormatIdentifier()
    {
        XidInfoField.TryParse(new byte[] { 0x81, 0x80, 0x00, 0x00 }, out _).Should().BeFalse();
    }

    [Fact]
    public void TryParse_Rejects_Wrong_GroupIdentifier()
    {
        XidInfoField.TryParse(new byte[] { 0x82, 0x81, 0x00, 0x00 }, out _).Should().BeFalse();
    }

    [Fact]
    public void Strict_Rejects_GroupLength_Overrun_But_Lenient_Clamps()
    {
        // GL claims 8 parameter bytes; only 3 follow.
        var info = new byte[] { 0x82, 0x80, 0x00, 0x08, 0x08, 0x01, 0x05 };

        XidInfoField.TryParse(info, XidParseOptions.Strict, out _)
            .Should().BeFalse("strict spec: GL must equal the parameter-field length (¶1021)");

        XidInfoField.TryParse(info, XidParseOptions.Lenient, out var got)
            .Should().BeTrue("lenient clamps GL to the available bytes");
        got!.WindowSizeRx.Should().Be(5);
    }

    [Fact]
    public void Strict_Rejects_Truncated_Parameter_But_Lenient_Tolerates()
    {
        // GL=4: a window param (3 bytes) then a stray PI 0x09 with no PL octet.
        var info = new byte[] { 0x82, 0x80, 0x00, 0x04, 0x08, 0x01, 0x05, 0x09 };

        XidInfoField.TryParse(info, XidParseOptions.Strict, out _)
            .Should().BeFalse("strict: parameter field must be exact PI/PL/PV triples");

        XidInfoField.TryParse(info, XidParseOptions.Lenient, out var got)
            .Should().BeTrue("lenient drops the trailing partial parameter");
        got!.WindowSizeRx.Should().Be(5);
    }

    [Fact]
    public void Strict_Rejects_Pv_Longer_Than_Remaining_But_Lenient_Truncates()
    {
        // GL=4: PI 0x09 (T1) with PL=3 but only 1 PV byte before the field ends.
        var info = new byte[] { 0x82, 0x80, 0x00, 0x04, 0x09, 0x03, 0x10 };

        XidInfoField.TryParse(info, XidParseOptions.Strict, out _).Should().BeFalse();
        XidInfoField.TryParse(info, XidParseOptions.Lenient, out var got).Should().BeTrue();
        // Lenient reads the 1 available octet ⇒ 0x10 = 16.
        got!.AckTimerMillis.Should().Be(0x10);
    }

    // ─── Integration with the XID U-frame factory ───────────────────────

    [Fact]
    public void Codec_Output_Drives_Ax25Frame_Xid_And_RoundTrips_Off_The_Wire()
    {
        var dest = new Callsign("M0LTE", 0);
        var src = new Callsign("G7XYZ", 7);
        var negotiated = new XidParameters
        {
            ClassesOfProcedures = ClassesOfProcedures.HalfDuplexDefault,
            HdlcOptionalFunctions = HdlcOptionalFunctions.Default,
            IFieldLengthRxBits = XidParameters.OctetsToBits(256),
            WindowSizeRx = 32,
            AckTimerMillis = 3000,
            Retries = 10,
        };

        byte[] info = XidInfoField.Encode(negotiated);
        var frame = Ax25Frame.Xid(dest, src, info, isCommand: true, pollFinal: true);

        // The XID frame carries our info field verbatim, and a full wire
        // round-trip (encode → bytes → parse → info → decode) recovers it.
        frame.Info.ToArray().Should().Equal(info);

        Ax25Frame.TryParse(frame.ToBytes(), Ax25ParseOptions.Lenient, extended: false, out var parsedFrame)
            .Should().BeTrue();
        XidInfoField.TryParse(parsedFrame!.Info.Span, out var decoded).Should().BeTrue();
        decoded.Should().Be(negotiated);
    }
}
