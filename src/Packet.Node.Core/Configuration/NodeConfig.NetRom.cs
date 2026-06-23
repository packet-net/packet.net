using Packet.NetRom;
using Packet.NetRom.Wire;
using YamlDotNet.Serialization;

namespace Packet.Node.Core.Configuration;

/// <summary>
/// The node's NET/ROM routing role — the single 3-state knob that replaces the old
/// pair of <c>connect</c> + <c>forward</c> bools (which had an inert combination:
/// <c>forward</c> did nothing unless <c>connect</c> was also on, because forwarding
/// reuses the connected-mode interlink machinery <c>connect</c> gated). The three
/// states are a clean escalation of how much transit work the node does, orthogonal
/// to <see cref="NetRomConfig.Enabled"/> (hearing) and
/// <see cref="NetRomConfig.Broadcast"/> (advertising). See <see cref="NetRomConfig.Routing"/>.
/// </summary>
/// <remarks>
/// This is a node-host config concept (how this station participates), distinct from
/// the library's <see cref="NetRomForwardMode"/> (how a transit node picks <em>among</em>
/// kept routes). They compose: <see cref="Transit"/> + <see cref="NetRomForwardMode"/>.
/// </remarks>
public enum NetRomRouting
{
    /// <summary>Passive — listen for NODES and maintain the routing table only; no
    /// connected-mode interlinks, no transit. The default (equivalent to the old
    /// <c>connect: false</c>). A stock node hears the network but transmits nothing
    /// on it and opens no circuits.</summary>
    None,

    /// <summary>Open connected-mode interlinks so this node's own
    /// <c>connect &lt;alias&gt;</c> can route an L4 circuit across the network, but do
    /// <b>not</b> relay third-party transit datagrams. An endpoint that participates
    /// (originates + terminates circuits) without carrying others' traffic
    /// (equivalent to the old <c>connect: true, forward: false</c>).</summary>
    Endpoint,

    /// <summary>Full router — interlinks for our own circuits <b>and</b> relay transit
    /// datagrams for other stations (TTL-decremented, hop-by-hop). The network-layer
    /// routing role (equivalent to the old <c>connect: true, forward: true</c>).</summary>
    Transit,
}

/// <summary>
/// NET/ROM configuration. The node always <b>hears</b> NODES routing broadcasts
/// (UI frames to dest <c>NODES</c>, PID 0xCF) via the frame-trace tap, parses
/// them, and builds a routing table surfaced in <c>Nodes</c> / a future MCP tool —
/// the read-only awareness slice. With <see cref="Broadcast"/> on it also
/// <b>originates</b> its own NODES broadcast on the NODESINTERVAL schedule, and the
/// <see cref="Routing"/> mode escalates how much it routes:
/// <see cref="NetRomRouting.Endpoint"/> opens <b>L4 virtual circuits</b> over
/// connected-mode AX.25 interlinks so <c>connect &lt;alias&gt;</c> routes a user to
/// a distant node, and <see cref="NetRomRouting.Transit"/> additionally
/// <b>forwards transit datagrams</b> for other stations — the full network-layer
/// routing role.
/// </summary>
/// <remarks>
/// <para>
/// The knobs are exposed because NET/ROM has no single normative standard — the
/// canonical defaults apply unless the operator overrides, never a silent BPQ-ism.
/// Default <see cref="Enabled"/> is <c>true</c> (hearing is free + harmless), but
/// the TX-bearing escalations (<see cref="Broadcast"/>, <see cref="Routing"/>)
/// default off / <see cref="NetRomRouting.None"/>: a stock node does not transmit on
/// the air or open interlinks until the operator opts in (spec-faithful +
/// safe-by-default).
/// </para>
/// <para>
/// <b>Back-compat.</b> The old <c>connect</c> + <c>forward</c> bools are retained as
/// nullable <b>legacy inputs</b> (<see cref="Connect"/>, <see cref="Forward"/>) that
/// feed <see cref="ResolveRouting"/>; an existing config with those keys keeps parsing
/// and behaving identically. <see cref="Routing"/> wins when explicitly set. See
/// <see cref="ResolveRouting"/> for the mapping + the warnings it surfaces.
/// </para>
/// </remarks>
public sealed record NetRomConfig
{
    /// <summary>Whether to listen for NODES broadcasts and maintain the routing
    /// table. Default <c>true</c> (read-only, harmless). Set <c>false</c> to make
    /// the node deaf to NET/ROM entirely (also disables broadcast + routing).</summary>
    public bool Enabled { get; init; } = true;

    /// <summary>
    /// Whether to <b>originate</b> our own NODES routing broadcast (and so advertise
    /// our presence + learned routes to neighbours). Default <c>false</c> —
    /// transmitting on the air is opt-in. Requires <see cref="Enabled"/>.
    /// </summary>
    public bool Broadcast { get; init; }

    /// <summary>
    /// The node's routing role (see <see cref="NetRomRouting"/>). The single 3-state
    /// successor to the old <c>connect</c> + <c>forward</c> bools.
    /// <see cref="NetRomRouting.None"/> (the default) is passive — hear + maintain the
    /// table, no interlinks, no transit; <see cref="NetRomRouting.Endpoint"/> opens
    /// interlinks for our own <c>connect &lt;alias&gt;</c> but does not relay transit;
    /// <see cref="NetRomRouting.Transit"/> is the full router (interlinks + relay
    /// transit). Requires <see cref="Enabled"/> when not <see cref="NetRomRouting.None"/>.
    /// </summary>
    /// <remarks>
    /// <b>Bound nullable to distinguish unset from explicit <c>None</c>.</b> The YAML
    /// binder writes <c>null</c> when the <c>routing:</c> key is absent and a concrete
    /// value when it is present — so <see cref="ResolveRouting"/> can tell "operator said
    /// nothing, fall back to the legacy <c>connect</c>/<c>forward</c> keys" apart from
    /// "operator explicitly chose <c>none</c>, ignore the legacy keys". A non-nullable
    /// enum defaulting to <c>None</c> could not make that distinction. Every consumer
    /// reads the resolved value via <see cref="EffectiveRouting"/>, never this raw field.
    /// </remarks>
    public NetRomRouting? Routing { get; init; }

    /// <summary>
    /// <b>Legacy input (deprecated — use <see cref="Routing"/>).</b> Whether
    /// <c>connect &lt;alias&gt;</c> may route across the network via NET/ROM L4
    /// circuits. Retained as a nullable input so existing configs with a <c>connect:</c>
    /// key keep parsing + behaving identically; <c>null</c> = the key was absent. Fed to
    /// <see cref="ResolveRouting"/> only when <see cref="Routing"/> is unset. Never read
    /// directly by a consumer — read <see cref="EffectiveRouting"/>.
    /// </summary>
    public bool? Connect { get; init; }

    /// <summary>
    /// <b>Legacy input (deprecated — use <see cref="Routing"/>).</b> Whether this node
    /// forwarded transit datagrams. Retained as a nullable input so existing configs
    /// with a <c>forward:</c> key keep parsing; <c>null</c> = the key was absent. Note
    /// the old wart this fix removes: <c>forward</c> was inert unless <c>connect</c> was
    /// also on. Fed to <see cref="ResolveRouting"/> only when <see cref="Routing"/> is
    /// unset; see that method for the mapping (including the contradictory
    /// <c>connect:false, forward:true</c> → <see cref="NetRomRouting.None"/> + a warning).
    /// Never read directly by a consumer — read <see cref="EffectiveRouting"/>.
    /// </summary>
    public bool? Forward { get; init; }

    /// <summary>
    /// How a forwarding node picks among multiple kept routes to a destination
    /// (<see cref="NetRomForwardMode"/>). Default
    /// <see cref="NetRomForwardMode.PerFlow"/> — a transit node spreads distinct L4
    /// circuits across the kept routes, quality-weighted, each circuit pinned to one
    /// path (so its ordering is preserved). Set <see cref="NetRomForwardMode.BestRoute"/>
    /// to always use the single best route. Only consulted when <see cref="Routing"/>
    /// is <see cref="NetRomRouting.Transit"/>.
    /// </summary>
    public NetRomForwardMode ForwardMode { get; init; } = NetRomForwardMode.PerFlow;

    // The node's NET/ROM alias is no longer a separate field here — it is unified with
    // Identity.Alias (the single node-name concept, the BPQ NODEALIAS model). The NODES broadcast
    // takes its alias from Identity.Alias; a pre-v2 config's netRom.alias is folded into
    // Identity.Alias by the v1→v2 schema migration (NodeConfigSchemaMigrations).

    /// <summary>Path quality assumed for a directly-heard neighbour (the canonical
    /// default-port quality). Null = canonical default (192).</summary>
    public int? DefaultNeighbourQuality { get; init; }

    /// <summary>Worst quality a learned route may have and still be kept (MINQUAL).
    /// Null = canonical default (0 — keep everything above zero).</summary>
    public int? MinQuality { get; init; }

    /// <summary>Obsolescence count a route is (re)initialised to on a broadcast
    /// (OBSINIT). Null = canonical default (6).</summary>
    public int? ObsoleteInitial { get; init; }

    /// <summary>The obsolescence advertise-gate (OBSMIN): a route below this is kept
    /// but no longer included in our outgoing broadcasts. Null = canonical default (4).</summary>
    public int? ObsoleteMinimum { get; init; }

    /// <summary>Seconds between obsolescence sweeps + (when <see cref="Broadcast"/>)
    /// NODES broadcasts — the canonical NODESINTERVAL. Null = default (3600 — once an
    /// hour).</summary>
    public int? SweepIntervalSeconds { get; init; }

    /// <summary>The L4 circuit send-window we propose / accept (BPQ <c>L4WINDOW</c>).
    /// Null = canonical default (4).</summary>
    public int? Window { get; init; }

    /// <summary>The L4 retransmit timeout in seconds (BPQ <c>L4TIMEOUT</c>-ish). Null
    /// = default (5 s).</summary>
    public int? TransportTimeoutSeconds { get; init; }

    /// <summary>Max L4 retransmit attempts before a circuit fails (BPQ
    /// <c>L4RETRIES</c>). Null = default (3).</summary>
    public int? TransportRetries { get; init; }

    /// <summary>Initial L3 network-header time-to-live (hop limit) on circuits we
    /// originate (BPQ <c>L3TIMETOLIVE</c>). Null = default (25).</summary>
    public int? TimeToLive { get; init; }

    /// <summary>
    /// Offer LinBPQ-style negotiated NET/ROM L4 payload compression on circuits (the BPQ
    /// <c>L4Compress</c> / <c>L2Compress</c> capability). <b>Default <c>false</c></b>
    /// (decline) — compression is always negotiated, so declining simply runs every link
    /// uncompressed, which any NET/ROM peer can read (the interop-safe path). When set,
    /// the node advertises compression in its Connect Request / Acknowledge and only
    /// actually compresses outbound data to peers that also agreed; a peer that declines
    /// (or doesn't understand the extension) transparently gets uncompressed data. This is
    /// the only NET/ROM knob whose <em>safe</em> default is to stay off — turn it on only
    /// for links to compression-capable BPQ neighbours (e.g. GB7RDG).
    /// </summary>
    public bool Compress { get; init; }

    /// <summary>
    /// The INP3 link-timing routing overlay (default-off). When
    /// <see cref="NetRomInp3Options.Enabled"/> is <c>false</c> — which is the
    /// default, since the property initialises to <c>new()</c> ⇒
    /// <see cref="NetRomInp3Options.Default"/> — the node behaves byte-for-byte as
    /// today: no L3RTT probing, no RIF ingest/emit, no INP3 routes. INP3 is an
    /// opt-in overlay on the vanilla quality-based NET/ROM stack; it requires both
    /// <see cref="Enabled"/> and a <see cref="Routing"/> mode that opens interlinks
    /// (<see cref="NetRomRouting.Endpoint"/> or <see cref="NetRomRouting.Transit"/>) —
    /// the L3RTT / RIF frames ride the connected-mode interlink machinery those modes
    /// gate, so the host constructs the overlay only when interlinks are enabled — the
    /// validator rejects <c>inp3.enabled</c> with <c>routing: none</c> rather than
    /// silently no-op.
    /// </summary>
    /// <remarks>
    /// Unlike the nullable-overlay knobs above (<see cref="Window"/> etc., which are
    /// resolved field-by-field against a lib <c>Default</c>), this binds the whole
    /// <see cref="NetRomInp3Options"/> record directly: it is one validated record
    /// (its own <see cref="NetRomInp3Options.Validate"/> is the single source of
    /// truth for the knob ranges), it is pure durations / ints / bools (no
    /// discriminated union, no <c>Callsign</c> struct), and an absent nested
    /// <c>inp3:</c> key simply leaves the C# default in place under the existing
    /// <c>IgnoreUnmatchedProperties</c> + camel-case deserializer. The
    /// <see cref="System.TimeSpan"/>-typed knobs (<see cref="NetRomInp3Options.L3RttInterval"/>,
    /// <see cref="NetRomInp3Options.L3RttResetWindow"/>, <see cref="NetRomInp3Options.RifInterval"/>,
    /// <see cref="NetRomInp3Options.PositiveDebounce"/>) carry as YAML duration scalars
    /// (e.g. <c>l3RttInterval: 00:01:00</c>) via YamlDotNet's built-in
    /// <c>TimeSpan</c> converter. See docs/netrom-inp3-host-integration-design.md §2.
    /// </remarks>
    public NetRomInp3Options Inp3 { get; init; } = new();

    /// <summary>
    /// The resolved routing role every consumer reads (the <see cref="ResolveRouting"/>
    /// tuple's first element, discarding the warnings). This is the single value
    /// <c>NetRomService</c> / the validator / the API gate on — never the raw
    /// <see cref="Routing"/> / <see cref="Connect"/> / <see cref="Forward"/> fields.
    /// </summary>
    /// <remarks><see cref="YamlIgnoreAttribute"/>: a derived view, not a stored field — it
    /// must never round-trip through the YAML; only <see cref="Routing"/> /
    /// <see cref="Connect"/> / <see cref="Forward"/> are persisted.</remarks>
    [YamlIgnore]
    public NetRomRouting EffectiveRouting => ResolveRouting().Routing;

    /// <summary>
    /// Resolve the effective <see cref="NetRomRouting"/> from the new <see cref="Routing"/>
    /// knob and the legacy <see cref="Connect"/> / <see cref="Forward"/> bools, returning
    /// any back-compat warnings the operator should see (surfaced at config load — see
    /// <c>FileConfigProvider</c>). The mapping, in precedence order:
    /// <list type="bullet">
    /// <item><b><see cref="Routing"/> explicitly set</b> → it wins. If a legacy
    /// <c>connect</c>/<c>forward</c> key is ALSO present, a warning notes they are ignored
    /// in favour of <c>routing</c>.</item>
    /// <item><b>Legacy keys present, <see cref="Routing"/> unset</b> →
    /// <c>connect==true &amp;&amp; forward!=false</c> ⇒ <see cref="NetRomRouting.Transit"/>;
    /// <c>connect==true &amp;&amp; forward==false</c> ⇒ <see cref="NetRomRouting.Endpoint"/>;
    /// <c>connect!=true &amp;&amp; forward!=true</c> ⇒ <see cref="NetRomRouting.None"/>;
    /// <c>connect!=true &amp;&amp; forward==true</c> (the contradictory combo that was always
    /// inert) ⇒ <see cref="NetRomRouting.None"/> <b>plus a warning</b> that <c>forward: true</c>
    /// did nothing without <c>connect</c> and is now treated as <c>routing: none</c>.</item>
    /// <item><b>Nothing set</b> → <see cref="NetRomRouting.None"/> (the default).</item>
    /// </list>
    /// Pure + side-effect free, so it is safe to call from the property getter, the
    /// validator, and the config-load warning path alike.
    /// </summary>
    public (NetRomRouting Routing, IReadOnlyList<string> Warnings) ResolveRouting()
    {
        var legacyPresent = Connect.HasValue || Forward.HasValue;

        if (Routing is { } explicitMode)
        {
            // The new knob wins outright. Note (don't honour) any stale legacy keys.
            return legacyPresent
                ? (explicitMode,
                   ["netrom.connect / netrom.forward are ignored because netrom.routing is set explicitly; remove the legacy keys."])
                : (explicitMode, []);
        }

        if (!legacyPresent)
        {
            return (NetRomRouting.None, []);   // nothing configured — the default.
        }

        // Legacy mapping. Treat absent legacy bools as false (their historical defaults
        // were connect:false, forward:true — but forward only ever mattered under connect,
        // so an absent forward under connect:true maps to Transit, matching old behaviour).
        var connect = Connect == true;
        var forward = Forward == true;

        if (connect)
        {
            // connect:true, forward defaulting on (forward != false) ⇒ Transit;
            // connect:true, forward:false ⇒ Endpoint.
            return (Forward == false ? NetRomRouting.Endpoint : NetRomRouting.Transit, []);
        }

        // connect not on. forward:true here is the contradictory combo — it was always
        // inert (forwarding needs the interlink machinery connect gated). Surface it.
        if (forward)
        {
            return (NetRomRouting.None,
                ["netrom.forward: true had no effect without netrom.connect and is now treated as netrom.routing: none. Set netrom.routing: transit for the full router role."]);
        }

        return (NetRomRouting.None, []);
    }
}
