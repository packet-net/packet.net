using AwesomeAssertions;
using Packet.NetRom.Routing;
using Xunit;

namespace Packet.NetRom.Tests.Routing;

/// <summary>
/// Tests for the INP3 SNTT integer IIR smoother (<see cref="Inp3Sntt"/>). The
/// worked convergence trajectories are taken verbatim from
/// <c>docs/netrom-inp3-i2-design.md</c> §0.5 and become shared cross-stack golden
/// vectors: the same <c>(seed, sample…)</c> sequence must produce the same SNTT
/// trajectory in C#, <c>@packet-net/ax25</c> (TS), and pico-node (Rust). The C#
/// reference smoother is authoritative; TS and Rust mirror its integer arithmetic
/// 1:1 (no floating point anywhere - the shift-and-add form is exact and
/// language-agnostic, design §7).
/// </summary>
public sealed class Inp3SnttTests
{
    // ── first-sample seeding (§0.2) ───────────────────────────────────────

    [Fact]
    public void Fresh_is_uninitialised_and_reads_unset()
    {
        var s = Inp3Sntt.Fresh;
        s.Initialised.Should().BeFalse();
        s.Ms.Should().Be(Inp3Sntt.Unset);
        s.Value.Should().BeNull();
    }

    [Fact]
    public void First_sample_seeds_the_filter_directly_with_no_smoothing()
    {
        // The first valid sample seeds SNTT := sample (canonical SRT/Karn). Were it
        // smoothed against a 0 seed it would read ~25 ((7*0+200+4)/8), not 200.
        var s = Inp3Sntt.Fresh.Update(200);
        s.Initialised.Should().BeTrue();
        s.Ms.Should().Be(200);
        s.Value.Should().Be(200);
    }

    [Fact]
    public void First_sample_of_zero_seeds_a_real_zero_distinct_from_unset()
    {
        // A same-host loopback can legitimately measure ~0; the Unset sentinel must
        // be distinct from a genuine 0 ms measurement.
        var s = Inp3Sntt.Fresh.Update(0);
        s.Initialised.Should().BeTrue();
        s.Ms.Should().Be(0);
        s.Value.Should().Be(0u);
        s.Ms.Should().NotBe(Inp3Sntt.Unset);
    }

    [Fact]
    public void Seed_factory_is_equivalent_to_fresh_then_update()
    {
        Inp3Sntt.Seed(200).Should().Be(Inp3Sntt.Fresh.Update(200));
        Inp3Sntt.Seed(0).Should().Be(Inp3Sntt.Fresh.Update(0));
    }

    // ── worked convergence example A - steady link (§0.5) ─────────────────

    [Fact]
    public void Example_A_steady_link_sits_exactly_on_its_fixed_point()
    {
        // RTT steady at 400 ms ⇒ sample 200. Seed = 200; every subsequent
        // (7·200 + 200 + 4)/8 = 1604/8 = 200 - the +4 round-to-nearest keeps a
        // steady input pinned on its fixed point, no drift.
        var s = Inp3Sntt.Seed(200);
        s.Ms.Should().Be(200);
        for (int i = 0; i < 100; i++)
        {
            s = s.Update(200);
            s.Ms.Should().Be(200, "a steady sample reproduces itself exactly");
        }
    }

    [Fact]
    public void Example_B_step_up_then_settle_matches_the_design_trajectory()
    {
        // A link that got slower: RTT jumps 100 → 1000 ms (sample 50 → 500).
        // Design §0.5 table B: seed 50, then 50, 106, 155, 198, 236.
        var s = Inp3Sntt.Seed(50);          // step 1 (seed)
        s.Ms.Should().Be(50);

        s = s.Update(50);                   // step 2: (7·50+50+4)/8 = 404/8 = 50
        s.Ms.Should().Be(50);

        s = s.Update(500);                  // step 3: (7·50+500+4)/8 = 854/8 = 106
        s.Ms.Should().Be(106);

        s = s.Update(500);                  // step 4: (7·106+500+4)/8 = 1246/8 = 155
        s.Ms.Should().Be(155);

        s = s.Update(500);                  // step 5: (7·155+500+4)/8 = 1589/8 = 198
        s.Ms.Should().Be(198);

        s = s.Update(500);                  // step 6: (7·198+500+4)/8 = 1890/8 = 236
        s.Ms.Should().Be(236);
    }

    [Fact]
    public void Example_B_converges_toward_the_new_sample_over_many_probes()
    {
        // A sustained slowdown is fully reflected within a few probe intervals: a
        // 1/8-gain EWMA reaches ~95% of a step in ~24 samples. With integer rounding
        // the fixed-point region for sample 500 is [497, 500]; assert it converges to
        // within 3 ms of the new sample and never overshoots it.
        var s = Inp3Sntt.Seed(50).Update(50);   // steady at 50
        for (int i = 0; i < 60; i++)
        {
            s = s.Update(500);
            s.Ms.Should().BeLessThanOrEqualTo(500, "the filter approaches a step from below, never overshooting");
        }
        s.Ms.Should().BeInRange(497, 500, "a 1/8 IIR settles onto the integer fixed-point band of its input");
    }

    // ── worked convergence example C - outlier rejection (§0.5) ───────────

    [Fact]
    public void Example_C_single_outlier_is_damped_then_walked_back()
    {
        // Steady 200 ms RTT ⇒ sample 100, with one 2000 ms spike ⇒ sample 1000.
        // Design §0.5 table C: seed 100, then 100, 213, 199, 187, 176, 167.
        var s = Inp3Sntt.Seed(100);         // step 1 (seed)
        s.Ms.Should().Be(100);

        s = s.Update(100);                  // step 2: (7·100+100+4)/8 = 804/8 = 100
        s.Ms.Should().Be(100);

        s = s.Update(1000);                 // step 3 (spike): (7·100+1000+4)/8 = 1704/8 = 213
        s.Ms.Should().Be(213,
            "a lone 10× spike moves SNTT by only +113, not to 1000 — the outlier rejection the smoother exists for");

        s = s.Update(100);                  // step 4: (7·213+100+4)/8 = 1595/8 = 199
        s.Ms.Should().Be(199);

        s = s.Update(100);                  // step 5: (7·199+100+4)/8 = 1497/8 = 187
        s.Ms.Should().Be(187);

        s = s.Update(100);                  // step 6: (7·187+100+4)/8 = 1413/8 = 176
        s.Ms.Should().Be(176);

        s = s.Update(100);                  // step 7: (7·176+100+4)/8 = 1336/8 = 167
        s.Ms.Should().Be(167);
    }

    [Fact]
    public void Example_C_walks_back_into_the_band_of_the_true_value_after_a_spike()
    {
        // After the spike, the filter walks back to the true 100 within a handful of
        // probes. It rests in the integer rounding band [100, 104] rather than exactly
        // 100: descending from the spike it settles on the upper fixed point (104),
        // because the round-to-nearest +denom/2 term gives the integer IIR a small DC
        // bias (the same artifact AX.25 SRT carries). The point is the 10x outlier
        // leaves only a few ms of residue, not 100+.
        var s = Inp3Sntt.Seed(100).Update(100).Update(1000);   // post-spike = 213
        for (int i = 0; i < 100; i++)
        {
            s = s.Update(100);
        }
        s.Ms.Should().BeInRange(100u, 104u, "the outlier residue decays into the rounding band of the true input");
    }

    // ── monotonic-toward-sample (§0.1 one-pole low-pass) ──────────────────

    [Fact]
    public void Update_moves_strictly_toward_the_sample_when_above_current()
    {
        // A one-pole low-pass: SNTT' = SNTT + (sample - SNTT)/8. With sample > SNTT
        // the result is strictly between the old value and the sample (it moves
        // toward the sample, never past it).
        var s = Inp3Sntt.Seed(100);
        for (int i = 0; i < 30; i++)
        {
            uint before = s.Ms;
            s = s.Update(1000);
            s.Ms.Should().BeGreaterThan(before, "SNTT rises toward a larger sample");
            s.Ms.Should().BeLessThanOrEqualTo(1000u, "but never past the sample");
        }
    }

    [Fact]
    public void Update_moves_strictly_toward_the_sample_when_below_current()
    {
        var s = Inp3Sntt.Seed(1000);
        for (int i = 0; i < 30; i++)
        {
            uint before = s.Ms;
            s = s.Update(0);
            s.Ms.Should().BeLessThan(before, "SNTT falls monotonically toward a smaller sample");
            // floors at the sample band - never negative (it is unsigned).
        }
        // Run to the fixed point: a steady 0 sample settles in the integer rounding
        // band [0, denom/2] (~4 at the default 1/8 gain), not exactly 0 - the same
        // round-to-nearest +denom/2 DC bias as Example C. The invariant is convergence
        // into that band, not exact 0.
        for (int i = 0; i < 100; i++)
        {
            s = s.Update(0);
        }
        s.Ms.Should().BeInRange(0u, 4u, "a steady 0 sample converges into the rounding band, not exactly 0");
    }

    [Theory]
    [InlineData(0u, 1000u)]      // rise from a low seed
    [InlineData(1000u, 0u)]      // fall from a high seed
    [InlineData(50u, 500u)]      // example-B shape
    [InlineData(1000u, 100u)]    // example-C walk-back shape
    public void A_smoothed_value_always_lies_between_its_previous_value_and_the_sample(uint seed, uint sample)
    {
        var s = Inp3Sntt.Seed(seed);
        uint before = s.Ms;
        var after = s.Update(sample);
        uint lo = Math.Min(before, sample);
        uint hi = Math.Max(before, sample);
        after.Ms.Should().BeInRange(lo, hi,
            "the IIR is a convex combination of the previous value and the sample");
    }

    // ── overflow / range bounds (§0.3) ────────────────────────────────────

    [Fact]
    public void Sample_above_the_horizon_is_clamped_to_SampleMaxMs_on_seed()
    {
        Inp3Sntt.Seed(uint.MaxValue - 1).Ms.Should().Be(Inp3Sntt.SampleMaxMs);
        Inp3Sntt.Fresh.Update(700_000).Ms.Should().Be(Inp3Sntt.SampleMaxMs);
    }

    [Fact]
    public void Sample_above_the_horizon_is_clamped_before_smoothing()
    {
        // A wild sample is clamped to 600_000 before the IIR sees it, so it cannot
        // drive SNTT past the horizon.
        var s = Inp3Sntt.Seed(0);
        s = s.Update(uint.MaxValue);
        // (7·0 + 600000 + 4)/8 = 600004/8 = 75000 - the clamped sample, smoothed.
        s.Ms.Should().Be(75_000);
    }

    [Fact]
    public void Smoothed_value_never_exceeds_SampleMaxMs_even_under_max_input_storm()
    {
        // Pin SNTT at the top, then keep slamming max samples at the highest gain
        // (256): the convex-combination result can sit on the top but never above it,
        // and the int accumulator (worst case 255·600000 + 600000 + 128 ≈ 1.5e8)
        // never overflows.
        var s = Inp3Sntt.Seed(Inp3Sntt.SampleMaxMs);
        for (int i = 0; i < 100; i++)
        {
            s = s.Update(uint.MaxValue, Inp3Sntt.MaxGainShift);
            s.Ms.Should().BeLessThanOrEqualTo(Inp3Sntt.SampleMaxMs);
        }
        s.Ms.Should().Be(Inp3Sntt.SampleMaxMs, "max sample at the top stays at the top");
    }

    [Theory]
    [InlineData(1)]   // gain 1/2 (denom 2): worst acc = 1·600000 + 600000 + 1
    [InlineData(3)]   // gain 1/8 (default): worst acc = 7·600000 + 600000 + 4
    [InlineData(8)]   // gain 1/256: worst acc = 255·600000 + 600000 + 128 ≈ 1.5e8
    public void All_valid_gains_keep_a_max_x_max_update_within_range(int gainShift)
    {
        var s = Inp3Sntt.Seed(Inp3Sntt.SampleMaxMs).Update(Inp3Sntt.SampleMaxMs, gainShift);
        s.Ms.Should().BeInRange(0u, Inp3Sntt.SampleMaxMs);
        s.Ms.Should().Be(Inp3Sntt.SampleMaxMs);
    }

    // ── configurable gain (§0.4) ──────────────────────────────────────────

    [Fact]
    public void Default_update_uses_the_default_gain_shift()
    {
        Inp3Sntt.Seed(50).Update(500)
            .Should().Be(Inp3Sntt.Seed(50).Update(500, Inp3Sntt.DefaultGainShift));
    }

    [Fact]
    public void A_smaller_gain_shift_is_twitchier_a_larger_one_is_more_sluggish()
    {
        // After one step from 100 toward 1000, a higher gain (1/2, shift 1) moves
        // further than the default (1/8, shift 3), which moves further than a low
        // gain (1/256, shift 8). gain = 1/(1<<shift): smaller shift ⇒ larger gain.
        uint twitchy = Inp3Sntt.Seed(100).Update(1000, 1).Ms;   // gain 1/2
        uint mid = Inp3Sntt.Seed(100).Update(1000, 3).Ms;       // gain 1/8 (default)
        uint sluggish = Inp3Sntt.Seed(100).Update(1000, 8).Ms;  // gain 1/256

        twitchy.Should().BeGreaterThan(mid);
        mid.Should().BeGreaterThan(sluggish);

        // Exact integer checks of the shift form, sample - seed = 900:
        //   shift 1: (1·100 + 1000 + 1) >> 1 = 1101 >> 1 = 550
        //   shift 3: (7·100 + 1000 + 4) >> 3 = 1704 >> 3 = 213
        //   shift 8: (255·100 + 1000 + 128) >> 8 = 26628 >> 8 = 104
        twitchy.Should().Be(550);
        mid.Should().Be(213);
        sluggish.Should().Be(104);
    }

    [Theory]
    [InlineData(0)]                          // gain 1 = no smoothing (pointless)
    [InlineData(-1)]
    [InlineData(9)]                          // gain 1/512 = sluggish past usefulness
    [InlineData(int.MaxValue)]
    public void An_out_of_range_gain_shift_is_rejected(int gainShift)
    {
        var act = () => Inp3Sntt.Seed(100).Update(200, gainShift);
        act.Should().Throw<ArgumentOutOfRangeException>().WithParameterName(nameof(gainShift));
    }

    [Theory]
    [InlineData(Inp3Sntt.MinGainShift)]
    [InlineData(Inp3Sntt.DefaultGainShift)]
    [InlineData(Inp3Sntt.MaxGainShift)]
    public void Every_in_range_gain_shift_is_accepted(int gainShift)
    {
        var act = () => Inp3Sntt.Seed(100).Update(200, gainShift);
        act.Should().NotThrow();
    }

    [Fact]
    public void Gain_shift_only_applies_after_seeding_the_first_sample_still_seeds_directly()
    {
        // The first sample seeds regardless of gain (no smoothing on sample #1).
        Inp3Sntt.Fresh.Update(321, Inp3Sntt.MaxGainShift).Ms.Should().Be(321);
        Inp3Sntt.Fresh.Update(321, Inp3Sntt.MinGainShift).Ms.Should().Be(321);
    }

    // ── value-type semantics ──────────────────────────────────────────────

    [Fact]
    public void Update_is_pure_it_does_not_mutate_the_source()
    {
        var original = Inp3Sntt.Seed(100);
        var updated = original.Update(1000);
        original.Ms.Should().Be(100, "the struct is immutable; Update returns a new value");
        updated.Ms.Should().NotBe(100);
    }

    [Fact]
    public void Equality_is_by_value()
    {
        Inp3Sntt.Seed(200).Should().Be(Inp3Sntt.Seed(200));
        (Inp3Sntt.Seed(200) == Inp3Sntt.Seed(200)).Should().BeTrue();
        (Inp3Sntt.Seed(200) != Inp3Sntt.Seed(201)).Should().BeTrue();
        Inp3Sntt.Fresh.Should().Be(Inp3Sntt.Fresh);
        Inp3Sntt.Fresh.Should().NotBe(Inp3Sntt.Seed(0));
    }
}
