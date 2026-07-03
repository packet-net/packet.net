namespace Packet.Tune.Core.Tests;

/// <summary>The UP/DN/OK heuristic: clipping always wins, solid decode with
/// quiet FEC is OK, and the FEC trend disambiguates struggling bursts.</summary>
public class DeviationAdvisorTests
{
    [Fact]
    public void Clipping_is_always_down()
    {
        var report = new MeterReport(5, 5, FecCorrectedBytesDelta: 0, LostAdcSamplesDelta: 12, RssiDbm: -90);
        DeviationAdvisor.Advise(report).Should().Be(TuningAdvice.Down);
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
    public void Advice_tokens_round_trip()
    {
        foreach (TuningAdvice advice in Enum.GetValues<TuningAdvice>())
        {
            DeviationAdvisor.FromWire(DeviationAdvisor.ToWire(advice)).Should().Be(advice);
        }
        DeviationAdvisor.FromWire("??").Should().BeNull();
    }
}
