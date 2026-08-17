using System.Collections.Concurrent;
using Packet.Node.Core.Configuration;

namespace Packet.Node.Core.Capabilities;

/// <summary>
/// Remembers, per neighbour, whether it supports v2.2/SABME (<see cref="PeerCapabilityRecord.SupportsExtended"/>)
/// and whether it answers a pre-session XID (<see cref="PeerCapabilityRecord.SupportsSrejViaXid"/>), so a
/// dial can skip probes a known non-answerer would only stall on, and re-probe a negative after ~30 days.
/// </summary>
/// <remarks>
/// <para>
/// The dial decision is <see cref="PlanDial"/>; the post-dial learning is
/// <see cref="RecordOutcome"/>. A hot <see cref="ConcurrentDictionary{TKey,TValue}"/> keyed by the
/// (port, peer) pair is hydrated from <see cref="IPeerCapabilityStore.All"/> on construction and written
/// through on every update, so reads (the dial hot path) never touch the database.
/// </para>
/// <para>
/// The store is <b>optional</b> (mirroring <see cref="NetRom.NetRomService"/>): a null store ⇒ in-memory
/// only — the cache still works for the run, it just doesn't survive a restart. That keeps tests and
/// embedders that don't supply a <c>pdn.db</c> unaffected.
/// </para>
/// </remarks>
public sealed class PeerCapabilityCache
{
    /// <summary>A learned negative is re-probed after this long, in case the peer (or its firmware) changed.</summary>
    public static readonly TimeSpan StaleAfter = TimeSpan.FromDays(30);

    private readonly IPeerCapabilityStore? store;
    private readonly TimeProvider time;
    private readonly ConcurrentDictionary<(string PortId, string Peer), PeerCapabilityRecord> hot = new();

    /// <summary>Build the cache over an optional <paramref name="store"/> (null ⇒ in-memory only) and an
    /// optional <paramref name="time"/> source (default <see cref="TimeProvider.System"/>). Hydrates the
    /// hot dictionary from the store on construction.</summary>
    public PeerCapabilityCache(IPeerCapabilityStore? store = null, TimeProvider? time = null)
    {
        this.store = store;
        this.time = time ?? TimeProvider.System;

        if (store is not null)
        {
            foreach (var rec in store.All())
            {
                hot[(rec.PortId, rec.Peer)] = rec;
            }
        }
    }

    /// <summary>
    /// Decide how to dial <paramref name="peer"/> on <paramref name="portId"/> with no declared per-port
    /// link policy (all-auto). A miss or a stale record falls back to the optimistic
    /// <paramref name="policy"/> default; a fresh learned positive is honoured (offer SABME); a fresh
    /// learned negative is skipped (mod-8, and skip the pre-connect XID if the peer is a known
    /// non-answerer).
    /// </summary>
    public PeerDialPlan PlanDial(string portId, string peer, PeerDialPolicy policy)
        => PlanDial(portId, peer, policy, PortLinkConfig.Default);

    /// <summary>
    /// Decide how to dial <paramref name="peer"/> on <paramref name="portId"/>, honouring the port's
    /// <b>declared</b> link policy first and what has been learned about the peer second.
    /// </summary>
    /// <remarks>
    /// The precedence is the whole point of the port's policy: an operator who says
    /// <c>link.dial: v20</c> has told us this port's stations are v2.0, so no learned or optimistic
    /// SABME is offered on it; <c>v22</c> pins the other way and never degrades. Only
    /// <see cref="LinkDialPreference.Auto"/> leaves the decision to the cache - which is where every
    /// port started before the policy existed, so a null/absent <c>link:</c> block is byte-for-byte
    /// today's behaviour.
    /// </remarks>
    /// <param name="portId">The port the dial will use.</param>
    /// <param name="peer">The neighbour to dial.</param>
    /// <param name="policy">Why we are dialling (sets the optimistic default under <c>auto</c>).</param>
    /// <param name="link">The port's declared link policy (null ⇒ all-auto).</param>
    public PeerDialPlan PlanDial(string portId, string peer, PeerDialPolicy policy, PortLinkConfig? link)
    {
        ArgumentNullException.ThrowIfNull(portId);
        ArgumentNullException.ThrowIfNull(peer);

        var rec = Lookup(portId, peer);
        return Plan(
            policy,
            link,
            learnedExtended: Fresh(rec, rec?.SupportsExtended) ? rec!.SupportsExtended : null,
            learnedSrejViaXid: Fresh(rec, rec?.SupportsSrejViaXid) ? rec!.SupportsSrejViaXid : null);
    }

    /// <summary>
    /// The plan a node with <b>no</b> capability cache dials - the port's declared policy over the
    /// bare <paramref name="policy"/> defaults. Shared with <see cref="PlanDial(string, string,
    /// PeerDialPolicy, PortLinkConfig?)"/> so a cache-less path (an embedder with no <c>pdn.db</c>,
    /// or the NET/ROM service before a cache is wired) can never disagree with a cached one about
    /// what a declared policy means.
    /// </summary>
    public static PeerDialPlan PlanWithoutCache(PeerDialPolicy policy, PortLinkConfig? link)
        => Plan(policy, link, learnedExtended: null, learnedSrejViaXid: null);

    /// <summary>
    /// The pre-connect-XID decision alone, for a dial whose version is <b>already settled</b> as
    /// mod-8 - the v2.0 retry the connector makes after a SABME drew no answer. Going back through
    /// <see cref="PlanDial(string, string, PeerDialPolicy, PortLinkConfig?)"/> would re-decide the
    /// version (and, on the extended branch, report the XID probe as the moot <c>false</c>), so the
    /// two decisions are separable here and share one rule.
    /// </summary>
    public bool PlanPreConnectXid(string portId, string peer, PortLinkConfig? link)
    {
        ArgumentNullException.ThrowIfNull(portId);
        ArgumentNullException.ThrowIfNull(peer);

        var rec = Lookup(portId, peer);
        return PreConnectXidFor(link, Fresh(rec, rec?.SupportsSrejViaXid) ? rec!.SupportsSrejViaXid : null);
    }

    /// <summary>The cache-less form of <see cref="PlanPreConnectXid"/> - the port's declaration
    /// with nothing learned to refine it.</summary>
    public static bool PlanPreConnectXidWithoutCache(PortLinkConfig? link)
        => PreConnectXidFor(link, learnedSrejViaXid: null);

    // The one dial decision. `learned*` are the FRESH learned values (null = nothing usable
    // learned in this dimension), so freshness/staleness is settled by the caller and this stays
    // a pure function of (why we dial, what the port declares, what we know).
    private static PeerDialPlan Plan(
        PeerDialPolicy policy, PortLinkConfig? link, bool? learnedExtended, bool? learnedSrejViaXid)
    {
        var declared = PortLinkConfig.Resolve(link);

        // Extended: the port's declaration wins outright; under `auto`, a fresh learned answer
        // wins; otherwise the reason-for-dialling default (UserConnect offers SABME, Interlink
        // stays mod-8 until proven extended - a stalled SABME costs the whole backbone a retry
        // cycle). NET/ROM interlinks therefore stay mod-8 unless the PORT says v22.
        bool extended = declared.Dial switch
        {
            LinkDialPreference.V22 => true,
            LinkDialPreference.V20 => false,
            _ => learnedExtended ?? (policy == PeerDialPolicy.UserConnect),
        };

        // Pre-connect XID: moot on the extended path (XID negotiation rides the SABME setup).
        bool preConnectXid = !extended && PreConnectXidFor(link, learnedSrejViaXid);

        return new PeerDialPlan(extended, preConnectXid);
    }

    // The pre-connect-XID rule for a MOD-8 dial: the port's declaration wins, then the learned
    // answer - send the XID unless we have freshly learned this peer does NOT answer one.
    private static bool PreConnectXidFor(PortLinkConfig? link, bool? learnedSrejViaXid)
        => PortLinkConfig.Resolve(link).PreConnectXid switch
        {
            LinkPreConnectXid.On => true,
            LinkPreConnectXid.Off => false,
            _ => learnedSrejViaXid != false,
        };

    /// <summary>
    /// Record what a returned dial observed. <b>Plan-aware</b>: a dimension is only updated when the dial
    /// actually probed it — a mod-8 dial proves nothing about extended capability, so it leaves
    /// <see cref="PeerCapabilityRecord.SupportsExtended"/> untouched; a dial that sent no pre-connect XID
    /// leaves <see cref="PeerCapabilityRecord.SupportsSrejViaXid"/> untouched. The unprobed dimension is
    /// preserved from the existing record.
    /// </summary>
    /// <param name="portId">The port the dial used.</param>
    /// <param name="peer">The neighbour dialled.</param>
    /// <param name="dialedExtended">Whether the dial offered SABME (extended setup).</param>
    /// <param name="observedIsExtended">Whether the resulting link is extended (true = capable, false =
    /// peer refused / degraded to mod-8). Only meaningful when <paramref name="dialedExtended"/>.</param>
    /// <param name="dialedPreConnectXid">Whether the dial sent a pre-connect XID.</param>
    /// <param name="observedSrejEnabled">Whether the XID exchange enabled SREJ. Only meaningful when
    /// <paramref name="dialedPreConnectXid"/>.</param>
    public void RecordOutcome(
        string portId,
        string peer,
        bool dialedExtended,
        bool observedIsExtended,
        bool dialedPreConnectXid,
        bool observedSrejEnabled)
    {
        ArgumentNullException.ThrowIfNull(portId);
        ArgumentNullException.ThrowIfNull(peer);

        var now = time.GetUtcNow();
        var existing = Lookup(portId, peer);

        // Only learn a dimension we actually probed; otherwise carry the prior value forward.
        bool? supportsExtended = dialedExtended ? observedIsExtended : existing?.SupportsExtended;
        bool? supportsSrejViaXid = dialedPreConnectXid ? observedSrejEnabled : existing?.SupportsSrejViaXid;

        // LastRefused stamps an extended degrade (we offered SABME, peer came back mod-8); else carry forward.
        DateTimeOffset? lastRefused = (dialedExtended && !observedIsExtended) ? now : existing?.LastRefused;

        var updated = new PeerCapabilityRecord(
            portId, peer, supportsExtended, supportsSrejViaXid, now, lastRefused);

        hot[(portId, peer)] = updated;
        store?.Upsert(updated);
    }

    /// <summary>
    /// Record that an extended (SABME) dial to <paramref name="peer"/> on <paramref name="portId"/>
    /// drew <b>no answer at all</b>: the dial failed without that station sending back a single
    /// frame - no UA, no DM, no FRMR. This is the negative the cache previously could not learn:
    /// only a RETURNED dial reached <see cref="RecordOutcome"/>, so a peer that ignores SABME rather
    /// than rejecting it was dialled with SABME on every attempt forever (the GB7RDG cutover
    /// signature - 44 SABMEs, 0 SABMs on air). The caller
    /// (<c>Ax25OutboundConnector</c>) establishes the "not a single frame" part by watching the
    /// port's frame trace across the dial; the exception type alone cannot, because the SDL's own
    /// give-up at RC == N2 surfaces as the same teardown a DM refusal does.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Only the extended dimension is written; <see cref="PeerCapabilityRecord.SupportsSrejViaXid"/>
    /// is carried forward untouched (a SABME dial sends no pre-connect XID, so it probes nothing
    /// there). <see cref="PeerCapabilityRecord.LastRefused"/> is stamped, as for a degrade.
    /// </para>
    /// <para>
    /// <b>The inference this makes, deliberately.</b> "No answer" is equally explained by "the
    /// station is off air", so the negative is not a proof that the peer dislikes SABME. It is
    /// recorded anyway because the ACTION it drives is cheap and safe - the next dial leads with a
    /// SABM, which every v2.2 peer also answers - while the alternative (re-probing SABME every
    /// time) costs a full connect budget on every attempt to a silent peer. The cost is bounded by
    /// <see cref="StaleAfter"/>: a genuinely v2.2 peer that happened to be off air is re-probed
    /// after ~30 days, and a port that must never degrade sets <c>link.dial: v22</c>, under which
    /// this is never called.
    /// </para>
    /// </remarks>
    public void RecordSilentToExtended(string portId, string peer)
    {
        ArgumentNullException.ThrowIfNull(portId);
        ArgumentNullException.ThrowIfNull(peer);

        var now = time.GetUtcNow();
        var existing = Lookup(portId, peer);

        var updated = new PeerCapabilityRecord(
            portId, peer,
            SupportsExtended: false,
            SupportsSrejViaXid: existing?.SupportsSrejViaXid,
            LastProbed: now,
            LastRefused: now);

        hot[(portId, peer)] = updated;
        store?.Upsert(updated);
    }

    /// <summary>Every cached record (operator surface for later phases).</summary>
    public IReadOnlyList<PeerCapabilityRecord> All() => hot.Values.ToList();

    /// <summary>Forget one (port, peer) — clears the store and the hot dictionary. Returns whether the hot
    /// entry was present (the store delete is best-effort).</summary>
    public bool Forget(string portId, string peer)
    {
        ArgumentNullException.ThrowIfNull(portId);
        ArgumentNullException.ThrowIfNull(peer);

        store?.Clear(portId, peer);
        return hot.TryRemove((portId, peer), out _);
    }

    private PeerCapabilityRecord? Lookup(string portId, string peer) =>
        hot.TryGetValue((portId, peer), out var rec) ? rec : null;

    // A learned dimension is fresh when the record exists, that dimension has a value, and the record was
    // probed within the staleness window. A null dimension (never probed) is never "fresh".
    private bool Fresh(PeerCapabilityRecord? rec, bool? dimension) =>
        rec is not null && dimension.HasValue && (time.GetUtcNow() - rec.LastProbed) < StaleAfter;
}
