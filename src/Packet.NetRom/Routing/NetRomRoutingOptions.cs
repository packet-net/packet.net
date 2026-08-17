namespace Packet.NetRom.Routing;

/// <summary>
/// The configurable knobs of NET/ROM route maintenance. These exist because
/// <b>NET/ROM has no single normative standard</b> — the canonical appendix
/// names defaults (OBSINIT 6, three routes per destination), but real nodes set
/// the quality floors and table caps differently (BPQ's per-port MINQUAL,
/// XRouter's deliberately-lower qualities), and quality-floor drift is the
/// perennial NET/ROM interop pain. Per CLAUDE.md we keep the canonical defaults
/// and expose every divergence as a named knob, rather than baking any one
/// node's choices in.
/// </summary>
/// <remarks>
/// Defaults are the canonical-appendix values where one exists, and the most
/// widely-interoperable de-facto value otherwise (documented per-property). All
/// are read-only-ingest concerns: a higher floor simply means we <em>learn</em>
/// fewer routes; nothing here transmits.
/// </remarks>
public sealed record NetRomRoutingOptions
{
    /// <summary>
    /// The path quality assumed for a directly-heard neighbour we have no
    /// configured link quality for — the quality of the assumed direct route to
    /// a broadcast's originator. Canonical default-port path quality is
    /// <b>192</b> (a common direct-link convention; the appendix's worked
    /// examples and BPQ both sit in the 192–203 band).
    /// </summary>
    public int DefaultNeighbourQuality { get; init; } = 192;

    /// <summary>
    /// The worst quality a route may have and still be kept (MINQUAL). A derived
    /// route quality below this is dropped. Canonical floor is <b>0</b> (keep
    /// everything above zero); operators commonly raise it to 128/150/180 to
    /// reject mislabelled-neighbour qualities, so it is a knob.
    /// </summary>
    /// <remarks>
    /// We default to <b>0</b> (canonical, maximally-receptive) so the read-only
    /// table learns the most it can from a mixed network; the node host can raise
    /// it. A quality-0 route is always dropped regardless (the trivial-loop guard
    /// and the "never usable" rule), independent of this floor.
    /// </remarks>
    public int MinQuality { get; init; }

    /// <summary>
    /// The obsolescence count a route is (re)initialised to when a broadcast
    /// adds/refreshes it (OBSINIT). The table is swept at the broadcast interval,
    /// decrementing every route's count; at 0 the route is purged. Canonical
    /// default <b>6</b>.
    /// </summary>
    public int ObsoleteInitial { get; init; } = 6;

    /// <summary>
    /// The obsolescence advertise-gate (BPQ's OBSMIN): a route whose obsolescence
    /// has decayed <em>below</em> this is still kept + usable but is no longer
    /// included in our outgoing NODES broadcasts — so a fading route stops being
    /// advertised before it is finally purged at 0. Canonical / BPQ default
    /// <b>4</b>; a value ≤ 1 advertises every kept route.
    /// </summary>
    public int ObsoleteMinimum { get; init; } = 4;

    /// <summary>
    /// Maximum routes retained per destination (sorted by quality, best first).
    /// Canonical default <b>3</b>.
    /// </summary>
    public int MaxRoutesPerDestination { get; init; } = 3;

    /// <summary>
    /// Upper bound on the number of distinct destinations the table will hold — a
    /// memory-safety cap against an unbounded destination list on a busy network
    /// (BPQ's MAXNODES). Once reached, broadcasts advertising a brand-new
    /// destination are ignored (existing destinations still update). Default
    /// <b>1024</b>, generous for read-only ingest.
    /// </summary>
    public int MaxDestinations { get; init; } = 1024;

    /// <summary>
    /// The node's <b>canonical port ordering</b>, as a rank function: lower rank wins a tie.
    /// Since a neighbour is keyed <b>(port, callsign)</b>, one destination can hold two routes
    /// of identical quality via the same callsign on different ports, and something has to
    /// break that tie deterministically or the selected next hop flaps with dictionary order.
    /// </summary>
    /// <remarks>
    /// The library cannot know configuration order, so the host supplies it (the node host
    /// wires this to <c>PortSupervisor.CanonicalServingPortIds()</c>, i.e. configuration
    /// order). <c>null</c> - the default - ranks by port id <b>ordinal</b>, which is stable and
    /// deterministic but arbitrary. The rank is only ever a <em>tie-break</em>: quality (or, in
    /// the INP3 space, target time) always decides first, so wiring it can never demote a
    /// better route.
    /// </remarks>
    public Func<string, int>? PortRank { get; init; }

    /// <summary>The canonical defaults.</summary>
    public static NetRomRoutingOptions Default { get; } = new();

    /// <summary>
    /// Compare two port ids by the configured <see cref="PortRank"/>, falling back to ordinal
    /// port-id order (which is also the whole comparison when no rank is wired). The single
    /// tie-break used by route ordering, the route cap and the advertisement pick, so all
    /// three agree on which of two equal-quality per-port routes is "the" route.
    /// </summary>
    public int ComparePorts(string portA, string portB)
    {
        if (PortRank is { } rank)
        {
            int a = rank(portA), b = rank(portB);
            if (a != b)
            {
                return a.CompareTo(b);
            }
        }
        return string.CompareOrdinal(portA, portB);
    }
}
