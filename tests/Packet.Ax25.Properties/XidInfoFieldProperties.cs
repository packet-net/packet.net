using AwesomeAssertions;
using FsCheck;
using FsCheck.Xunit;
using Packet.Ax25.Xid;

namespace Packet.Ax25.Properties;

/// <summary>
/// Round-trip + fuzz properties for the AX.25 v2.2 XID information-field codec
/// (<see cref="XidInfoField"/>, §4.3.3.7 / Figure 4.5). The example-based suite
/// <c>tests/Packet.Ax25.Tests/Xid/XidInfoFieldTests.cs</c> pins the spec's
/// worked example and per-parameter cases; these properties widen the envelope
/// across the whole parameter domain and assert the parser is crash-proof on
/// arbitrary attacker-controlled FI/GI/GL/PI/PL/PV bytes.
/// </summary>
/// <remarks>
/// <para>What these pin (per the workstream brief, item b):</para>
/// <list type="bullet">
/// <item>A random <see cref="XidParameters"/> → encode → parse → equal.</item>
/// <item><see cref="XidInfoField.TryParse(System.ReadOnlySpan{byte}, out XidParameters?)"/>
/// never throws on arbitrary bytes, under both Strict and Lenient options.</item>
/// </list>
/// <para>
/// The XID info field is a trust boundary: it is the TLV payload of an XID
/// U-frame received from a peer, so the same "crash on a malformed frame is a
/// link-layer DoS" reasoning that motivates <c>Ax25ParserFuzzProperties</c>
/// applies here.
/// </para>
/// </remarks>
public class XidInfoFieldProperties
{
    /// <summary>
    /// A random parameter set survives encode → parse intact. Each field is
    /// generated within its on-wire-representable domain (duplex is a bool,
    /// reject/modulo are enums/bools, the numeric fields are non-negative and
    /// bounded to the widths the encoder emits), so equality is the right
    /// assertion: the codec is a faithful round-trip over its value space.
    /// </summary>
    [Property(MaxTest = 1_000)]
    public void Random_Parameters_Round_Trip(
        bool? halfDuplex,
        byte hdlcSelector,
        int? n1Octets,
        int? windowK,
        int? ackTimerMs,
        int? retries)
    {
        var parameters = new XidParameters
        {
            ClassesOfProcedures = halfDuplex is { } hd
                ? new ClassesOfProcedures { HalfDuplex = hd }
                : null,
            HdlcOptionalFunctions = BuildHdlc(hdlcSelector),
            // N1 is stored in bits on the wire (×8); generate octets and convert
            // so the encoder's bit unit and the round-trip both stay exact.
            IFieldLengthRxBits = n1Octets is { } oct ? XidParameters.OctetsToBits(Clamp(oct, 0, 8191)) : null,
            WindowSizeRx = windowK is { } k ? Clamp(k, 0, 127) : null,            // 7-bit field
            AckTimerMillis = ackTimerMs is { } t ? Clamp(t, 0, 16_777_215) : null, // up to 3-octet numeric
            Retries = retries is { } n ? Clamp(n, 0, 65_535) : null,              // up to 2-octet numeric
        };

        var encoded = XidInfoField.Encode(parameters);

        // Strict parse: the encoder is spec-strict, so its output must parse
        // strictly with no leniency required.
        XidInfoField.TryParse(encoded, XidParseOptions.Strict, out var decoded)
            .Should().BeTrue("the strict encoder's output must parse under the strict parser");

        decoded.Should().Be(parameters, "encode → parse is a faithful round-trip over the parameter value space");
    }

    /// <summary>
    /// The encoder's output is well-formed: FI/GI are the spec constants and the
    /// Group Length is exactly the parameter-field byte count (excludes the
    /// 4-byte header). Property form of the example-based GL test.
    /// </summary>
    [Property(MaxTest = 500)]
    public void Encoded_Header_Is_Wellformed(
        bool? halfDuplex, byte hdlcSelector, int? n1Octets, int? windowK, int? ackTimerMs, int? retries)
    {
        var parameters = new XidParameters
        {
            ClassesOfProcedures = halfDuplex is { } hd ? new ClassesOfProcedures { HalfDuplex = hd } : null,
            HdlcOptionalFunctions = BuildHdlc(hdlcSelector),
            IFieldLengthRxBits = n1Octets is { } oct ? XidParameters.OctetsToBits(Clamp(oct, 0, 8191)) : null,
            WindowSizeRx = windowK is { } k ? Clamp(k, 0, 127) : null,
            AckTimerMillis = ackTimerMs is { } t ? Clamp(t, 0, 16_777_215) : null,
            Retries = retries is { } n ? Clamp(n, 0, 65_535) : null,
        };

        var encoded = XidInfoField.Encode(parameters);

        encoded.Length.Should().BeGreaterThanOrEqualTo(XidInfoField.HeaderLength);
        encoded[0].Should().Be(XidInfoField.FormatIdentifier);
        encoded[1].Should().Be(XidInfoField.GroupIdentifier);
        int gl = (encoded[2] << 8) | encoded[3];
        gl.Should().Be(encoded.Length - XidInfoField.HeaderLength,
            "Group Length must equal the parameter-field length (§4.3.3.7 ¶1021)");

        // The parameter field is an exact run of complete PI/PL/PV triples in
        // ascending-PI order.
        var pis = new List<byte>();
        int pos = XidInfoField.HeaderLength;
        while (pos < encoded.Length)
        {
            byte pi = encoded[pos];
            (pos + 1).Should().BeLessThan(encoded.Length + 1, "every PI has a PL octet");
            int pl = encoded[pos + 1];
            (pos + 2 + pl).Should().BeLessThanOrEqualTo(encoded.Length, "every PV fits within the field");
            pis.Add(pi);
            pos += 2 + pl;
        }
        pis.Should().BeInAscendingOrder("parameters are emitted in ascending PI order (¶1024)");
    }

    /// <summary>
    /// For any byte array - empty, oversize, all-zero, all-0xFF, well-formed-but-
    /// truncated - <see cref="XidInfoField.TryParse(System.ReadOnlySpan{byte}, XidParseOptions, out XidParameters?)"/>
    /// must terminate without throwing under both option presets, returning a
    /// non-null value on true and null on false.
    /// </summary>
    [Property(MaxTest = 2_000)]
    public void TryParse_Never_Throws(byte[] bytes)
    {
        bytes ??= [];

        bool strict = XidInfoField.TryParse(bytes, XidParseOptions.Strict, out var a);
        if (strict)
        {
            a.Should().NotBeNull();
        }
        else
        {
            a.Should().BeNull();
        }

        bool lenient = XidInfoField.TryParse(bytes, XidParseOptions.Lenient, out var b);
        if (lenient)
        {
            b.Should().NotBeNull();
        }
        else
        {
            b.Should().BeNull();
        }

        // A value the parser accepted must re-encode without throwing - the
        // semantic view always maps back to some well-formed wire form.
        if (strict) { var _ = XidInfoField.Encode(a!); }
        if (lenient) { var _ = XidInfoField.Encode(b!); }
    }

    /// <summary>
    /// Lenient is a strict superset: anything the strict parser accepts, the
    /// lenient parser also accepts and decodes to the same value. (The reverse
    /// doesn't hold - lenient tolerates over-claimed GL / truncated parameters.)
    /// </summary>
    [Property(MaxTest = 2_000)]
    public void Lenient_Accepts_Everything_Strict_Accepts(byte[] bytes)
    {
        bytes ??= [];
        if (!XidInfoField.TryParse(bytes, XidParseOptions.Strict, out var strict))
        {
            return;
        }

        XidInfoField.TryParse(bytes, XidParseOptions.Lenient, out var lenient)
            .Should().BeTrue("lenient must accept any well-formed (strict-acceptable) field");
        lenient.Should().Be(strict, "lenient and strict agree on a well-formed field");
    }

    /// <summary>
    /// A well-formed XID field with arbitrary <em>unknown</em> PIs interleaved
    /// still parses (unknown PIs are skipped per ¶1024), and the recognised
    /// parameter that follows them decodes correctly. Generates the unknown-PI
    /// noise as raw PL-bounded triples so the parser must walk past them.
    /// </summary>
    [Property(MaxTest = 500)]
    public void Unknown_Pis_Are_Skipped_And_Following_Parameter_Decodes(
        byte unknownPiRaw, byte[] unknownPv, byte windowKRaw)
    {
        unknownPv ??= [];
        // Pick a PI that is definitely not an AX.25 parameter (avoid 2,3,5..10).
        byte unknownPi = (byte)(0x20 + (unknownPiRaw % 0x40));   // 0x20..0x5F, all unknown
        var pv = unknownPv.Length > 250 ? unknownPv[..250] : unknownPv;
        byte window = (byte)(windowKRaw & 0x7F);

        var pf = new List<byte>();
        pf.Add(unknownPi);
        pf.Add((byte)pv.Length);
        pf.AddRange(pv);
        pf.Add(XidInfoField.PiWindowSizeRx);
        pf.Add(0x01);
        pf.Add(window);

        var buf = new byte[XidInfoField.HeaderLength + pf.Count];
        buf[0] = XidInfoField.FormatIdentifier;
        buf[1] = XidInfoField.GroupIdentifier;
        buf[2] = (byte)((pf.Count >> 8) & 0xFF);
        buf[3] = (byte)(pf.Count & 0xFF);
        pf.CopyTo(buf, XidInfoField.HeaderLength);

        XidInfoField.TryParse(buf, XidParseOptions.Strict, out var got).Should().BeTrue();
        got!.WindowSizeRx.Should().Be(window, "the recognised parameter after the unknown PI must decode");
    }

    // ─── Helpers ──────────────────────────────────────────────────────────

    /// <summary>
    /// Build an <see cref="HdlcOptionalFunctions"/> from a selector byte, or
    /// null. Only the genuinely-negotiated bits (reject scheme, modulo,
    /// segmenter, SREJ-multiframe) vary - the fixed bits the encoder forces are
    /// not part of the value space.
    /// </summary>
    private static HdlcOptionalFunctions? BuildHdlc(byte selector)
    {
        if ((selector & 0x80) == 0)
        {
            return null;   // ~half the time, absent
        }

        return new HdlcOptionalFunctions
        {
            Reject = (selector & 0x01) != 0 ? RejectMode.ImplicitReject : RejectMode.SelectiveReject,
            Modulo128 = (selector & 0x02) != 0,
            SrejMultiframe = (selector & 0x04) != 0,
            SegmenterReassembler = (selector & 0x08) != 0,
        };
    }

    private static int Clamp(int value, int min, int max) => Math.Clamp(value, min, max);
}
