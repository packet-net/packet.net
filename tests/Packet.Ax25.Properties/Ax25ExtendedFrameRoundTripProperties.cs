using AwesomeAssertions;
using FsCheck;
using FsCheck.Xunit;
using Packet.Ax25;
using Packet.Ax25.Session;
using Packet.Core;

namespace Packet.Ax25.Properties;

/// <summary>
/// Round-trip + fuzz properties for the AX.25 v2.2 EXTENDED (modulo-128) frame
/// surface - the 2-octet control field (Fig 4.1b) that the mod-8 properties in
/// <see cref="Ax25FrameRoundTripProperties"/> / <see cref="Ax25ParserFuzzProperties"/>
/// never touch. An extended I/S frame's control field is mode-dependent and not
/// derivable from the octets alone, so the parse path is exercised with
/// <c>extended: true</c> exactly as the receive path does for a SABME-negotiated
/// link.
/// </summary>
/// <remarks>
/// <para>What these pin (per the workstream brief, item a):</para>
/// <list type="bullet">
/// <item>A random mod-128 I frame → encode → parse-as-extended → classify →
/// fields match, including the 7-bit N(S)/N(R) and the P/F bit, and the
/// 127→0 wrap.</item>
/// <item>A random mod-128 S frame (RR/RNR/REJ/SREJ) → encode → parse → classify
/// → 7-bit N(R) + P/F + role match.</item>
/// <item>The extended parser never throws on arbitrary bytes (the mod-8
/// <c>TryParse_Never_Throws</c> sibling, but down the 2-octet-control branch).</item>
/// </list>
/// <para>
/// Callsigns are sanitised the same way as <see cref="Ax25FrameRoundTripProperties"/>
/// so shrinking doesn't chase address-validation rejections - the subject under
/// test is the control-field codec, not the address codec.
/// </para>
/// </remarks>
public class Ax25ExtendedFrameRoundTripProperties
{
    /// <summary>
    /// A random extended I-frame round-trips: encode → parse-as-extended →
    /// classify as <see cref="IFrameReceived"/> → 7-bit N(S)/N(R), P/F, PID and
    /// info all match. N(S)/N(R) are taken modulo 128, so this exercises the full
    /// 0..127 range including the boundary.
    /// </summary>
    [Property(MaxTest = 500)]
    public void Extended_I_Frame_Round_Trips_With_7Bit_Sequence_Numbers(
        NonEmptyString destBase, byte destSsidRaw,
        NonEmptyString srcBase, byte srcSsidRaw,
        byte nsRaw, byte nrRaw, bool pollBit, byte pid, byte[] info)
    {
        info ??= [];
        var dest = SanitiseCallsign(destBase.Get, destSsidRaw);
        var src = SanitiseCallsign(srcBase.Get, srcSsidRaw);
        var ns = (byte)(nsRaw % 128);
        var nr = (byte)(nrRaw % 128);

        var original = Ax25Frame.I(dest, src, nr: nr, ns: ns, info: info,
            pid: pid, pollBit: pollBit, extended: true);

        // An extended frame carries a 2-octet control field.
        original.IsExtendedControl.Should().BeTrue("Ax25Frame.I(extended: true) builds a 2-octet control field");
        original.RequiredBytes.Should().Be(7 + 7 + 2 + 1 + info.Length);

        var bytes = original.ToBytes();
        Ax25Frame.TryParse(bytes, Ax25ParseOptions.Lenient, extended: true, out var decoded)
            .Should().BeTrue();

        decoded!.IsExtendedControl.Should().BeTrue("parsed as extended ⇒ 2-octet control recovered");
        decoded.Ns.Should().Be(ns, "7-bit N(S) (Fig 4.1b first octet bits 7-1) round-trips");
        decoded.Nr.Should().Be(nr, "7-bit N(R) (Fig 4.1b second octet bits 7-1) round-trips");
        decoded.PollFinal.Should().Be(pollBit, "P bit migrates to bit 0 of the second control octet");
        decoded.Pid.Should().Be(pid);
        decoded.Info.ToArray().Should().Equal(info);

        // Classification reads only the first control octet (modulo-independent),
        // so an extended I-frame still classifies as IFrameReceived, and its
        // mode-aware N(S)/N(R) are correct.
        var ev = Ax25FrameClassifier.Classify(decoded);
        ev.Should().BeOfType<IFrameReceived>();
        ((IFrameReceived)ev).Frame.Ns.Should().Be(ns);
        ((IFrameReceived)ev).Frame.Nr.Should().Be(nr);
    }

    /// <summary>
    /// The 127→0 N(S)/N(R) wrap is representable and round-trips. Pinning the
    /// boundary specifically because mod-8 can never reach it and an off-by-one
    /// in the 7-bit shift/mask would only bite at the top of the range.
    /// </summary>
    [Property(MaxTest = 200)]
    public void Extended_Sequence_Numbers_Wrap_At_128(byte stepRaw, bool atNr)
    {
        // Walk a sequence value across the 127→0 boundary and assert each step
        // encodes/decodes to (value mod 128).
        var dest = new Callsign("M0LTE", 0);
        var src = new Callsign("G7XYZ", 7);
        int start = 120;
        int steps = stepRaw % 20;            // crosses 127→0 for steps ≥ 8

        for (int i = 0; i <= steps; i++)
        {
            var value = (byte)((start + i) % 128);
            var frame = atNr
                ? Ax25Frame.I(dest, src, nr: value, ns: 0, info: [0xAA], extended: true)
                : Ax25Frame.I(dest, src, nr: 0, ns: value, info: [0xAA], extended: true);

            Ax25Frame.TryParse(frame.ToBytes(), Ax25ParseOptions.Lenient, extended: true, out var decoded)
                .Should().BeTrue();
            var recovered = atNr ? decoded!.Nr : decoded!.Ns;
            recovered.Should().Be(value, $"value {value} (start {start} + {i}) must survive the 7-bit codec");
        }
    }

    /// <summary>
    /// A random extended supervisory frame round-trips and classifies to the
    /// right S-type with its 7-bit N(R) and P/F intact. S-frames carry no info
    /// field, so the second control octet is the only sequence-bearing octet.
    /// </summary>
    [Property(MaxTest = 500)]
    public void Extended_S_Frame_Round_Trips_And_Classifies(
        NonEmptyString destBase, byte destSsidRaw,
        NonEmptyString srcBase, byte srcSsidRaw,
        byte sTypeRaw, byte nrRaw, bool pollFinal, bool isCommand)
    {
        var dest = SanitiseCallsign(destBase.Get, destSsidRaw);
        var src = SanitiseCallsign(srcBase.Get, srcSsidRaw);
        var nr = (byte)(nrRaw % 128);

        var (build, expected) = (sTypeRaw % 4) switch
        {
            0 => ((Func<Ax25Frame>)(() => Ax25Frame.Rr(dest, src, nr, isCommand, pollFinal, extended: true)), typeof(RrReceived)),
            1 => (() => Ax25Frame.Rnr(dest, src, nr, isCommand, pollFinal, extended: true), typeof(RnrReceived)),
            2 => (() => Ax25Frame.Rej(dest, src, nr, isCommand, pollFinal, extended: true), typeof(RejReceived)),
            _ => (() => Ax25Frame.Srej(dest, src, nr, isCommand, pollFinal, extended: true), typeof(SrejReceived)),
        };

        var original = build();
        original.IsExtendedControl.Should().BeTrue();
        original.RequiredBytes.Should().Be(7 + 7 + 2, "extended S-frame: addresses + 2 control octets, no PID/info");

        Ax25Frame.TryParse(original.ToBytes(), Ax25ParseOptions.Lenient, extended: true, out var decoded)
            .Should().BeTrue();

        decoded!.IsExtendedControl.Should().BeTrue();
        decoded.Nr.Should().Be(nr, "7-bit N(R) round-trips for an extended S-frame");
        decoded.PollFinal.Should().Be(pollFinal);
        decoded.IsCommand.Should().Be(isCommand);
        decoded.IsResponse.Should().Be(!isCommand);
        decoded.Info.ToArray().Should().BeEmpty("S-frames carry no information field");

        Ax25FrameClassifier.Classify(decoded).Should().BeOfType(expected);
    }

    /// <summary>
    /// The extended parse path (2-octet control branch) degrades gracefully on
    /// arbitrary input - same contract as the mod-8 <c>TryParse_Never_Throws</c>,
    /// but exercising the branch the caller takes for a SABME-negotiated link.
    /// </summary>
    [Property(MaxTest = 2_000)]
    public void Extended_TryParse_Never_Throws(byte[] bytes)
    {
        bytes ??= [];
        bool okLenient = Ax25Frame.TryParse(bytes, Ax25ParseOptions.Lenient, extended: true, out var a);
        bool okStrict = Ax25Frame.TryParse(bytes, Ax25ParseOptions.Strict, extended: true, out var b);
        if (okLenient)
        {
            a.Should().NotBeNull();
        }
        else
        {
            a.Should().BeNull();
        }

        if (okStrict)
        {
            b.Should().NotBeNull();
        }
        else
        {
            b.Should().BeNull();
        }
    }

    /// <summary>
    /// Anything the extended parser accepts must round-trip through
    /// <c>ToBytes</c> + parse-as-extended - the byte form is canonical for the
    /// value, control-extension and all.
    /// </summary>
    [Property(MaxTest = 1_000)]
    public void Extended_Parsed_Frame_Round_Trips_Through_ToBytes(byte[] bytes)
    {
        bytes ??= [];
        if (!Ax25Frame.TryParse(bytes, Ax25ParseOptions.Lenient, extended: true, out var first))
        {
            return;
        }

        var rewritten = first!.ToBytes();
        Ax25Frame.TryParse(rewritten, Ax25ParseOptions.Lenient, extended: true, out var second)
            .Should().BeTrue();

        second!.Destination.Should().Be(first.Destination);
        second.Source.Should().Be(first.Source);
        second.Digipeaters.Should().Equal(first.Digipeaters);
        second.Control.Should().Be(first.Control);
        second.ControlExtension.Should().Be(first.ControlExtension);
        second.Pid.Should().Be(first.Pid);
        second.Info.ToArray().Should().Equal(first.Info.ToArray());
    }

    /// <summary>
    /// U frames are 1 octet in both modes (Fig 4.1a/b), so the <c>extended</c>
    /// flag must not change how a U frame parses: a SABME/UA/DM/etc round-trips
    /// identically whether decoded as mod-8 or extended. Guards the
    /// "<c>extended</c> only affects I and S frames" contract.
    /// </summary>
    [Property(MaxTest = 200)]
    public void Extended_Flag_Does_Not_Change_U_Frame_Parse(
        NonEmptyString destBase, byte destSsidRaw,
        NonEmptyString srcBase, byte srcSsidRaw, bool pollBit)
    {
        var dest = SanitiseCallsign(destBase.Get, destSsidRaw);
        var src = SanitiseCallsign(srcBase.Get, srcSsidRaw);

        // SABME is the v2.2 connection U-frame - 1-octet control in both modes.
        var sabme = Ax25Frame.Sabme(dest, src, pollBit: pollBit);
        var bytes = sabme.ToBytes();

        Ax25Frame.TryParse(bytes, Ax25ParseOptions.Lenient, extended: false, out var asMod8).Should().BeTrue();
        Ax25Frame.TryParse(bytes, Ax25ParseOptions.Lenient, extended: true, out var asExt).Should().BeTrue();

        asMod8!.IsExtendedControl.Should().BeFalse("a U frame is 1-octet control even when the link is extended");
        asExt!.IsExtendedControl.Should().BeFalse();
        asExt.Control.Should().Be(asMod8.Control);
        asExt.RequiredBytes.Should().Be(asMod8.RequiredBytes);
        Ax25FrameClassifier.Classify(asExt).Should().BeOfType<SabmeReceived>();
        Ax25FrameClassifier.Classify(asMod8).Should().BeOfType<SabmeReceived>();
    }

    /// <summary>
    /// Map an arbitrary FsCheck string to a valid AX.25 callsign (1-6 chars,
    /// uppercase A-Z / 0-9). Same sanitiser as
    /// <see cref="Ax25FrameRoundTripProperties"/> - keeps shrinking on the
    /// control-field codec, not the address validator.
    /// </summary>
    private static Callsign SanitiseCallsign(string raw, byte ssidRaw)
    {
        var chars = raw.ToUpperInvariant()
            .Where(c => (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9'))
            .Take(6)
            .ToArray();
        if (chars.Length == 0)
        {
            chars = ['X'];
        }

        return new Callsign(new string(chars), (byte)(ssidRaw & 0x0F));
    }
}
