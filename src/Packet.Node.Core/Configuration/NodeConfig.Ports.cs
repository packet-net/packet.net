namespace Packet.Node.Core.Configuration;

/// <summary>
/// One AX.25 port — a single radio channel reached through one KISS transport,
/// hosting exactly one <c>Ax25Listener</c>.
/// </summary>
/// <remarks>
/// <see cref="Id"/> is the <b>stable reconcile key</b>: the hot-reload delta
/// matches old and new ports by <see cref="Id"/> to decide added / removed /
/// changed. Renaming a port's <see cref="Id"/> therefore reads as "remove the
/// old, add the new" — a full restart of that port. Keep it stable across edits.
/// </remarks>
public sealed record PortConfig
{
    /// <summary>Stable, operator-chosen identifier for this port (the reconcile
    /// key — see the type remarks). Must be unique within
    /// <see cref="NodeConfig.Ports"/>.</summary>
    public required string Id { get; init; }

    /// <summary>Whether the port should be brought up. A disabled port is
    /// retained in config but torn down at runtime.</summary>
    public bool Enabled { get; init; } = true;

    /// <summary>How to reach the modem for this port (serial KISS, NinoTNC,
    /// KISS-over-TCP, or AXUDP). A discriminated union keyed by its <c>kind</c>.</summary>
    public required TransportConfig Transport { get; init; }

    /// <summary>
    /// Optional named channel-tuning profile (e.g. <c>slow-afsk1200</c>). Opt-in:
    /// it fills only the AX.25 / KISS fields the operator left unset on this port —
    /// an explicit value always wins — and absence means exact spec defaults. See
    /// <see cref="ChannelProfiles"/> for what each profile sets and why it is a
    /// named per-port choice rather than a silent node-wide default.
    /// </summary>
    public string? Profile { get; init; }

    /// <summary>AX.25 listener parameters for this port (timers, window, retries).
    /// Null = spec defaults (or the <see cref="Profile"/>'s value, if a profile is
    /// set and the field is unset here).</summary>
    public Ax25PortParams? Ax25 { get; init; }

    /// <summary>KISS modem tuning (TXDELAY, persistence, slot time) applied live
    /// once the port is up. Null = leave the modem at its power-on defaults.</summary>
    public KissParams? Kiss { get; init; }

    /// <summary>
    /// Optional AX.25 compatibility profile for this port: which wire frames it
    /// accepts (an <c>Ax25ParseOptions</c> preset — <c>strict</c> / <c>lenient</c> /
    /// <c>bpq</c> / <c>xrouter</c> / <c>direwolf</c> — plus per-flag overrides) and
    /// which SDL session quirks new sessions run with. Null = lenient parsing +
    /// default (spec-correct) quirks, the node's historical behaviour. Applied
    /// live on edit: parsing changes take effect on the next inbound frame, quirks
    /// on the next-built session. See <see cref="PortCompatConfig"/>.
    /// </summary>
    public PortCompatConfig? Compat { get; init; }

    /// <summary>
    /// Optional per-port ID-beacon override. Null = inherit the system default
    /// (<see cref="NodeConfig.Beacon"/>) wholesale. When present, its
    /// <see cref="PortBeaconConfig.Enabled"/> flag wins outright, and its nullable
    /// <see cref="PortBeaconConfig.IntervalMinutes"/> / <see cref="PortBeaconConfig.Text"/>
    /// fields fill in from the system default when left null (a per-field merge —
    /// see <see cref="EffectiveBeacon"/>).
    /// </summary>
    public PortBeaconConfig? Beacon { get; init; }

    /// <summary>
    /// Optional per-port NET/ROM <b>route quality</b> (0..255) — the quality assumed
    /// for a directly-heard neighbour on this port, and the quality basis the routes
    /// it advertises are combined against. Overrides the node-wide
    /// <see cref="NetRomConfig.DefaultNeighbourQuality"/> for routes learned on this
    /// port. Null (the default) ⇒ inherit the global value (then the canonical 192).
    /// This is the BPQ per-port <c>QUALITY</c> knob: a mixed-grade node advertises an
    /// accurate quality on each port (e.g. 191 on one link, 192 on another) instead of
    /// one uniform value, so neighbours pick routes correctly. See
    /// <see cref="EffectiveNetRomQuality"/> for the resolution chain.
    /// </summary>
    public int? NetRomQuality { get; init; }

    /// <summary>
    /// Resolve this port's effective NET/ROM neighbour quality: the explicit per-port
    /// <see cref="NetRomQuality"/> if set, else the node-wide
    /// <paramref name="globalDefault"/> (<see cref="NetRomConfig.DefaultNeighbourQuality"/>),
    /// else the canonical default (192 — see <c>NetRomRoutingOptions.DefaultNeighbourQuality</c>).
    /// The returned value is clamped to 0..255 defensively (validation already rejects
    /// out-of-range, but a clamp keeps the routing math total).
    /// </summary>
    public int EffectiveNetRomQuality(int? globalDefault)
        => Math.Clamp(NetRomQuality ?? globalDefault ?? 192, 0, 255);

    /// <summary>
    /// Optional per-port NET/ROM <b>minimum quality</b> (0..255) — the worst quality a
    /// route learned on this port may have and still be kept (the BPQ per-port
    /// <c>MINQUAL</c>). Overrides the node-wide <see cref="NetRomConfig.MinQuality"/> for
    /// NODES broadcasts heard on this port. Null (the default) ⇒ inherit the global value
    /// (then the canonical 0 — keep everything above zero). A mixed-grade node sets a high
    /// floor on a busy/poor port (GB7RDG's 100 on RF) so only good routes survive there,
    /// while keeping a permissive floor elsewhere. See <see cref="EffectiveNetRomMinQuality"/>
    /// for the resolution chain.
    /// </summary>
    public int? NetRomMinQuality { get; init; }

    /// <summary>
    /// Resolve this port's effective NET/ROM minimum quality (MINQUAL): the explicit per-port
    /// <see cref="NetRomMinQuality"/> if set, else the node-wide <paramref name="globalDefault"/>
    /// (<see cref="NetRomConfig.MinQuality"/>), else the canonical default (0 — keep everything
    /// above zero, <c>NetRomRoutingOptions.MinQuality</c>). The returned value is clamped to
    /// 0..255 defensively (validation already rejects out-of-range, but a clamp keeps the
    /// floor comparison total).
    /// </summary>
    public int EffectiveNetRomMinQuality(int? globalDefault)
        => Math.Clamp(NetRomMinQuality ?? globalDefault ?? 0, 0, 255);

    /// <summary>
    /// Optional per-port cap (in octets) on the size of a NET/ROM NODES-broadcast UI frame
    /// (the BPQ per-port <c>NODESPACLEN</c>). When set, a large NODES table fragments into
    /// several smaller UI frames each no larger than this, so the broadcast stays robust on a
    /// slow / shared channel (GB7RDG's <c>NODESPACLEN=160</c> on an RF port). Null (the
    /// default) ⇒ no cap: the broadcast uses the canonical structural limit of
    /// <see cref="Packet.NetRom.Wire.NodesBroadcast.MaxEntriesPerFrame"/> (11) entries per
    /// frame — byte-for-byte today's behaviour. This is the NODES-UI-frame size cap, distinct
    /// from <see cref="Ax25PortParams.N1"/> (the connected-mode I-frame / PACLEN limit).
    /// </summary>
    public int? NodesPaclen { get; init; }
}

/// <summary>
/// Per-port AX.25 listener tuning. Each value is optional; an unset value means
/// "use the engine's spec default". These map onto <c>Ax25ListenerOptions</c>.
/// </summary>
/// <remarks>
/// Changing any of these is a <b>new-sessions-only</b> change — the reconcile
/// rebuilds the listener's option seed so future sessions pick up the new
/// values, but it never reaches into a live session's negotiated context.
/// </remarks>
public sealed record Ax25PortParams
{
    /// <summary>T1 acknowledgement timer seed, milliseconds. Null = engine default.</summary>
    public int? T1Ms { get; init; }

    /// <summary>T2 acknowledge-delay timer (§6.7.1.2), milliseconds. Received
    /// in-sequence I-frames coalesce into one cumulative RR sent T2 after the
    /// first unacknowledged frame. 0 = no delay (ack per frame). Null = engine
    /// default (3000).</summary>
    public int? T2Ms { get; init; }

    /// <summary>T3 inactive-link timer, milliseconds. Null = engine default.</summary>
    public int? T3Ms { get; init; }

    /// <summary>N2 maximum retries before giving up. Null = engine default (10).</summary>
    public int? N2 { get; init; }

    /// <summary>Send-window size k. Null = engine default (4 for mod-8).</summary>
    public int? WindowSize { get; init; }

    /// <summary>
    /// Maximum information-field length in octets — N1 / PACLEN. This is the per-port
    /// frame-length limit: the node segments an outbound SDU into I-frames of at most
    /// this many info octets, and an inbound frame larger than this is not accepted into
    /// a session. Null = engine default (256, the AX.25 v2.2 default + the XID-offered N1).
    /// A heterogeneous node sets a small value on a slow/lossy medium — e.g. ~80 on a
    /// shared-HF port — so on-air frame durations stay short and robust, while leaving the
    /// VHF/UHF ports at the 256 default.
    /// </summary>
    /// <remarks>
    /// <b>Precedence vs XID.</b> The configured value is the port's <em>offered N1</em>
    /// (the link's starting cap). XID negotiation can only LOWER it — the negotiator takes
    /// the minimum of the two ends' offered N1 — so a peer offering a smaller N1 still wins,
    /// but the configured cap is never exceeded on the wire. With no per-port value the
    /// session starts at 256 and XID behaves exactly as before. Like the timer/window knobs
    /// this is a new-sessions-only reseed: it seeds <c>Ax25SessionContext.N1</c> at session
    /// build time and never reaches into a live session's negotiated N1.
    /// </remarks>
    public int? N1 { get; init; }

    /// <summary>LRU cap on cached per-peer sessions. Null = engine default (64).</summary>
    public int? MaxCachedPeers { get; init; }
}

/// <summary>
/// KISS modem tuning knobs, all in the units the KISS spec uses. Each is
/// optional. Applied via the <c>ICsmaChannelParams</c> setters once the port is
/// up, and re-applied live on a hot reconfigure (no port restart).
/// </summary>
public sealed record KissParams
{
    /// <summary>KISS TXDELAY (0x01), in units of 10 ms.</summary>
    public byte? TxDelay { get; init; }

    /// <summary>KISS PERSIST (0x02), 0..255.</summary>
    public byte? Persistence { get; init; }

    /// <summary>KISS SLOTTIME (0x03), in units of 10 ms.</summary>
    public byte? SlotTime { get; init; }

    /// <summary>
    /// KISS TXTAIL (0x04), in units of 10 ms. Unlike the other KISS knobs here this has
    /// an <b>implicit default of 0</b> (not "leave the modem alone"): the node sends an
    /// explicit, deterministic tail to the modem on bring-up, on the regular KISS-param
    /// apply cadence, and on a hot config change — so a port whose <c>txTail</c> is
    /// unset still receives <c>0</c> (see <c>PortSupervisor.ApplyKissParamsToModemAsync</c>).
    /// <para>
    /// 0 is correct for most paths — a NinoTNC into a fully analogue audio path, even on
    /// a slow AFSK1200 channel. A <b>non-zero</b> tail is needed only for a software modem
    /// (samoyed / Dire Wolf) or a NinoTNC driving a radio with a non-zero-latency audio
    /// path: the need is a <em>modem + radio-audio-path-latency</em> property, NOT a
    /// channel/baud one, which the node can't infer — so the operator overrides per port
    /// and that explicit value wins. Set non-null to override the implicit 0.
    /// </para>
    /// </summary>
    public byte? TxTail { get; init; }

    /// <summary>
    /// Whether this port's outbound transmissions are <b>paced</b> over the G8BPQ
    /// ACKMODE extension. Default <c>false</c> — a stock port blasts frames
    /// fire-and-forget exactly as before. When <c>true</c> (and the transport is a
    /// kiss-tcp link to an ACKMODE-honouring TNC / net-sim), the node sends each
    /// frame in ACKMODE and waits for the TNC's TX-completion echo before releasing
    /// the next, serialising its transmissions onto the half-duplex channel instead
    /// of colliding with itself. Unlike the other KISS knobs here this is NOT a live
    /// modem setting — it gates how the modem is constructed (the
    /// <c>PacingKissModem</c> wrapper), so toggling it restarts the port (see
    /// <c>ReconcilePlanner</c>). Only meaningful on a kiss-tcp transport; a no-op on
    /// transports that don't speak ACKMODE.
    /// </summary>
    public bool AckMode { get; init; }

    /// <summary>
    /// TX-complete→T1: when <c>true</c>, the port's AX.25 engine transmits every
    /// T1-arming frame (I-frame, P=1 enquiry, SABM/SABME/DISC) in ACKMODE and
    /// re-arms a still-running T1 to (now + T1V) when the TNC's TX-completion
    /// echo reports the frame has actually cleared the air — so T1 bounds the
    /// peer's response time instead of also absorbing this port's own TX-queue
    /// and airtime (which at 1200 baud makes the default T1 expire mid-window;
    /// see <c>Ax25ListenerOptions.RestartT1OnTxComplete</c> for the full
    /// rationale and the LinkBench measurements). Default <c>false</c>.
    /// Requires an ACKMODE-capable transport (kiss-tcp to net-sim / a real
    /// NinoTNC); on anything else the engine quietly latches back to plain
    /// sends. Composes with (but does not require) <see cref="AckMode"/> pacing.
    /// Like <see cref="AckMode"/> this is a construction-time choice, so a
    /// toggle restarts the port (see <c>ReconcilePlanner</c>).
    /// </summary>
    public bool T1FromTxComplete { get; init; }
}
