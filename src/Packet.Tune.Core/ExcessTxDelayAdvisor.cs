using System.Globalization;

namespace Packet.Tune.Core;

/// <summary>
/// A bounded rolling window of pre-data carrier samples for one heard station: the last
/// <see cref="Capacity"/> measured carrier-rise→first-data leads (ms), with a median.
/// This is the passive, zero-RF aggregation of
/// <c>RadioMetadata.PreDataCarrier</c> — every received burst-opening frame contributes
/// one sample, and the median tracks the PEER's effective TXDELAY (+ a small constant
/// rig overhead, measured ~40–75 ms at 1200 Bd). NOT thread-safe: callers synchronise
/// (the heard log adds under its per-entry lock).
/// </summary>
public sealed class PreDataCarrierWindow
{
    /// <summary>The default window size.</summary>
    public const int DefaultCapacity = 32;

    private readonly Queue<double> samples;

    /// <summary>Create a window keeping the last <paramref name="capacity"/> samples.</summary>
    public PreDataCarrierWindow(int capacity = DefaultCapacity)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(capacity);
        Capacity = capacity;
        samples = new Queue<double>(capacity);
    }

    /// <summary>The window size — the oldest sample beyond it is dropped.</summary>
    public int Capacity { get; }

    /// <summary>Samples currently in the window (≤ <see cref="Capacity"/>) — a
    /// confidence signal for the median.</summary>
    public int Count => samples.Count;

    /// <summary>Add one measured pre-data carrier sample (ms). Non-finite or negative
    /// readings are discarded — a mis-attributed carrier window produces wild values
    /// that must not skew the median.</summary>
    public void Add(double preDataCarrierMs)
    {
        if (!double.IsFinite(preDataCarrierMs) || preDataCarrierMs < 0)
        {
            return;
        }
        samples.Enqueue(preDataCarrierMs);
        while (samples.Count > Capacity)
        {
            samples.Dequeue();
        }
    }

    /// <summary>The rolling median (ms), or <c>null</c> while the window is empty.</summary>
    public double? MedianMs
    {
        get
        {
            if (samples.Count == 0)
            {
                return null;
            }
            var sorted = samples.Order().ToList();
            int mid = sorted.Count / 2;
            return sorted.Count % 2 == 1 ? sorted[mid] : (sorted[mid - 1] + sorted[mid]) / 2.0;
        }
    }
}

/// <summary>Knobs for <see cref="ExcessTxDelayAdvisor"/>.</summary>
public sealed record ExcessTxDelayAdvisorOptions
{
    /// <summary>Median pre-data carrier above which a peer is flagged. The default is
    /// deliberately generous: ~150 ms decodes cleanly on a healthy 1200 Bd link and the
    /// measurement carries a ~40–75 ms constant rig overhead, so 250 ms of measured
    /// lead means real wasted airtime, not measurement noise. Default 250 ms.</summary>
    public double ThresholdMs { get; init; } = 250;

    /// <summary>Minimum samples in the window before advising — one long keying (a
    /// station's first-after-power-on transmission, a manual test) must not flag a
    /// peer. Default 4.</summary>
    public int MinSamples { get; init; } = 4;
}

/// <summary>One excess-TXDELAY advisory for a heard peer.</summary>
/// <param name="Peer">The peer's callsign.</param>
/// <param name="MedianPreDataCarrierMs">The rolling median measured pre-data carrier (ms).</param>
/// <param name="SampleCount">Samples behind the median.</param>
/// <param name="ThresholdMs">The threshold that fired.</param>
public sealed record ExcessTxDelayAdvice(
    string Peer,
    double MedianPreDataCarrierMs,
    int SampleCount,
    double ThresholdMs)
{
    /// <summary>The operator-facing advisory line.</summary>
    public string Message => string.Create(
        CultureInfo.InvariantCulture,
        $"{Peer} keys ~{MedianPreDataCarrierMs:0} ms before data — TXDELAY likely too high, wasting airtime " +
        $"(threshold {ThresholdMs:0} ms, n={SampleCount})");

    /// <inheritdoc/>
    public override string ToString() => Message;
}

/// <summary>
/// The passive excess-TXDELAY detector (layer 1 of the TXDELAY optimisation feature —
/// see <c>docs/research/txdelay-optimisation.md</c>): given a peer's rolling median
/// measured pre-data carrier (from <see cref="PreDataCarrierWindow"/>, fed by
/// <c>RadioMetadata.PreDataCarrier</c>), flag the peers that burn excess preamble.
/// Zero RF — it only ever reads what the station already received. Shared by the node's
/// heard/tuning surfaces and the <c>packet-tune</c> CLI. The active counterpart (fixing
/// our OWN TXDELAY) is <see cref="TxDelayMinimizer"/>.
/// </summary>
public static class ExcessTxDelayAdvisor
{
    /// <summary>
    /// Assess one peer: an <see cref="ExcessTxDelayAdvice"/> when the median exceeds
    /// the threshold with enough samples behind it, else <c>null</c> (no measurement,
    /// too few samples, or a healthy TXDELAY).
    /// </summary>
    /// <param name="peer">The peer's callsign (for the advisory text).</param>
    /// <param name="medianPreDataCarrierMs">The rolling median measured pre-data
    /// carrier in ms, or <c>null</c> when nothing was measured.</param>
    /// <param name="sampleCount">Samples behind the median.</param>
    /// <param name="options">Threshold knobs; null = defaults (250 ms, n≥4).</param>
    public static ExcessTxDelayAdvice? Assess(
        string peer,
        double? medianPreDataCarrierMs,
        int sampleCount,
        ExcessTxDelayAdvisorOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(peer);
        var o = options ?? new ExcessTxDelayAdvisorOptions();
        if (medianPreDataCarrierMs is not { } median || sampleCount < o.MinSamples || median <= o.ThresholdMs)
        {
            return null;
        }
        return new ExcessTxDelayAdvice(peer, median, sampleCount, o.ThresholdMs);
    }
}
