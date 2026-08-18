namespace Packet.NetRom.Wire;

/// <summary>
/// The tunable knobs of the INP3 link-timing overlay - the probe cadence, the
/// reflection-timeout reset window, the SNTT smoother gain, optimistic-probe
/// policy, and the advertised capability text. Mirrors
/// <see cref="Packet.NetRom.Transport.NetRomCircuitOptions"/> / <see cref="Packet.NetRom.Routing.NetRomRoutingOptions"/>:
/// a <see cref="Default"/> preset and validated ranges, every divergence a named
/// knob defaulted to an interoperable value (CLAUDE.md "spec-faithful core,
/// pragmatism is a named flag").
/// </summary>
/// <remarks>
/// <para>
/// All durations are driven by an injected <see cref="System.TimeProvider"/> (the
/// engine converts them to milliseconds against a <em>monotonic</em> source) - no
/// wall-clock anywhere in the INP3 layer.
/// </para>
/// <para>
/// The SNTT gain (<see cref="SnttGainShift"/>) is interop-<em>tuning</em>, not
/// wire-compat: two nodes never exchange their smoothing constant, only the
/// resulting (advisory) SNTT-derived target times in RIPs. It does not have to
/// match a peer to interoperate - but cross-stack parity requires all three stacks
/// (C# / TS / Rust) use the same configured value (the "identical given identical
/// config" discipline, like the quality floor).
/// </para>
/// </remarks>
public sealed record NetRomInp3Options
{
    /// <summary>
    /// How often to probe each (capable / optimistically-probed) interlink
    /// neighbour with an L3RTT datagram. Plan §8 <c>l3RttIntervalSeconds</c> default
    /// <b>60 s</b>.
    /// </summary>
    public TimeSpan L3RttInterval { get; init; } = TimeSpan.FromSeconds(60);

    /// <summary>
    /// Reflection-timeout → reset: how long a neighbour may go without reflecting a
    /// probe before its INP3 state is torn down (and, for an INP3-capable
    /// neighbour, <c>NeighbourDown</c> is raised). Plan §8 <c>l3RttResetSeconds</c>
    /// default <b>180 s</b> (the spec value). Must exceed
    /// <see cref="L3RttInterval"/> - a reset window shorter than one probe interval
    /// would tear down a live neighbour before it could answer.
    /// </summary>
    public TimeSpan L3RttResetWindow { get; init; } = TimeSpan.FromSeconds(180);

    /// <summary>
    /// The SNTT IIR gain expressed as a right-shift: <c>gain = 1 / (1 &lt;&lt; SnttGainShift)</c>.
    /// Default <b>3</b> ⇒ gain <c>1/8</c> (the AX.25 SRT convention; shift-by-3, no
    /// multiply). Interop-tuning, <b>not</b> wire-compat (AMBIGUITY-I2-1). Valid
    /// range <b>1..8</b> (gain 1/2 .. 1/256): 0 means gain 1 = no smoothing
    /// (pointless) and &gt; 8 is sluggish past usefulness.
    /// </summary>
    public int SnttGainShift { get; init; } = 3;

    /// <summary>
    /// Probe interlink neighbours whose INP3 capability is not yet known, to
    /// bootstrap discovery (we only learn a peer speaks INP3 by receiving its
    /// probe - AMBIGUITY-I2-2, so we must probe first). Default <b>true</b>. A
    /// never-capable neighbour that never reflects is dropped from probing silently
    /// after one reset window - it is <em>never</em> <c>MarkNeighbourDown</c>'d
    /// (AMBIGUITY-I2-3 guard); only an INP3-capable neighbour that goes silent
    /// raises <c>NeighbourDown</c>.
    /// </summary>
    public bool ProbeUnknownCapability { get; init; } = true;

    /// <summary>
    /// The IP version to advertise in our probes' <c>$IX</c> capability token (e.g.
    /// <c>4</c>), or <c>null</c> for none (<c>$N</c> only). Plan §8
    /// <c>advertiseIp</c>; off unless we run IP-over-NET/ROM. Must be a single
    /// decimal digit 0-9 when set.
    /// </summary>
    public int? AdvertiseIpAccept { get; init; }

    /// <summary>
    /// The emit-side capability-text pad width for probes we build
    /// (AMBIGUITY-L3RTT-3). Default <see cref="Inp3L3RttFrame.DefaultCapabilityTextWidth"/>.
    /// The recogniser is width-independent, so this is purely cosmetic on the wire.
    /// </summary>
    public int CapabilityTextWidth { get; init; } = Inp3L3RttFrame.DefaultCapabilityTextWidth;

    /// <summary>
    /// Prefer INP3 (measured target-time) routes over NODES quality routes when
    /// selecting the active route for a destination - BPQ's <c>PREFERINP3ROUTES</c>
    /// knob (plan §8). Default <b>false</b>: even with the INP3 overlay enabled, the
    /// conservative default keeps quality primary, so a node "turns INP3 on"
    /// (ingesting + advertising time-routes) without changing where traffic flows;
    /// flip this once the measured times are trusted. When <c>true</c>, a destination
    /// that has <em>any</em> INP3 route forwards over its lowest-target-time INP3
    /// route, falling back to the best-quality route only when no INP3 route exists
    /// (the selection truth table, plan risk #4 / <c>docs/netrom-inp3-i3-design.md</c>
    /// §3). When <c>false</c> the <see cref="Packet.NetRom.Routing.NetRomRoute.Inp3"/>
    /// metric is ignored by selection entirely (routes are still ingested + visible
    /// for monitoring and re-advertisement). Consumed by
    /// <see cref="Packet.NetRom.Routing.Inp3RouteSelector.SelectActiveRoute"/>.
    /// </summary>
    public bool PreferInp3Routes { get; init; }

    /// <summary>
    /// Master switch for the whole INP3 overlay (plan §8 <c>inp3.enabled</c>).
    /// Default <b>false</b>: the node behaves exactly as it does today - no L3RTT
    /// probing, no RIF ingestion or emission, no INP3 routes - so enabling the
    /// feature is a deliberate opt-in. This is the host-layer gate that sits above
    /// the (always-correct, host-free) engine + selector; when <c>false</c> the host
    /// simply never drives them.
    /// </summary>
    public bool Enabled { get; init; }

    /// <summary>
    /// The INP3 routing horizon in hops (plan §8 <c>hopLimit</c>): a RIP whose
    /// local hop count would exceed this is not learned, bounding loop blast-radius.
    /// Default <b>30</b>. The host passes this into
    /// <see cref="Packet.NetRom.Routing.NetRomRoutingTable"/>'s RIF ingestion.
    /// </summary>
    public int HopLimit { get; init; } = 30;

    /// <summary>
    /// Periodic full-RIF cadence - the baseline refresh interval (plan §8
    /// <c>rifIntervalSeconds</c>, design I-4 §6.2 "a periodic full RIF on the INP3
    /// interval regardless"). Triggered updates fire regardless of this. Default
    /// <b>300 s</b>. Consumed by
    /// <see cref="Packet.NetRom.Transport.Inp3UpdateScheduler"/>; separate from
    /// NODESINTERVAL and from the L3RTT cadence. Must be positive.
    /// </summary>
    public TimeSpan RifInterval { get; init; } = TimeSpan.FromSeconds(300);

    /// <summary>
    /// Positive-update debounce - how long a NEW / BETTER (positive) route change is
    /// batched before a fan-out, coalescing a burst of positive changes into one RIF
    /// (design I-4 §3.3 rule 2). NEGATIVE changes (loss / worsen-past-threshold)
    /// ignore this and fan out immediately. Default <b>5 s</b>. Must be positive and
    /// strictly less than <see cref="RifInterval"/> (a debounce &gt;= the periodic
    /// interval is pointless - the periodic emit would always drain the batch first).
    /// </summary>
    public TimeSpan PositiveDebounce { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>
    /// The worsen-by amount (ms) at or above which a slowed selected route counts as
    /// NEGATIVE (immediate fan-out) rather than POSITIVE (batched) - design
    /// AMBIGUITY-I4-3. Sub-threshold worsenings are routine SNTT jitter and batched.
    /// A loss / withdrawal is <em>always</em> NEGATIVE regardless of this threshold.
    /// Default <b>1000 ms</b>. The table / ingestion path applies it when classifying
    /// a change for <see cref="Packet.NetRom.Transport.Inp3UpdateScheduler.MarkDirty"/>;
    /// it is a knob here so it is tunable and cross-stack-pinned. Must be non-negative.
    /// </summary>
    public int WorsenThresholdMs { get; init; } = 1000;

    /// <summary>The canonical / widely-interoperable defaults.</summary>
    public static NetRomInp3Options Default { get; } = new();

    /// <summary>
    /// Validate the option ranges. Mirrors the other options records' guards; the
    /// host's config validator surfaces out-of-range YAML (plan §8).
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">Any field is out of its valid range.</exception>
    public void Validate()
    {
        if (L3RttInterval <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(L3RttInterval), L3RttInterval, "L3RTT probe interval must be positive");
        }
        if (L3RttResetWindow <= L3RttInterval)
        {
            throw new ArgumentOutOfRangeException(nameof(L3RttResetWindow), L3RttResetWindow,
                "L3RTT reset window must exceed the probe interval (a shorter window tears down a live neighbour before it can answer)");
        }
        if (SnttGainShift is < 1 or > 8)
        {
            throw new ArgumentOutOfRangeException(nameof(SnttGainShift), SnttGainShift, "SNTT gain shift must be in [1, 8] (gain 1/2 .. 1/256)");
        }
        if (AdvertiseIpAccept is < 0 or > 9)
        {
            throw new ArgumentOutOfRangeException(nameof(AdvertiseIpAccept), AdvertiseIpAccept, "advertised IP-accept version must be a single decimal digit 0–9");
        }
        if (CapabilityTextWidth < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(CapabilityTextWidth), CapabilityTextWidth, "capability text width must be non-negative");
        }
        if (HopLimit < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(HopLimit), HopLimit, "INP3 hop limit must be at least 1");
        }
        if (RifInterval <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(RifInterval), RifInterval, "periodic RIF interval must be positive");
        }
        if (PositiveDebounce <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(PositiveDebounce), PositiveDebounce, "positive-update debounce must be positive");
        }
        if (PositiveDebounce >= RifInterval)
        {
            throw new ArgumentOutOfRangeException(nameof(PositiveDebounce), PositiveDebounce,
                "positive-update debounce must be less than the periodic RIF interval (a debounce >= the interval is pointless — the periodic emit would always drain the batch first)");
        }
        if (WorsenThresholdMs < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(WorsenThresholdMs), WorsenThresholdMs, "worsen threshold must be non-negative");
        }
    }
}
