namespace Packet.Tune.Core.Tests;

/// <summary>The UP/DN/OK/SW heuristic: clipping always wins, zero decodes
/// without clipping is the direction-free "sweep the pot" state, solid decode
/// with quiet FEC is OK, and the FEC trend disambiguates struggling bursts.</summary>
public class DeviationAdvisorTests
{
    [Fact]
    public void Clipping_is_always_down()
    {
        var report = new MeterReport(5, 5, FecCorrectedBytesDelta: 0, LostAdcSamplesDelta: 12, RssiDbm: -90);
        DeviationAdvisor.Advise(report).Should().Be(TuningAdvice.Down);
    }

    [Fact]
    public void Zero_decodes_without_clipping_advise_sweep_not_up()
    {
        // A fully-dead burst carries no direction (too quiet, too loud and
        // no-path all read 0/n) — a directional UP here sent the operator
        // the wrong way on the pre-R2-retap bench rig.
        var report = new MeterReport(0, 5, FecCorrectedBytesDelta: 0, LostAdcSamplesDelta: 0, RssiDbm: -90);
        DeviationAdvisor.Advise(report).Should().Be(TuningAdvice.Sweep);
    }

    [Fact]
    public void Zero_decodes_with_clipping_still_advise_down()
    {
        // Clipping IS directional evidence even when nothing decoded.
        var report = new MeterReport(0, 5, FecCorrectedBytesDelta: 0, LostAdcSamplesDelta: 40, RssiDbm: -90);
        DeviationAdvisor.Advise(report).Should().Be(TuningAdvice.Down);
    }

    [Fact]
    public void Sweep_advice_has_the_no_decode_wording_and_the_SW_token()
    {
        DeviationAdvisor.ToWire(TuningAdvice.Sweep).Should().Be("SW");
        DeviationAdvisor.Describe(TuningAdvice.Sweep).Should().Be("no decode — sweep the pot");
    }

    [Fact]
    public void Legacy_directional_tokens_are_unchanged_on_the_wire()
    {
        // AD-args backward compatibility: UP/DN/OK are what older peers
        // send and expect; SW is purely additive (an old peer parses it as
        // unknown advice, never as a direction).
        DeviationAdvisor.ToWire(TuningAdvice.Up).Should().Be("UP");
        DeviationAdvisor.ToWire(TuningAdvice.Down).Should().Be("DN");
        DeviationAdvisor.ToWire(TuningAdvice.Ok).Should().Be("OK");
    }

    [Fact]
    public void Full_decode_with_quiet_fec_is_ok()
    {
        var report = new MeterReport(5, 5, FecCorrectedBytesDelta: 0, LostAdcSamplesDelta: 0, RssiDbm: -90);
        DeviationAdvisor.Advise(report).Should().Be(TuningAdvice.Ok);
    }

    [Fact]
    public void Full_decode_with_no_fec_signal_is_ok()
    {
        // Plain-AX.25 modes: fec/clip both unavailable.
        var report = new MeterReport(5, 5, null, null, null);
        DeviationAdvisor.Advise(report).Should().Be(TuningAdvice.Ok);
    }

    [Fact]
    public void Missed_decodes_without_clipping_or_fec_trend_advise_up()
    {
        var report = new MeterReport(1, 5, FecCorrectedBytesDelta: 0, LostAdcSamplesDelta: 0, RssiDbm: -90);
        DeviationAdvisor.Advise(report).Should().Be(TuningAdvice.Up);
    }

    [Fact]
    public void Rising_fec_with_held_decode_advises_down()
    {
        var previous = new MeterReport(5, 5, FecCorrectedBytesDelta: 60, LostAdcSamplesDelta: 0, RssiDbm: -90);
        var current = new MeterReport(5, 5, FecCorrectedBytesDelta: 240, LostAdcSamplesDelta: 0, RssiDbm: -90);
        DeviationAdvisor.Advise(current, previous).Should().Be(TuningAdvice.Down);
    }

    [Fact]
    public void The_audio_level_never_changes_the_verdict()
    {
        // Bracketing verdicts are authoritative: the GETRSSI level (3.41-era
        // fast path) is guidance only, whatever it reads.
        var clipping = new MeterReport(5, 5, 0, LostAdcSamplesDelta: 12, RssiDbm: -90, AudioLevelDb: -62.5);
        DeviationAdvisor.Advise(clipping).Should().Be(TuningAdvice.Down);

        var solid = new MeterReport(5, 5, 0, 0, -90, AudioLevelDb: -20.0);
        DeviationAdvisor.Advise(solid).Should().Be(TuningAdvice.Ok);

        var struggling = new MeterReport(1, 5, 0, 0, -90, AudioLevelDb: -62.5);
        DeviationAdvisor.Advise(struggling).Should().Be(TuningAdvice.Up);
    }

    [Fact]
    public void DescribeLevel_is_null_without_a_level()
    {
        var report = new MeterReport(5, 5, 0, 0, -90);
        DeviationAdvisor.DescribeLevel(report, previous: null, idleLevelDb: -34.5).Should().BeNull();
    }

    [Fact]
    public void DescribeLevel_reports_level_quieting_and_a_steady_trend()
    {
        var previous = new MeterReport(5, 5, 0, 0, -90.1, AudioLevelDb: -62.7);
        var current = new MeterReport(5, 5, 0, 0, -90.1, AudioLevelDb: -62.5);

        string? note = DeviationAdvisor.DescribeLevel(current, previous, idleLevelDb: -34.5);

        note.Should().Be("level -62.5 dB, 28.0 dB quieting, level steady");
    }

    [Fact]
    public void DescribeLevel_reports_movement_beyond_the_steady_tolerance()
    {
        var previous = new MeterReport(5, 5, 0, 0, -90.1, AudioLevelDb: -58.0);
        var current = new MeterReport(5, 5, 0, 0, -90.1, AudioLevelDb: -62.5);

        string? note = DeviationAdvisor.DescribeLevel(current, previous);

        note.Should().Be("level -62.5 dB, level -4.5 dB vs last burst",
            "no idle baseline was supplied, so no quieting figure appears");
    }

    [Fact]
    public void DescribeLevel_with_no_previous_and_no_idle_is_just_the_level()
    {
        var current = new MeterReport(5, 5, 0, 0, -90.1, AudioLevelDb: -62.5);
        DeviationAdvisor.DescribeLevel(current).Should().Be("level -62.5 dB");
    }

    [Fact]
    public void Advice_tokens_round_trip()
    {
        foreach (TuningAdvice advice in Enum.GetValues<TuningAdvice>())
        {
            DeviationAdvisor.FromWire(DeviationAdvisor.ToWire(advice)).Should().Be(advice);
        }
        DeviationAdvisor.FromWire("??").Should().BeNull();
    }
}
