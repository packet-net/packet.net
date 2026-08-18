using System.Net;
using AwesomeAssertions;
using Packet.Core;
using Packet.NetRom.Wire;
using Xunit;

namespace Packet.NetRom.Tests.Wire;

/// <summary>
/// Vectors and totality tests for the INP3 RIF / RIP / TLV wire codec
/// (<see cref="Inp3Rif"/> / <see cref="Inp3Rip"/> / <see cref="Inp3Tlv"/>),
/// against the locked byte layouts in
/// <c>docs/netrom-inp3-i1-wire-spec.md</c> §2.5-2.6. Every hex vector in the spec
/// is asserted here, including the unknown-TLV-retained, alias/EOP, and
/// horizon/withdrawal cases, plus round-trip and the totality (never-throw)
/// contract on garbage and truncation.
/// </summary>
public class Inp3RifTests
{
    private static readonly Callsign Gb7Rdg0 = new("GB7RDG", 0);
    private static readonly Callsign Gb7Rdg7 = new("GB7RDG", 7);
    private static readonly Callsign M0lte0 = new("M0LTE", 0);
    private static readonly Callsign Gb7Xyz0 = new("GB7XYZ", 0);

    private static byte[] Hex(string hex) =>
        hex.Split([' ', '\n', '\r', '\t'], StringSplitOptions.RemoveEmptyEntries)
           .Select(b => Convert.ToByte(b, 16))
           .ToArray();

    // ─── Shifted-callsign sanity (the spec's stated shifted forms) ───

    [Theory]
    [InlineData("GB7RDG", (byte)0, "8E 84 6E A4 88 8E 60")]
    [InlineData("GB7RDG", (byte)7, "8E 84 6E A4 88 8E 6E")]
    [InlineData("M0LTE", (byte)0, "9A 60 98 A8 8A 40 60")]
    [InlineData("GB7XYZ", (byte)0, "8E 84 6E B0 B2 B4 60")]
    public void Shifted_callsign_matches_the_spec_vectors(string @base, byte ssid, string expectedHex)
    {
        var buf = new byte[7];
        NetRomCallsign.WriteShifted(new Callsign(@base, ssid), buf);
        buf.Should().Equal(Hex(expectedHex));
    }

    // ─── RIP single-entry vectors (§2.5) ───

    [Fact]
    public void RIP1_alias_tlv_parses_and_round_trips()
    {
        // 8E 84 6E A4 88 8E 60  02  00 2D  00 03 52 44 47  00
        var bytes = Hex("8E 84 6E A4 88 8E 60 02 00 2D 00 03 52 44 47 00");
        bytes.Length.Should().Be(16);

        Inp3Rip.TryParse(bytes, out var rip, out int consumed).Should().BeTrue();
        consumed.Should().Be(16);
        rip!.Destination.Should().Be(Gb7Rdg0);
        rip.HopCount.Should().Be((byte)2);
        rip.TargetTimeMs.Should().Be(450);
        rip.IsHorizon.Should().BeFalse();
        rip.Tlvs.Should().ContainSingle();
        rip.Tlvs[0].Type.Should().Be(Inp3Tlv.AliasType);
        rip.Tlvs[0].AsAlias().Should().Be("RDG");
        rip.Alias.Should().Be("RDG");

        rip.ToBytes().Should().Equal(bytes);
    }

    [Fact]
    public void RIP2_ip_tlv_parses_and_round_trips()
    {
        // 9A 60 98 A8 8A 40 60  01  00 0C  01 04 2C 83 5B 02  00   (44.131.91.2)
        var bytes = Hex("9A 60 98 A8 8A 40 60 01 00 0C 01 04 2C 83 5B 02 00");
        bytes.Length.Should().Be(17);

        Inp3Rip.TryParse(bytes, out var rip, out int consumed).Should().BeTrue();
        consumed.Should().Be(17);
        rip!.Destination.Should().Be(M0lte0);
        rip.HopCount.Should().Be((byte)1);
        rip.TargetTimeMs.Should().Be(120);
        rip.Tlvs.Should().ContainSingle();
        rip.Tlvs[0].Type.Should().Be(Inp3Tlv.IpType);
        rip.Tlvs[0].AsIpAddress().Should().Be(IPAddress.Parse("44.131.91.2"));

        rip.ToBytes().Should().Equal(bytes);
    }

    [Fact]
    public void RIP3_unknown_tlv_is_retained_verbatim_and_re_emitted()
    {
        // 8E 84 6E B0 B2 B4 60  04  00 FA  7F 02 AA BB  00 03 58 59 5A  00
        var bytes = Hex("8E 84 6E B0 B2 B4 60 04 00 FA 7F 02 AA BB 00 03 58 59 5A 00");
        bytes.Length.Should().Be(20);

        Inp3Rip.TryParse(bytes, out var rip, out int consumed).Should().BeTrue();
        consumed.Should().Be(20);
        rip!.Destination.Should().Be(Gb7Xyz0);
        rip.HopCount.Should().Be((byte)4);
        rip.TargetTimeMs.Should().Be(2500);

        rip.Tlvs.Should().HaveCount(2);

        // The 0x7F TLV is unknown → retained verbatim, flagged not-known.
        var unknown = rip.Tlvs[0];
        unknown.Type.Should().Be((byte)0x7F);
        unknown.IsKnown.Should().BeFalse();
        unknown.Value.ToArray().Should().Equal(0xAA, 0xBB);

        // The alias TLV after the unknown one still decodes.
        rip.Tlvs[1].Type.Should().Be(Inp3Tlv.AliasType);
        rip.Tlvs[1].IsKnown.Should().BeTrue();
        rip.Alias.Should().Be("XYZ");

        // Re-emission keeps the unknown TLV byte-for-byte.
        rip.ToBytes().Should().Equal(bytes);
    }

    [Fact]
    public void RIP4_horizon_withdrawal_has_no_tlv_and_flags_horizon()
    {
        // 8E 84 6E A4 88 8E 6E  FF  EA 60  00
        var bytes = Hex("8E 84 6E A4 88 8E 6E FF EA 60 00");
        bytes.Length.Should().Be(11);

        Inp3Rip.TryParse(bytes, out var rip, out int consumed).Should().BeTrue();
        consumed.Should().Be(11);
        rip!.Destination.Should().Be(Gb7Rdg7);
        rip.HopCount.Should().Be((byte)0xFF);
        rip.TargetTimeMs.Should().Be(Inp3Rip.HorizonMs);
        rip.TargetTimeMs.Should().Be(600_000);
        rip.IsHorizon.Should().BeTrue();
        rip.Tlvs.Should().BeEmpty();
        rip.Alias.Should().BeNull();

        rip.ToBytes().Should().Equal(bytes);
    }

    // ─── RIF body vectors (§2.5) ───

    [Fact]
    public void RIF_FULL_parses_all_four_rips_in_order()
    {
        var bytes = Hex(
            "FF " +
            "8E 84 6E A4 88 8E 60 02 00 2D 00 03 52 44 47 00 " +                 // RIP-1
            "9A 60 98 A8 8A 40 60 01 00 0C 01 04 2C 83 5B 02 00 " +              // RIP-2
            "8E 84 6E B0 B2 B4 60 04 00 FA 7F 02 AA BB 00 03 58 59 5A 00 " +     // RIP-3
            "8E 84 6E A4 88 8E 6E FF EA 60 00");                                 // RIP-4
        bytes.Length.Should().Be(65);   // 1 + 16 + 17 + 20 + 11

        Inp3Rif.TryParse(bytes, Inp3ParseOptions.Strict, out var rif).Should().BeTrue();
        rif!.Rips.Should().HaveCount(4);

        rif.Rips.Select(r => r.Destination).Should().Equal(Gb7Rdg0, M0lte0, Gb7Xyz0, Gb7Rdg7);
        rif.Rips.Select(r => r.HopCount).Should().Equal((byte)2, (byte)1, (byte)4, (byte)0xFF);
        rif.Rips.Select(r => r.TargetTimeMs).Should().Equal(450, 120, 2500, 600_000);

        rif.Rips[0].Alias.Should().Be("RDG");
        rif.Rips[1].Tlvs[0].AsIpAddress().Should().Be(IPAddress.Parse("44.131.91.2"));
        rif.Rips[2].Tlvs[0].Type.Should().Be((byte)0x7F);   // unknown retained
        rif.Rips[2].Alias.Should().Be("XYZ");
        rif.Rips[3].IsHorizon.Should().BeTrue();

        // Round-trip the whole frame.
        rif.ToBytes().Should().Equal(bytes);
    }

    [Fact]
    public void RIF_MIN_signature_plus_one_no_tlv_rip()
    {
        // FF  9A 60 98 A8 8A 40 60  01  00 7B  00
        var bytes = Hex("FF 9A 60 98 A8 8A 40 60 01 00 7B 00");
        bytes.Length.Should().Be(12);

        Inp3Rif.TryParse(bytes, Inp3ParseOptions.Strict, out var rif).Should().BeTrue();
        rif!.Rips.Should().ContainSingle();
        var rip = rif.Rips[0];
        rip.Destination.Should().Be(M0lte0);
        rip.HopCount.Should().Be((byte)1);
        rip.TargetTimeMs.Should().Be(1230);   // 0x7B = 123 units × 10 ms
        rip.Tlvs.Should().BeEmpty();

        rif.ToBytes().Should().Equal(bytes);
    }

    // ─── Builder-side round-trip (parser is the oracle) ───

    [Fact]
    public void Built_rif_round_trips_through_the_parser()
    {
        var rif = new Inp3Rif
        {
            Rips =
            [
                new Inp3Rip
                {
                    Destination = Gb7Rdg0,
                    HopCount = 2,
                    TargetTimeMs = 450,
                    Tlvs = [Inp3Tlv.Alias("RDG")],
                },
                new Inp3Rip
                {
                    Destination = M0lte0,
                    HopCount = 1,
                    TargetTimeMs = 120,
                    Tlvs = [Inp3Tlv.Ip(IPAddress.Parse("44.131.91.2"))],
                },
                new Inp3Rip
                {
                    Destination = Gb7Rdg7,
                    HopCount = 0xFF,
                    TargetTimeMs = Inp3Rip.HorizonMs,
                    Tlvs = [],
                },
            ],
        };

        var bytes = rif.ToBytes();

        Inp3Rif.TryParse(bytes, Inp3ParseOptions.Strict, out var parsed).Should().BeTrue();
        parsed!.Rips.Should().HaveCount(3);
        parsed.Rips[0].Alias.Should().Be("RDG");
        parsed.Rips[1].Tlvs[0].AsIpAddress().Should().Be(IPAddress.Parse("44.131.91.2"));
        parsed.Rips[2].IsHorizon.Should().BeTrue();
        parsed.ToBytes().Should().Equal(bytes);
    }

    [Fact]
    public void Ipv6_tlv_round_trips()
    {
        var v6 = IPAddress.Parse("2001:db8::1");
        var rip = new Inp3Rip
        {
            Destination = M0lte0,
            HopCount = 1,
            TargetTimeMs = 100,
            Tlvs = [Inp3Tlv.Ip(v6)],
        };

        var bytes = rip.ToBytes();
        Inp3Rip.TryParse(bytes, out var parsed, out _).Should().BeTrue();
        parsed!.Tlvs[0].Value.Length.Should().Be(16);
        parsed.Tlvs[0].AsIpAddress().Should().Be(v6);
    }

    // ─── Empty-list preset gating (§2.6, mirrors NODES) ───

    [Fact]
    public void Signature_only_rif_is_rejected_by_strict_but_accepted_by_lenient()
    {
        var bytes = Hex("FF");   // signature, zero RIPs

        Inp3Rif.TryParse(bytes, Inp3ParseOptions.Strict, out _).Should().BeFalse();

        Inp3Rif.TryParse(bytes, Inp3ParseOptions.Lenient, out var lenient).Should().BeTrue();
        lenient!.Rips.Should().BeEmpty();
    }

    [Fact]
    public void Bpq_and_xrouter_presets_accept_signature_only_like_lenient()
    {
        var bytes = Hex("FF");
        Inp3Rif.TryParse(bytes, Inp3ParseOptions.Bpq, out var bpq).Should().BeTrue();
        bpq!.Rips.Should().BeEmpty();
        Inp3Rif.TryParse(bytes, Inp3ParseOptions.Xrouter, out var xr).Should().BeTrue();
        xr!.Rips.Should().BeEmpty();
    }

    // ─── Trailing-partial RIP gating (§2.6) ───

    [Fact]
    public void Rip_truncated_mid_target_time_is_rejected_by_strict_dropped_by_lenient()
    {
        // FF + a clean RIP-MIN body, then a second RIP clipped after 2 octets of its prefix.
        var clean = Hex("FF 9A 60 98 A8 8A 40 60 01 00 7B 00");
        var clipped = clean.Concat(Hex("8E 84 6E A4 88 8E 60 02 00")).ToArray();   // partial RIP-2

        // Strict: the leftover that doesn't complete a RIP rejects the whole frame.
        Inp3Rif.TryParse(clipped, Inp3ParseOptions.Strict, out _).Should().BeFalse();

        // Lenient: keep the whole RIP parsed, drop the clipped tail.
        Inp3Rif.TryParse(clipped, Inp3ParseOptions.Lenient, out var lenient).Should().BeTrue();
        lenient!.Rips.Should().ContainSingle();
        lenient.Rips[0].Destination.Should().Be(M0lte0);
    }

    [Fact]
    public void Truncated_trailing_alias_tlv_degrades_to_eop_keeping_the_route()
    {
        // FF + a RIP whose trailing bytes look like an alias TLV (00 03 ...) but claim
        // more value bytes than remain (len=3, only "RD" present).
        var bytes = Hex("FF 8E 84 6E A4 88 8E 60 02 00 2D 00 03 52 44");

        // The alias TLV type (0x00) is identical to the EOP byte (AMBIGUITY-RIF-2),
        // so a 0x00 that cannot be satisfied as a TLV is *necessarily* read as the
        // EOP - this is the same rule that lets a multi-RIP RIF find its boundaries
        // (a real EOP is followed by the next RIP's shifted callsign, whose first
        // octet never frames as a short alias TLV within the remaining body). The RIP
        // therefore keeps its routing fields (450 ms) and simply drops the malformed
        // trailing alias; the leftover bytes are a trailing partial. (A truncated
        // alias can NOT be soundly distinguished from EOP-plus-partial here - that's
        // the documented residual flagged for I-5 interop validation.)

        // Strict: the leftover (03 52 44) is an un-frameable trailing partial → reject.
        Inp3Rif.TryParse(bytes, Inp3ParseOptions.Strict, out _).Should().BeFalse();

        // Lenient: the leftover partial is dropped; the one whole RIP survives, sans alias.
        Inp3Rif.TryParse(bytes, Inp3ParseOptions.Lenient, out var lenient).Should().BeTrue();
        lenient!.Rips.Should().ContainSingle();
        lenient.Rips[0].TargetTimeMs.Should().Be(450);
        lenient.Rips[0].Alias.Should().BeNull("the malformed trailing alias was read as EOP and dropped");
    }

    [Fact]
    public void A_target_time_above_the_horizon_is_flagged_unreachable()
    {
        // Max encodable target time 0xFFFF = 655350 ms - above the 600 000 ms horizon,
        // so still a withdrawal. (RIP-4 covers exactly-horizon; this covers above it.)
        var bytes = Hex("FF 9A 60 98 A8 8A 40 60 01 FF FF 00");
        Inp3Rif.TryParse(bytes, out var rif).Should().BeTrue();
        rif!.Rips.Should().ContainSingle();
        rif.Rips[0].TargetTimeMs.Should().Be(655350);
        rif.Rips[0].IsHorizon.Should().BeTrue("any target time at/above 600 s is unreachable");
    }

    // ─── Wrong / missing signature (§2.6) ───

    [Fact]
    public void Empty_input_returns_null()
    {
        Inp3Rif.TryParse([], out var rif).Should().BeFalse();
        rif.Should().BeNull();
    }

    [Fact]
    public void Wrong_signature_returns_null()
    {
        // Same bytes as RIF-MIN but signature 0x00 instead of 0xFF.
        var bytes = Hex("00 9A 60 98 A8 8A 40 60 01 00 7B 00");
        Inp3Rif.TryParse(bytes, out var rif).Should().BeFalse();
        rif.Should().BeNull();
    }

    [Fact]
    public void Rip_with_bad_callsign_field_fails_to_parse()
    {
        // A 7-octet callsign slot with a non-space byte after a space pad does not
        // decode (NetRomCallsign.TryReadShifted → false). Build a callsign whose
        // shifted form is invalid: 0x00 chars are not A-Z/0-9 once unshifted.
        var bytes = new byte[Inp3Rip.PrefixLength + 1];   // garbage prefix + a byte
        // leave it all-zero except the EOP slot won't be reached - callsign decode fails first.
        Inp3Rip.TryParse(bytes, out var rip, out int consumed).Should().BeFalse();
        rip.Should().BeNull();
        consumed.Should().Be(0);
    }

    // ─── Totality: arbitrary / truncated bytes never throw (§0 contract) ───

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(10)]
    [InlineData(11)]
    [InlineData(15)]
    [InlineData(64)]
    public void Short_or_truncated_input_never_throws(int length)
    {
        var bytes = new byte[length];
        if (length > 0)
        {
            bytes[0] = Inp3Rif.Signature;
        }

        var act = () =>
        {
            Inp3Rif.TryParse(bytes, out _);
            Inp3Rif.TryParse(bytes, Inp3ParseOptions.Strict, out _);
        };
        act.Should().NotThrow();
    }

    [Fact]
    public void Truncations_of_every_full_rif_prefix_never_throw_and_never_over_read()
    {
        var full = Hex(
            "FF 8E 84 6E A4 88 8E 60 02 00 2D 00 03 52 44 47 00 " +
            "9A 60 98 A8 8A 40 60 01 00 0C 01 04 2C 83 5B 02 00 " +
            "8E 84 6E B0 B2 B4 60 04 00 FA 7F 02 AA BB 00 03 58 59 5A 00 " +
            "8E 84 6E A4 88 8E 6E FF EA 60 00");

        for (int n = 0; n <= full.Length; n++)
        {
            var prefix = full.AsSpan(0, n).ToArray();
            var actLenient = () => Inp3Rif.TryParse(prefix, Inp3ParseOptions.Lenient, out _);
            var actStrict = () => Inp3Rif.TryParse(prefix, Inp3ParseOptions.Strict, out _);
            actLenient.Should().NotThrow($"lenient parse of {n}-byte prefix must be total");
            actStrict.Should().NotThrow($"strict parse of {n}-byte prefix must be total");
        }
    }

    [Fact]
    public void Random_garbage_never_throws()
    {
        var rng = new Random(20260607);
        for (int i = 0; i < 2000; i++)
        {
            var bytes = new byte[rng.Next(0, 400)];
            rng.NextBytes(bytes);

            var act = () =>
            {
                Inp3Rif.TryParse(bytes, Inp3ParseOptions.Lenient, out _);
                Inp3Rif.TryParse(bytes, Inp3ParseOptions.Strict, out _);
                Inp3Rip.TryParse(bytes, out _, out _);
            };
            act.Should().NotThrow();
        }
    }

    [Fact]
    public void Random_signature_prefixed_garbage_never_throws()
    {
        // Bias toward 0xFF-signed bodies so the RIP walker is exercised on junk.
        var rng = new Random(424242);
        for (int i = 0; i < 2000; i++)
        {
            var bytes = new byte[rng.Next(1, 200)];
            rng.NextBytes(bytes);
            bytes[0] = Inp3Rif.Signature;

            var act = () =>
            {
                Inp3Rif.TryParse(bytes, Inp3ParseOptions.Lenient, out _);
                Inp3Rif.TryParse(bytes, Inp3ParseOptions.Strict, out _);
            };
            act.Should().NotThrow();
        }
    }
}
