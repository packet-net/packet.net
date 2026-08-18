namespace Packet.NetRom.Routing;

/// <summary>
/// The INP3 <b>SNTT</b> (Smoothed Neighbour Transport Time) integer IIR smoother -
/// the link-timing metric the route layer sums. It is an integer EWMA over
/// <c>RTT/2</c> raw samples, in milliseconds, with the same round-to-nearest
/// integer discipline as <see cref="NetRomQuality.Combine"/> (no floating point
/// anywhere, so the pico-node M0+ has no FPU dependency and the three stacks agree
/// bit-for-bit).
/// </summary>
/// <remarks>
/// <para>
/// The locked default filter is a <b>1/8-gain IIR</b> (the AX.25 SRT smoothed
/// round-trip-time convention, a shift-by-3):
/// </para>
/// <code>
///   SNTT' = (7 × SNTT + sample + 4) / 8        (integer division)
/// </code>
/// <para>
/// generalised to the configurable shift form so the gain stays a single integer
/// knob and the divide is a shift:
/// </para>
/// <code>
///   denom = 1 &lt;&lt; gainShift                       // gainShift = 3 ⇒ denom 8 ⇒ gain 1/8
///   SNTT' = ((denom - 1) × SNTT + sample + (denom &gt;&gt; 1)) &gt;&gt; gainShift
/// </code>
/// <para>
/// The <c>+ (denom &gt;&gt; 1)</c> is round-to-nearest on the divide (exactly the
/// <c>+ 128</c> in <c>(a × b + 128) / 256</c>). This is a one-pole low-pass with
/// gain <c>g = 1 / denom</c>: <c>SNTT' = SNTT + (sample − SNTT) / denom</c>,
/// rewritten to keep all intermediates non-negative for the integer divide.
/// </para>
/// <para>
/// <b>First-sample seeding (LOCKED).</b> A fresh neighbour has no history; seeding
/// SNTT = 0 would make the filter crawl up from zero and badly under-report the
/// link at first. The first valid sample therefore seeds the filter directly
/// (<c>SNTT := sample</c>, no smoothing on sample #1); every subsequent sample
/// applies the IIR. This is the canonical SRT/Karn seeding. The
/// <see cref="Unset"/> sentinel (<c>uint.MaxValue</c>) means "no measurement yet,"
/// distinct from a real <c>0 ms</c> (a same-host loopback could legitimately
/// measure ~0).
/// </para>
/// <para>
/// <b>Overflow / range (LOCKED).</b> Samples are clamped to
/// <c>[0, <see cref="SampleMaxMs"/>]</c> (the INP3 600 s horizon - a transport
/// time at/over the horizon is "unreachable," and the 180 s link reset tears the
/// link down long before a real RTT reaches 600 s anyway). With both inputs
/// ≤ 600 000, the worst-case accumulator <c>7 × 600 000 + 600 000 + 4 =
/// 4 800 004</c> sits far under <see cref="int.MaxValue"/>, so a 32-bit
/// intermediate is safe with &gt; 400× headroom - no widening to 64-bit. The IIR is
/// a convex combination of two values each in <c>[0, 600 000]</c>, so the result
/// stays in <c>[0, 600 000]</c>.
/// </para>
/// <para>
/// <b>Gain is interop-tuning, NOT wire-compat.</b> Two nodes never exchange their
/// smoothing gain - only the resulting SNTT-derived target times in RIPs, and even
/// those are advisory. The gain only affects how twitchy vs. sluggish our own link
/// metric is. The default 1/8 is exposed as <see cref="DefaultGainShift"/> and is
/// configurable per-call; cross-stack parity is "identical given identical config,"
/// so all three stacks must use the same configured value.
/// </para>
/// <para>
/// This is a pure value type - it carries only the smoothed value and the seeded
/// flag, holds no clock, and performs no I/O. The host-free
/// <c>Inp3Engine</c> owns the RTT measurement loop and feeds <c>RTT/2</c> samples
/// here.
/// </para>
/// </remarks>
public readonly struct Inp3Sntt : IEquatable<Inp3Sntt>
{
    /// <summary>
    /// The default SNTT IIR gain as a right-shift: <c>gain = 1 / (1 &lt;&lt;
    /// GainShift)</c>. Default <c>3</c> ⇒ gain <c>1/8</c> (the AX.25 SRT
    /// convention). Interop-tuning, not wire-compat (design AMBIGUITY-I2-1).
    /// </summary>
    public const int DefaultGainShift = 3;

    /// <summary>
    /// The minimum valid gain shift (gain <c>1/2</c>). A shift of <c>0</c> would be
    /// gain <c>1</c> = no smoothing (pointless), so it is rejected.
    /// </summary>
    public const int MinGainShift = 1;

    /// <summary>
    /// The maximum valid gain shift (gain <c>1/256</c>). Past this the filter is
    /// sluggish beyond usefulness.
    /// </summary>
    public const int MaxGainShift = 8;

    /// <summary>
    /// The upper clamp on a raw sample, in milliseconds - the INP3 600 s
    /// "unreachable" horizon (i1-wire-spec §2.4). A sample at/over this is clamped;
    /// the smoothed result therefore also stays within <c>[0, SampleMaxMs]</c>.
    /// </summary>
    public const uint SampleMaxMs = 600_000;

    /// <summary>
    /// The "no measurement yet" sentinel for <see cref="Ms"/> - distinct from a
    /// real <c>0 ms</c>. <see cref="Initialised"/> is the canonical test; this value
    /// is exposed so callers that store the raw <c>uint</c> (the per-neighbour state
    /// field) can recognise the un-seeded state.
    /// </summary>
    public const uint Unset = uint.MaxValue;

    private readonly uint _ms;

    private Inp3Sntt(uint ms) => _ms = ms;

    /// <summary>
    /// A fresh, un-seeded smoother - no measurement yet. The first
    /// <see cref="Update(uint,int)"/> seeds it directly from the sample.
    /// </summary>
    public static Inp3Sntt Fresh => new(Unset);

    /// <summary>
    /// Seed a smoother directly from a first sample (no smoothing), e.g. when
    /// reconstructing state. Equivalent to <c>Fresh.Update(sampleMs)</c>. The sample
    /// is clamped to <c>[0, <see cref="SampleMaxMs"/>]</c>.
    /// </summary>
    /// <param name="sampleMs">The first <c>RTT/2</c> sample, in milliseconds.</param>
    public static Inp3Sntt Seed(uint sampleMs) => new(ClampSample(sampleMs));

    /// <summary>
    /// True once at least one sample has been folded in. While false, <see cref="Ms"/>
    /// is <see cref="Unset"/> and the route layer must treat the neighbour as
    /// contributing no time-route.
    /// </summary>
    public bool Initialised => _ms != Unset;

    /// <summary>
    /// The smoothed neighbour transport time in milliseconds, or <see cref="Unset"/>
    /// (<c>uint.MaxValue</c>) if no sample has been folded in yet. Always in
    /// <c>[0, <see cref="SampleMaxMs"/>]</c> once <see cref="Initialised"/>.
    /// </summary>
    public uint Ms => _ms;

    /// <summary>
    /// The smoothed value as a nullable, for the route layer: the millisecond value
    /// once <see cref="Initialised"/>, else <c>null</c>.
    /// </summary>
    public uint? Value => Initialised ? _ms : null;

    /// <summary>
    /// Fold a new <c>RTT/2</c> sample into the smoother using the default 1/8 gain,
    /// returning the new smoothed value. The first sample seeds directly; every
    /// subsequent sample applies the integer IIR. The sample is clamped to
    /// <c>[0, <see cref="SampleMaxMs"/>]</c> before smoothing.
    /// </summary>
    /// <param name="sampleMs">The new <c>RTT/2</c> sample, in milliseconds.</param>
    /// <returns>A new <see cref="Inp3Sntt"/> with the updated smoothed value.</returns>
    public Inp3Sntt Update(uint sampleMs) => Update(sampleMs, DefaultGainShift);

    /// <summary>
    /// Fold a new <c>RTT/2</c> sample into the smoother using the given gain shift
    /// (<c>gain = 1 / (1 &lt;&lt; gainShift)</c>), returning the new smoothed value.
    /// The first sample seeds directly; every subsequent sample applies the integer
    /// IIR <c>((denom-1)·SNTT + sample + denom/2) &gt;&gt; gainShift</c>. The sample
    /// is clamped to <c>[0, <see cref="SampleMaxMs"/>]</c> before smoothing.
    /// </summary>
    /// <param name="sampleMs">The new <c>RTT/2</c> sample, in milliseconds.</param>
    /// <param name="gainShift">
    /// The IIR gain as a right-shift, in <c>[<see cref="MinGainShift"/>,
    /// <see cref="MaxGainShift"/>]</c>. Default 3 ⇒ 1/8.
    /// </param>
    /// <returns>A new <see cref="Inp3Sntt"/> with the updated smoothed value.</returns>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="gainShift"/> is outside <c>[<see cref="MinGainShift"/>,
    /// <see cref="MaxGainShift"/>]</c>.
    /// </exception>
    public Inp3Sntt Update(uint sampleMs, int gainShift)
    {
        if (gainShift < MinGainShift || gainShift > MaxGainShift)
        {
            throw new ArgumentOutOfRangeException(
                nameof(gainShift), gainShift,
                $"SNTT gain shift must be in [{MinGainShift}, {MaxGainShift}] (gain 1/2 .. 1/256).");
        }

        uint sample = ClampSample(sampleMs);

        // First valid sample seeds the filter directly (canonical SRT/Karn seeding);
        // smoothing begins at the second sample.
        if (!Initialised)
        {
            return new Inp3Sntt(sample);
        }

        // Integer IIR, round-to-nearest:
        //   denom = 1 << gainShift
        //   SNTT' = ((denom - 1) * SNTT + sample + denom/2) >> gainShift
        //
        // Accumulator headroom: with SNTT ≤ 600_000 and sample ≤ 600_000 and the
        // largest denom (256), the worst case is 255*600_000 + 600_000 + 128 =
        // 153_600_128 - far under int.MaxValue (2.1e9). int intermediate is safe.
        int denom = 1 << gainShift;
        int accumulator = (denom - 1) * (int)_ms + (int)sample + (denom >> 1);
        uint smoothed = (uint)(accumulator >> gainShift);

        // The IIR is a convex combination of two values each in [0, SampleMaxMs],
        // so the result is already in range; assert as a cheap invariant.
        System.Diagnostics.Debug.Assert(
            smoothed <= SampleMaxMs, "SNTT IIR result escaped [0, SampleMaxMs].");

        return new Inp3Sntt(smoothed);
    }

    /// <summary>
    /// Raw-<c>uint</c> alias for <see cref="Unset"/>, for callers (e.g. the
    /// per-neighbour state in <c>Inp3Engine</c>) that store the smoothed value as a
    /// bare <c>uint</c> rather than an <see cref="Inp3Sntt"/>. Identical to
    /// <see cref="Unset"/>.
    /// </summary>
    public const uint SnttUnset = Unset;

    /// <summary>
    /// Fold a <c>RTT/2</c> sample into a raw-<c>uint</c> smoothed value - the
    /// per-neighbour-state form of <see cref="Update(uint,int)"/>.
    /// <paramref name="currentMs"/> is <see cref="SnttUnset"/> for an un-seeded
    /// neighbour (the sample then seeds directly) or a prior smoothed value (the IIR
    /// applies). Returns the new raw smoothed value (never <see cref="SnttUnset"/>).
    /// </summary>
    /// <param name="currentMs">The prior smoothed value, or <see cref="SnttUnset"/>.</param>
    /// <param name="sampleMs">The new <c>RTT/2</c> sample, in milliseconds (clamped to <see cref="SampleMaxMs"/>).</param>
    /// <param name="gainShift">The IIR gain shift, in <c>[<see cref="MinGainShift"/>, <see cref="MaxGainShift"/>]</c>.</param>
    public static uint Smooth(uint currentMs, uint sampleMs, int gainShift)
    {
        var state = currentMs == Unset ? Fresh : new Inp3Sntt(currentMs);
        return state.Update(sampleMs, gainShift).Ms;
    }

    private static uint ClampSample(uint sampleMs) => sampleMs > SampleMaxMs ? SampleMaxMs : sampleMs;

    /// <inheritdoc/>
    public bool Equals(Inp3Sntt other) => _ms == other._ms;

    /// <inheritdoc/>
    public override bool Equals(object? obj) => obj is Inp3Sntt other && Equals(other);

    /// <inheritdoc/>
    public override int GetHashCode() => _ms.GetHashCode();

    /// <summary>Value equality.</summary>
    public static bool operator ==(Inp3Sntt left, Inp3Sntt right) => left.Equals(right);

    /// <summary>Value inequality.</summary>
    public static bool operator !=(Inp3Sntt left, Inp3Sntt right) => !left.Equals(right);

    /// <inheritdoc/>
    public override string ToString() => Initialised ? $"{_ms} ms" : "unset";
}
