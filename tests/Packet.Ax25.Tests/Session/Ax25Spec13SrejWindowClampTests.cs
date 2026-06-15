using AwesomeAssertions;
using Packet.Ax25.Session;
using Packet.Core;

namespace Packet.Ax25.Tests.Session;

/// <summary>
/// The <see cref="Ax25SessionQuirks.Ax25Spec13ClampSrejWindowToHalfModulus"/>
/// quirk (packethacking/ax25spec#13). Selective Repeat (SREJ) requires the send
/// window k ≤ modulus/2 — the 2·W ≤ modulus bound — because recovery state is
/// keyed by the bare N(S). Above the cap, two in-flight frames can share an N(S)
/// and SREJ recovery silently delivers a stale stored I-frame from the previous
/// ring cycle (packet-net/packet.net#393, found by tools/Packet.LinkBench: corruption
/// at mod-8 k≥5, clean at k≤4). With the quirk on (default) the engine's
/// <see cref="Ax25SessionContext.EffectiveWindow"/> caps the outstanding-frame
/// window at modulus/2 while SREJ is in effect; with it off the figure-literal
/// uncapped k applies.
/// </summary>
public class Ax25Spec13SrejWindowClampTests
{
    private static Ax25SessionContext Ctx(int k, bool srej, bool extended, Ax25SessionQuirks quirks) => new()
    {
        Local = new Callsign("M0LTE", 0),
        Remote = new Callsign("G7XYZ", 7),
        Quirks = quirks,
        IsExtended = extended,
        K = k,
        SrejEnabled = srej,
    };

    [Theory]
    // mod-8 (modulus/2 = 4): SREJ caps above 4, leaves ≤4 alone.
    [InlineData(7, true, false, 4)]
    [InlineData(5, true, false, 4)]
    [InlineData(4, true, false, 4)]
    [InlineData(3, true, false, 3)]
    // mod-128 (modulus/2 = 64).
    [InlineData(100, true, true, 64)]
    [InlineData(32, true, true, 32)]
    // SREJ off (go-back-N) is never capped — k up to modulus-1 is legitimate.
    [InlineData(7, false, false, 7)]
    [InlineData(100, false, true, 100)]
    public void Effective_send_window_is_capped_at_half_modulus_only_under_srej(
        int k, bool srej, bool extended, int expected)
    {
        Ctx(k, srej, extended, Ax25SessionQuirks.Default).EffectiveWindow.Should().Be(expected);
    }

    [Fact]
    public void StrictlyFaithful_leaves_the_window_uncapped_reproducing_the_unsafe_figure_behaviour()
    {
        // SREJ + k=7 on mod-8 — the corrupting configuration — runs uncapped as drawn.
        Ctx(7, srej: true, extended: false, Ax25SessionQuirks.StrictlyFaithful)
            .EffectiveWindow.Should().Be(7);
    }

    [Fact]
    public void The_default_mod8_window_is_unchanged_by_the_clamp()
    {
        // k=4 = modulus/2: at the safe limit, so the default is untouched whether
        // SREJ is on or off — no behaviour change for the common case.
        Ctx(4, srej: true, extended: false, Ax25SessionQuirks.Default).EffectiveWindow.Should().Be(4);
        Ctx(4, srej: false, extended: false, Ax25SessionQuirks.Default).EffectiveWindow.Should().Be(4);
    }
}
