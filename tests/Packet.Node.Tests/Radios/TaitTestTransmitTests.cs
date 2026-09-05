using Packet.Node.Core.Radios;
using M0LTE.Radio.Tait;

namespace Packet.Node.Tests.Radios;

/// <summary>
/// The judgement half of a test transmission: turning a handful of raw detector millivolts into
/// something an operator can act on. The serial half (key, sample, unkey) needs a radio; this is the
/// part that decides what the readings <em>mean</em>, and it is where getting it wrong sends someone
/// up a mast for nothing - or, worse, says "looks fine" over a disconnected feeder.
/// </summary>
[Trait("Category", "Node")]
public sealed class TaitTestTransmitTests
{
    private const string Port = "vhf-1";

    // A 2 m (B1) TM8110. The band split is what selects the service manual's detector figures, and
    // it is parsed out of the RADIO_VERSIONS [00] product code.
    private static TaitRadioIdentity Radio(string productCode = "TMAB12-B100_0201") =>
        new('1', '3', '2', "1.0", "19925328", new Dictionary<string, string> { ["00"] = productCode });

    private static TaitTestTxResult Run(
        IEnumerable<int> forward, IEnumerable<int> reverse,
        bool keyed = true, bool inhibited = false, int idleForward = 10, int idleReverse = 4,
        string productCode = "TMAB12-B100_0201")
    {
        var f = forward.ToList();
        var r = reverse.ToList();
        return TaitTestTransmitService.Summarise(
            Port, DateTimeOffset.UnixEpoch, 1000, Radio(productCode), keyed, inhibited,
            [idleForward, idleForward, idleForward], [idleReverse, idleReverse, idleReverse],
            f, r, f);
    }

    [Fact]
    public void A_well_matched_antenna_reads_ok_and_the_vswr_comes_out_of_the_voltage_ratio()
    {
        // Detector voltage goes as the square root of power, so the offset-corrected reverse/forward
        // VOLTAGE ratio is the reflection coefficient directly: 172/1720 = 0.1 -> (1.1/0.9) = 1.22:1.
        var result = Run([1730, 1730, 1730, 1730], [176, 176, 176, 176]);

        result.Verdict.Should().Be("ok");
        result.ForwardOverIdleMillivolts.Should().Be(1720);
        result.ReverseOverIdleMillivolts.Should().Be(172);
        result.ReflectionCoefficient.Should().BeApproximately(0.1, 0.001);
        result.Vswr.Should().BeApproximately(1.222, 0.005);
        result.Band.Should().Be("B1");
        result.Notes.Should().Contain(n => n.Contains("ESTIMATE", StringComparison.Ordinal),
            "a number derived from uncalibrated detectors must never be presented as a measurement");
    }

    [Fact]
    public void A_collapsing_forward_reading_is_the_manuals_own_signature_for_a_folded_back_pa()
    {
        // MMA-00005-05 Task 4 (p.280): a momentarily very high reverse power means the antenna VSWR
        // threshold has been exceeded and the PA has shut down to very low power. What that looks
        // like from here is forward power falling off a cliff part-way through the key.
        var result = Run([1700, 1650, 300, 90, 80], [700, 720, 120, 40, 35]);

        result.Foldback.Should().BeTrue();
        result.Verdict.Should().Be("foldback");
        result.Notes[0].Should().Contain("COLLAPSED");
    }

    [Fact]
    public void Reverse_power_over_the_bands_tabulated_ceiling_is_called_out()
    {
        // B1's ceiling is 500 mV (Tables 11.3 / 12.3, High power into a good load). A steady
        // forward reading with 900 mV coming back is not foldback - it is a bad match.
        var result = Run([1710, 1710, 1710, 1710], [904, 904, 904, 904]);

        result.Foldback.Should().BeFalse();
        result.Verdict.Should().Be("high-reverse");
        result.Reference!.ReverseCeilingMillivolts.Should().Be(500);
        result.Notes[0].Should().Contain("900 mV");
    }

    [Fact]
    public void A_refusal_to_transmit_beats_every_other_reading()
    {
        // PROGRESS 02 is the one failure the radio actually tells software about, and it means
        // nothing went out - so whatever the detectors said is beside the point.
        var result = Run([12, 11, 12], [5, 4, 5], keyed: false, inhibited: true);

        result.Verdict.Should().Be("inhibited");
        result.Inhibited.Should().BeTrue();
        result.Notes[0].Should().Contain("REFUSED");
    }

    [Fact]
    public void A_transmitter_that_never_came_up_is_not_reported_as_a_good_antenna()
    {
        var result = Run([10, 10, 10], [4, 4, 4], keyed: false);

        result.Verdict.Should().Be("no-transmit");
        result.Vswr.Should().BeNull();
    }

    [Fact]
    public void Below_the_estimate_floor_the_panel_says_so_rather_than_inventing_a_figure()
    {
        // At Very Low power the reverse reading is diode knee and coupler directivity floor, not
        // reflected power. A ratio computed there is noise with a decimal point on it.
        var result = Run([120, 118, 122], [30, 28, 31]);

        result.Verdict.Should().Be("unknown");
        result.Vswr.Should().BeNull();
        result.ReflectionCoefficient.Should().BeNull();
        result.Notes[0].Should().Contain("higher power step");
    }

    [Fact]
    public void An_unknown_band_split_loses_the_reference_figures_but_not_the_estimate()
    {
        var result = Run([1730, 1730, 1730], [176, 176, 176], productCode: "TMAB12-ZZ00_0201");

        result.Band.Should().BeNull();
        result.Reference.Should().BeNull();
        result.Verdict.Should().Be("ok");
        result.Vswr.Should().BeApproximately(1.222, 0.005);
    }

    [Fact]
    public void Idle_offsets_are_subtracted_so_a_detectors_zero_reading_is_not_read_as_reflection()
    {
        var withOffset = Run([1730, 1730, 1730], [276, 276, 276], idleForward: 30, idleReverse: 100);

        withOffset.ForwardOverIdleMillivolts.Should().Be(1700);
        withOffset.ReverseOverIdleMillivolts.Should().Be(176);
        withOffset.IdleReverseMillivolts.Should().Be(100);
        withOffset.Verdict.Should().Be("ok", "the 100 mV sitting on the reverse detector at idle is not reflected power");
    }
}
