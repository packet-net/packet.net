namespace Packet.NetRom.Wire;

/// <summary>
/// Per-call configuration for the NET/ROM wire-parse paths
/// (<see cref="NodesBroadcast.TryParse"/>). Each pragmatic accommodation for a
/// real-world node's divergence from the canonical NET/ROM wire format is a
/// named, individually-toggleable flag — exactly the
/// <see cref="Packet.Core.Ax25ParseOptions"/> pattern this project uses for
/// AX.25.
/// </summary>
/// <remarks>
/// <para>
/// <b>There is no single normative NET/ROM standard.</b> The closest thing to
/// canonical is the original protocol appendix
/// (<c>wiki.oarc.uk/_media/packet:thenetromprotocol.pdf</c>); in practice
/// <b>G8BPQ / LinBPQ is the de-facto reference</b>, with XRouter and the Linux
/// kernel <c>netrom</c> family diverging. We treat them all as interop targets,
/// <em>not</em> reference truth — the same discipline CLAUDE.md mandates for
/// AX.25. So the parser is faithful to the canonical appendix by default, and
/// every divergence we accommodate is a flag here (defaulted to preserve the
/// canonical reading), surfaced in the relevant peer preset, and documented in
/// <c>docs/strict-vs-pragmatic-audit.md</c>. We never silently bake a BPQ-ism
/// (or XRouter-ism) into the parser.
/// </para>
/// <para>
/// The current divergences are about <em>tolerance of the table dump</em>, not
/// the field layout (the 0xFF signature, the 6-byte alias, and the 21-byte
/// destination entries are universal). A node that pads its final UI frame, or
/// that runs an entry count not landing exactly on a 21-byte boundary, should
/// not make us drop the whole frame — but accepting that is opt-in, so a strict
/// caller can still reject a malformed dump.
/// </para>
/// </remarks>
public sealed record NetRomParseOptions
{
    /// <summary>
    /// Accept a routing-info region whose length is not an exact multiple of the
    /// 21-byte entry size: parse as many whole 21-byte entries as fit and ignore
    /// a short trailing remainder. Strict canonical NET/ROM emits only whole
    /// entries, so a remainder means either trailing pad or a truncated frame.
    /// </summary>
    /// <remarks>
    /// Driver: real nodes (BPQ included) have been observed padding the final
    /// UI frame of a multi-frame NODES dump, and a noisy RF link can clip the
    /// tail of a frame. Dropping every learned route because the <em>last</em>
    /// entry is short would be hostile; we keep the whole entries we did parse.
    /// Default <c>true</c> (lenient) — this is read-only ingest of third-party
    /// broadcasts, where resilience matters more than rejecting a stray byte.
    /// </remarks>
    public bool AllowTrailingPartialEntry { get; init; } = true;

    /// <summary>
    /// Accept a NODES broadcast carrying <em>zero</em> destination entries (just
    /// the 0xFF signature + the 6-byte sender alias, info field exactly 7 bytes).
    /// </summary>
    /// <remarks>
    /// A node with an empty routing table, or one announcing only its own
    /// presence, can emit a header-only broadcast. The canonical appendix frames
    /// the entry list as "repeated up to 11 times" — i.e. zero is in range. Still
    /// a flag (default <c>true</c>) so a caller that wants to treat a contentless
    /// broadcast as malformed can opt out.
    /// </remarks>
    public bool AllowEmptyDestinationList { get; init; } = true;

    /// <summary>
    /// Strict canonical NET/ROM — every accommodation disabled. A broadcast is
    /// accepted only if its routing-info region is an exact multiple of 21 bytes
    /// and contains at least one destination entry.
    /// </summary>
    public static NetRomParseOptions Strict { get; } = new()
    {
        AllowTrailingPartialEntry = false,
        AllowEmptyDestinationList = false,
    };

    /// <summary>
    /// Accept-everything mode (the kitchen sink). All currently-known
    /// accommodations enabled. The parameterless <see cref="NodesBroadcast.TryParse"/>
    /// overload uses this — read-only promiscuous ingest wants to be forgiving.
    /// </summary>
    public static NetRomParseOptions Lenient { get; } = new();

    /// <summary>
    /// BPQ / LinBPQ-flavoured leniency (the de-facto reference node). Today the
    /// same instance as <see cref="Lenient"/>; kept named so a future BPQ-specific
    /// quirk lands here without churning call sites.
    /// </summary>
    public static NetRomParseOptions Bpq { get; } = Lenient;

    /// <summary>
    /// XRouter-flavoured leniency (Paula G8PZT). Today identical to
    /// <see cref="Lenient"/>. XRouter's notable divergence is the <em>quality</em>
    /// it advertises (its RTT→quality conversion is deliberately lower — the
    /// "British notion of quality"), which is a routing-table concern handled in
    /// <see cref="Packet.NetRom.Routing"/>, not a wire-parse concern — the bytes
    /// still parse identically.
    /// </summary>
    public static NetRomParseOptions Xrouter { get; } = Lenient;
}
