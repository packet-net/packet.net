namespace Packet.Tune.Core.Tests;

/// <summary>Sample-picking rules of the burst meter: DCD-gated medians when
/// carrier tagging exists, and the documented fallbacks (strongest RSSI /
/// max-quieting audio level) when it does not.</summary>
public class NinoTncBurstMeterTests
{
    [Fact]
    public void Audio_level_prefers_the_median_of_carrier_gated_samples()
    {
        List<(float Db, bool Busy)> samples =
        [
            (-33.1f, false), // idle before the burst
            (-62.4f, true),
            (-62.6f, true),
            (-61.9f, true),
            (-33.0f, false), // idle after the burst
        ];

        NinoTncBurstMeter.PickAudioLevel(samples).Should().Be(-62.4f);
    }

    [Fact]
    public void Audio_level_without_dcd_falls_back_to_the_max_quieting_sample()
    {
        // No radio attached → no busy tagging. A carrier QUIETS the RX
        // audio, so the minimum (most negative) sample is the level while
        // the signal was present.
        List<(float Db, bool Busy)> samples =
        [
            (-33.1f, false),
            (-62.4f, false),
            (-33.0f, false),
        ];

        NinoTncBurstMeter.PickAudioLevel(samples).Should().Be(-62.4f);
    }

    [Fact]
    public void Audio_level_with_no_samples_is_null()
    {
        NinoTncBurstMeter.PickAudioLevel([]).Should().BeNull();
    }

    [Fact]
    public void Rssi_without_dcd_falls_back_to_the_strongest_sample()
    {
        // The mirror-image fallback: RF power is strongest during carrier.
        List<(float Dbm, bool Busy)> samples =
        [
            (-128.9f, false),
            (-90.2f, false),
            (-129.1f, false),
        ];

        NinoTncBurstMeter.PickRssi(samples).Should().Be(-90.2f);
    }
}
