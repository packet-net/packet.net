using AwesomeAssertions;
using Packet.Ax25.Session;
using Packet.Ax25.Xid;
using Xunit;

namespace Packet.Ax25.Tests.Session;

/// <summary>
/// Unit tests for the §6.3.2 reverts-to merge and the §1436 version-2.0 default
/// set (<see cref="XidNegotiator"/>) - the substantive logic of the MDL "Apply
/// Negotiated Parameters" placeholder, pinned per parameter without the harness.
/// </summary>
public class XidNegotiatorTests
{
    private static Ax25SessionContext NewContext() => new()
    {
        Local = new Core.Callsign("M0AAA"),
        Remote = new Core.Callsign("M0BBB"),
    };

    // ─── HDLC Optional Functions: lesser of reject + modulo (§6.3.2 ¶1426) ──

    [Theory]
    // ours, theirs, expected agreed selective-reject
    [InlineData(true, true, true)]   // both SREJ → SREJ
    [InlineData(true, false, false)]  // one REJ   → REJ (lesser)
    [InlineData(false, true, false)]  // one REJ   → REJ (lesser)
    [InlineData(false, false, false)]  // both REJ  → REJ
    public void Reject_scheme_is_the_lesser_of_the_two_offers(bool oursSrej, bool theirsSrej, bool expectSrej)
    {
        var ctx = NewContext();
        var offered = new XidParameters
        {
            HdlcOptionalFunctions = new HdlcOptionalFunctions
            {
                Reject = oursSrej ? RejectMode.SelectiveReject : RejectMode.ImplicitReject,
                Modulo128 = true,
            },
        };
        var response = new XidParameters
        {
            HdlcOptionalFunctions = new HdlcOptionalFunctions
            {
                Reject = theirsSrej ? RejectMode.SelectiveReject : RejectMode.ImplicitReject,
                Modulo128 = true,
            },
        };

        XidNegotiator.ApplyNegotiated(ctx, offered, response);

        ctx.SrejEnabled.Should().Be(expectSrej);
        ctx.ImplicitReject.Should().Be(!expectSrej);
    }

    [Theory]
    [InlineData(true, true, true)]   // both mod-128 → mod-128
    [InlineData(true, false, false)]  // one mod-8    → mod-8 (lesser)
    [InlineData(false, true, false)]
    [InlineData(false, false, false)]
    public void Modulo_is_the_lesser_of_the_two_offers(bool oursMod128, bool theirsMod128, bool expectMod128)
    {
        var ctx = NewContext();
        var offered = new XidParameters
        {
            HdlcOptionalFunctions = new HdlcOptionalFunctions { Modulo128 = oursMod128 },
        };
        var response = new XidParameters
        {
            HdlcOptionalFunctions = new HdlcOptionalFunctions { Modulo128 = theirsMod128 },
        };

        XidNegotiator.ApplyNegotiated(ctx, offered, response);

        ctx.IsExtended.Should().Be(expectMod128);
    }

    [Fact]
    public void Segmenter_enabled_only_when_both_sides_advertise_it()
    {
        var ctx = NewContext();
        var bothOn = new XidParameters
        {
            HdlcOptionalFunctions = new HdlcOptionalFunctions { SegmenterReassembler = true },
        };
        XidNegotiator.ApplyNegotiated(ctx, bothOn, bothOn);
        ctx.SegmenterReassemblerEnabled.Should().BeTrue();

        var ctx2 = NewContext();
        var oneOff = new XidParameters
        {
            HdlcOptionalFunctions = new HdlcOptionalFunctions { SegmenterReassembler = false },
        };
        XidNegotiator.ApplyNegotiated(ctx2, bothOn, oneOff);
        ctx2.SegmenterReassemblerEnabled.Should().BeFalse();
    }

    // ─── Window k + N1: notification/min (§6.3.2 ¶1430 / ¶1428) ─────────────

    [Fact]
    public void Window_k_is_the_min_of_the_two_advertised()
    {
        var ctx = NewContext();
        XidNegotiator.ApplyNegotiated(ctx,
            new XidParameters { WindowSizeRx = 32 },
            new XidParameters { WindowSizeRx = 10 });
        ctx.K.Should().Be(10);
    }

    [Fact]
    public void N1_is_the_min_of_the_two_advertised_octet_lengths()
    {
        var ctx = NewContext();
        XidNegotiator.ApplyNegotiated(ctx,
            new XidParameters { IFieldLengthRxBits = XidParameters.OctetsToBits(256) },
            new XidParameters { IFieldLengthRxBits = XidParameters.OctetsToBits(128) });
        ctx.N1.Should().Be(128, "N1 reverts to the min (the peer's smaller Rx capacity)");
    }

    // ─── T1 + N2: greater (§6.3.2 ¶1432 / ¶1434) ────────────────────────────

    [Fact]
    public void T1_is_the_greater_of_the_two_offers()
    {
        var ctx = NewContext();
        XidNegotiator.ApplyNegotiated(ctx,
            new XidParameters { AckTimerMillis = 1000 },
            new XidParameters { AckTimerMillis = 4000 });
        ctx.T1V.Should().Be(System.TimeSpan.FromMilliseconds(4000));
    }

    [Fact]
    public void N2_is_the_greater_of_the_two_offers()
    {
        var ctx = NewContext();
        XidNegotiator.ApplyNegotiated(ctx,
            new XidParameters { Retries = 8 },
            new XidParameters { Retries = 20 });
        ctx.N2.Should().Be(20);
    }

    // ─── Absent fields retain current values (§4.3.3.7 ¶1024) ───────────────

    [Fact]
    public void Absent_notification_fields_retain_the_current_context_values()
    {
        var ctx = NewContext();
        ctx.K = 5;
        ctx.N1 = 200;
        ctx.N2 = 7;
        ctx.T1V = System.TimeSpan.FromMilliseconds(1234);

        // Neither side offers k / N1 / T1 / N2 (only HDLC Optional Functions).
        var offered = new XidParameters { HdlcOptionalFunctions = HdlcOptionalFunctions.Default };
        XidNegotiator.ApplyNegotiated(ctx, offered, offered);

        ctx.K.Should().Be(5, "k absent from both offers ⇒ retain current");
        ctx.N1.Should().Be(200);
        ctx.N2.Should().Be(7);
        ctx.T1V.Should().Be(System.TimeSpan.FromMilliseconds(1234));
    }

    [Fact]
    public void Absent_HDLC_optional_functions_selects_the_v22_defaults()
    {
        // §6.3.2 ¶1426: if PI=3 absent from both, default SREJ + mod-128.
        var ctx = NewContext();
        var empty = new XidParameters();
        XidNegotiator.ApplyNegotiated(ctx, empty, empty);
        ctx.SrejEnabled.Should().BeTrue("default selective reject");
        ctx.IsExtended.Should().BeTrue("default modulo 128");
    }

    // ─── §1436 full version-2.0 default set ─────────────────────────────────

    [Fact]
    public void Version20_defaults_install_the_complete_1436_set()
    {
        var ctx = NewContext();
        // Pre-load with v2.2-ish values so we can see them all replaced.
        ctx.IsExtended = true;
        ctx.SrejEnabled = true;
        ctx.SegmenterReassemblerEnabled = true;
        ctx.K = 32;
        ctx.N1 = 512;
        ctx.N2 = 20;
        ctx.HalfDuplex = false;
        ctx.T1V = System.TimeSpan.FromMilliseconds(500);

        XidNegotiator.ApplyVersion20Defaults(ctx);

        ctx.HalfDuplex.Should().BeTrue("Set Half Duplex");
        ctx.ImplicitReject.Should().BeTrue("Set Implicit Reject");
        ctx.SrejEnabled.Should().BeFalse();
        ctx.IsExtended.Should().BeFalse("Modulo = 8");
        ctx.N1.Should().Be(256, "I Field Length Receive = 2048 bits = 256 octets");
        ctx.K.Should().Be(7, "Window Size Receive = 7");
        ctx.T1V.Should().Be(System.TimeSpan.FromMilliseconds(3000), "Acknowledge Timer = 3000 ms");
        ctx.N2.Should().Be(10, "Retries = 10");
        ctx.SegmenterReassemblerEnabled.Should().BeFalse("v2.2-only capability disabled on the v2.0 fallback");
    }
}
