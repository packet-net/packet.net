namespace Packet.Radio.Tait;

/// <summary>
/// One radio-health sample taken by <see cref="TaitRadioHealthMonitor"/>. Which fields are
/// populated depends on the transmitter state at sample time: averaged RSSI is read only while
/// receiving (the receiver is muted during the radio's own transmissions), and the
/// offset-corrected forward/reverse fields are computed only while the radio reported its
/// transmitter keyed for the whole sample.
/// </summary>
public sealed record TaitRadioHealthSample
{
    /// <summary>When the sample was taken (monitor clock, sample start).</summary>
    public required DateTimeOffset At { get; init; }

    /// <summary>Whether the radio reported its transmitter keyed throughout the sample
    /// (PROGRESS PTT edges via <see cref="TaitCcdiRadio.TransmitterStateChanged"/>).</summary>
    public required bool Transmitting { get; init; }

    /// <summary>The radio's own sliding-average RSSI in dBm (CCTM 063, 0.1 dB resolution).
    /// <c>null</c> while transmitting — own-RSSI then reads the muted receiver — or when the
    /// read failed.</summary>
    public float? RssiDbm { get; init; }

    /// <summary>Power-amplifier temperature in °C (CCTM 047; TM8100-series reports °C,
    /// TM8200 reports the ADC value only). <c>null</c> when unavailable.</summary>
    public int? PaTemperatureCelsius { get; init; }

    /// <summary>The PA temperature sensor's raw ADC reading in millivolts (CCTM 047).</summary>
    public int? PaDetectorMillivolts { get; init; }

    /// <summary>Raw forward-power detector reading in millivolts (CCTM 318) — uncalibrated
    /// detector-diode DC voltage, ∝ √P. While idle this is the detector's zero-power offset.</summary>
    public int? ForwardPowerMillivolts { get; init; }

    /// <summary>Raw reverse-power detector reading in millivolts (CCTM 319) — uncalibrated
    /// detector-diode DC voltage. While idle this is the detector's zero-power offset.</summary>
    public int? ReversePowerMillivolts { get; init; }

    /// <summary>The forward idle-offset estimate (median of recent not-transmitting CCTM 318
    /// readings) that was in effect when this sample was taken, or <c>null</c> before the first
    /// idle sample.</summary>
    public int? ForwardIdleOffsetMillivolts { get; init; }

    /// <summary>The reverse idle-offset estimate (median of recent not-transmitting CCTM 319
    /// readings) that was in effect when this sample was taken, or <c>null</c> before the first
    /// idle sample.</summary>
    public int? ReverseIdleOffsetMillivolts { get; init; }

    /// <summary>Forward detector reading minus the idle offset, clamped at 0 — populated only on
    /// transmit samples. A per-station trend figure, not a power measurement.</summary>
    public int? TxForwardOverIdleMillivolts { get; init; }

    /// <summary>Reverse detector reading minus the idle offset, clamped at 0 — populated only on
    /// transmit samples. A per-station trend figure, not a power measurement.</summary>
    public int? TxReverseOverIdleMillivolts { get; init; }

    /// <summary>
    /// Offset-corrected reverse/forward detector ratio — populated only on transmit samples
    /// whose corrected forward reading clears
    /// <see cref="TaitRadioHealthMonitorOptions.MinimumForwardForRatioMillivolts"/>.
    /// <b>This is a trend, not VSWR</b>: the detectors are uncalibrated, √P-scaled, and only
    /// service-specified at High power — alert when this figure <i>changes</i> for a station,
    /// never on its absolute value. See <see cref="TaitRadioHealthMonitor"/> remarks.
    /// </summary>
    public double? TxReverseForwardRatio { get; init; }
}

/// <summary>Min/median/max over the samples of one metric inside the summary window.</summary>
/// <param name="Min">Smallest value in the window.</param>
/// <param name="Median">Median value in the window (mean of the middle pair for even counts).</param>
/// <param name="Max">Largest value in the window.</param>
/// <param name="Count">How many samples carried this metric.</param>
public readonly record struct TaitRadioHealthStat(double Min, double Median, double Max, int Count)
{
    internal static TaitRadioHealthStat? Over(List<double> values)
    {
        if (values.Count == 0)
        {
            return null;
        }
        values.Sort();
        int mid = values.Count / 2;
        double median = values.Count % 2 == 1 ? values[mid] : (values[mid - 1] + values[mid]) / 2.0;
        return new TaitRadioHealthStat(values[0], median, values[^1], values.Count);
    }
}

/// <summary>
/// Rolling summary over <see cref="TaitRadioHealthMonitor"/>'s sample window. Consumers should
/// trend these figures and alert on <i>change</i> — none of the transmit-side detector stats are
/// calibrated measurements.
/// </summary>
/// <param name="SampleCount">Samples currently in the window.</param>
/// <param name="TransmitSampleCount">How many of them were transmit samples.</param>
/// <param name="From">Oldest sample instant in the window, or <c>null</c> when empty.</param>
/// <param name="To">Newest sample instant in the window, or <c>null</c> when empty.</param>
/// <param name="RssiDbm">Averaged-RSSI stats over the receive samples (CCTM 063).</param>
/// <param name="PaTemperatureCelsius">PA temperature stats (CCTM 047).</param>
/// <param name="TxForwardOverIdleMillivolts">Offset-corrected forward detector stats over the
/// transmit samples (CCTM 318).</param>
/// <param name="TxReverseOverIdleMillivolts">Offset-corrected reverse detector stats over the
/// transmit samples (CCTM 319).</param>
/// <param name="TxReverseForwardRatio">Offset-corrected reverse/forward ratio stats over the
/// transmit samples — a trend, never VSWR.</param>
public sealed record TaitRadioHealthSummary(
    int SampleCount,
    int TransmitSampleCount,
    DateTimeOffset? From,
    DateTimeOffset? To,
    TaitRadioHealthStat? RssiDbm,
    TaitRadioHealthStat? PaTemperatureCelsius,
    TaitRadioHealthStat? TxForwardOverIdleMillivolts,
    TaitRadioHealthStat? TxReverseOverIdleMillivolts,
    TaitRadioHealthStat? TxReverseForwardRatio);

/// <summary>Behavioural knobs for <see cref="TaitRadioHealthMonitor"/>. The defaults suit a
/// long-running node attachment (samples are three or four CCDI transactions of ~15 ms each, so
/// even aggressive intervals are cheap on the wire).</summary>
public sealed record TaitRadioHealthMonitorOptions
{
    /// <summary>Cadence of the periodic sample. Default 10 s.</summary>
    public TimeSpan SampleInterval { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>How many samples the rolling summary window holds (oldest evicted first).
    /// Default 90 — 15 minutes at the default interval.</summary>
    public int SummaryWindowSize { get; init; } = 90;

    /// <summary>How many recent idle (not-transmitting) detector readings feed the
    /// forward/reverse idle-offset medians. Default 8.</summary>
    public int IdleOffsetWindowSize { get; init; } = 8;

    /// <summary>Take an extra sample when the radio reports its transmitter keying, so short
    /// keyings are captured even when no periodic tick lands inside them. Default on.</summary>
    public bool SampleOnKeying { get; init; } = true;

    /// <summary>How long after a keying edge the extra sample waits for the PA to settle before
    /// reading the detectors. Default 150 ms.</summary>
    public TimeSpan KeyedSampleDelay { get; init; } = TimeSpan.FromMilliseconds(150);

    /// <summary>Minimum offset-corrected forward reading (mV) before a reverse/forward ratio is
    /// computed — below this the ratio is all detector-offset noise. Default 50 mV.</summary>
    public int MinimumForwardForRatioMillivolts { get; init; } = 50;
}
