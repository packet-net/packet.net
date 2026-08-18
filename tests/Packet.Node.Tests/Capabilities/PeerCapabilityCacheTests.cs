using Microsoft.Extensions.Time.Testing;
using Packet.Node.Core.Capabilities;

namespace Packet.Node.Tests.Capabilities;

/// <summary>
/// The capability cache decision + learning logic on a <see cref="FakeTimeProvider"/> (no
/// wall-clock, repo rule §2.7). Asserts the <see cref="PeerCapabilityCache.PlanDial"/> table -
/// miss ⇒ policy default, fresh positive honoured, fresh negative skipped, stale re-probed - and
/// the plan-aware <see cref="PeerCapabilityCache.RecordOutcome"/> learning that is the correctness
/// hinge: a dimension is only learned when the dial actually probed it.
/// </summary>
[Trait("Category", "Node")]
public sealed class PeerCapabilityCacheTests
{
    private const string Port = "vhf0";
    private const string Peer = "GB7RDG-7";
    private static readonly DateTimeOffset T0 = new(2026, 6, 1, 12, 0, 0, TimeSpan.Zero);

    private static FakeTimeProvider Clock() => new(T0);

    // --- PlanDial: miss ⇒ optimistic policy default ----------------------------------------

    [Fact]
    public void PlanDial_miss_user_connect_offers_sabme_and_no_xid()
    {
        var cache = new PeerCapabilityCache(store: null, time: Clock());

        var plan = cache.PlanDial(Port, Peer, PeerDialPolicy.UserConnect);

        plan.Extended.Should().BeTrue();        // optimistic: offer SABME
        plan.PreConnectXid.Should().BeFalse();  // moot on the extended path
    }

    [Fact]
    public void PlanDial_miss_interlink_stays_mod8_and_sends_xid()
    {
        var cache = new PeerCapabilityCache(store: null, time: Clock());

        var plan = cache.PlanDial(Port, Peer, PeerDialPolicy.Interlink);

        plan.Extended.Should().BeFalse();       // conservative: mod-8 until proven extended
        plan.PreConnectXid.Should().BeTrue();   // and probe SREJ via a pre-connect XID
    }

    // --- PlanDial: fresh learned answers -----------------------------------------------------

    [Fact]
    public void PlanDial_fresh_positive_extended_is_honoured_even_for_interlink()
    {
        var clock = Clock();
        var cache = new PeerCapabilityCache(store: null, time: clock);
        // Learn extended via a returned SABME dial.
        cache.RecordOutcome(Port, Peer, dialedExtended: true, observedIsExtended: true,
            dialedPreConnectXid: false, observedSrejEnabled: false);

        var plan = cache.PlanDial(Port, Peer, PeerDialPolicy.Interlink);

        plan.Extended.Should().BeTrue();        // known-extended ⇒ SABME despite the conservative policy
        plan.PreConnectXid.Should().BeFalse();  // moot on the extended path
    }

    [Fact]
    public void PlanDial_fresh_negative_extended_skips_sabme_even_for_user_connect()
    {
        var clock = Clock();
        var cache = new PeerCapabilityCache(store: null, time: clock);
        // Learn non-extended: we offered SABME, peer degraded to mod-8.
        cache.RecordOutcome(Port, Peer, dialedExtended: true, observedIsExtended: false,
            dialedPreConnectXid: false, observedSrejEnabled: false);

        var plan = cache.PlanDial(Port, Peer, PeerDialPolicy.UserConnect);

        plan.Extended.Should().BeFalse();       // known-non-extended ⇒ mod-8 despite the optimistic policy
        plan.PreConnectXid.Should().BeTrue();   // unknown XID answerer ⇒ still probe
    }

    [Fact]
    public void PlanDial_fresh_non_xid_answerer_skips_the_pre_connect_xid()
    {
        var clock = Clock();
        var cache = new PeerCapabilityCache(store: null, time: clock);
        // Mod-8 dial that probed XID and learned the peer does NOT answer it.
        cache.RecordOutcome(Port, Peer, dialedExtended: false, observedIsExtended: false,
            dialedPreConnectXid: true, observedSrejEnabled: false);

        var plan = cache.PlanDial(Port, Peer, PeerDialPolicy.Interlink);

        plan.Extended.Should().BeFalse();
        plan.PreConnectXid.Should().BeFalse();  // known non-answerer ⇒ skip the stall
    }

    [Fact]
    public void PlanDial_fresh_xid_answerer_still_sends_the_pre_connect_xid()
    {
        var clock = Clock();
        var cache = new PeerCapabilityCache(store: null, time: clock);
        // Mod-8 dial that probed XID and learned the peer DOES answer it (SREJ enabled).
        cache.RecordOutcome(Port, Peer, dialedExtended: false, observedIsExtended: false,
            dialedPreConnectXid: true, observedSrejEnabled: true);

        var plan = cache.PlanDial(Port, Peer, PeerDialPolicy.Interlink);

        plan.Extended.Should().BeFalse();
        plan.PreConnectXid.Should().BeTrue();   // a positive answerer ⇒ keep probing (it's cheap + useful)
    }

    // --- PlanDial: staleness re-probe --------------------------------------------------------

    [Fact]
    public void PlanDial_stale_negative_re_probes_with_the_policy_default()
    {
        var clock = Clock();
        var cache = new PeerCapabilityCache(store: null, time: clock);
        // Learn non-extended now...
        cache.RecordOutcome(Port, Peer, dialedExtended: true, observedIsExtended: false,
            dialedPreConnectXid: false, observedSrejEnabled: false);

        // ...let it go stale (> 30 days).
        clock.Advance(PeerCapabilityCache.StaleAfter + TimeSpan.FromDays(1));

        var plan = cache.PlanDial(Port, Peer, PeerDialPolicy.UserConnect);

        plan.Extended.Should().BeTrue();        // stale ⇒ fall back to the optimistic default (re-probe)
    }

    [Fact]
    public void PlanDial_just_inside_the_window_is_still_fresh()
    {
        var clock = Clock();
        var cache = new PeerCapabilityCache(store: null, time: clock);
        cache.RecordOutcome(Port, Peer, dialedExtended: true, observedIsExtended: false,
            dialedPreConnectXid: false, observedSrejEnabled: false);

        clock.Advance(PeerCapabilityCache.StaleAfter - TimeSpan.FromMinutes(1));

        cache.PlanDial(Port, Peer, PeerDialPolicy.UserConnect).Extended.Should().BeFalse(); // still honoured
    }

    // --- RecordOutcome: PLAN-AWARE learning (the correctness hinge) ---------------------------

    [Fact]
    public void RecordOutcome_dialed_extended_sets_supports_extended()
    {
        var cache = new PeerCapabilityCache(store: null, time: Clock());

        cache.RecordOutcome(Port, Peer, dialedExtended: true, observedIsExtended: true,
            dialedPreConnectXid: false, observedSrejEnabled: false);

        var rec = cache.All().Single();
        rec.SupportsExtended.Should().BeTrue();
        rec.SupportsSrejViaXid.Should().BeNull();   // never probed ⇒ stays null
    }

    [Fact]
    public void RecordOutcome_dialed_mod8_does_NOT_touch_supports_extended()
    {
        var clock = Clock();
        var cache = new PeerCapabilityCache(store: null, time: clock);
        // First a SABME dial learns extended = true.
        cache.RecordOutcome(Port, Peer, dialedExtended: true, observedIsExtended: true,
            dialedPreConnectXid: false, observedSrejEnabled: false);

        // A later mod-8 dial (dialedExtended:false) must NOT overwrite the learned extended bit -
        // a mod-8 dial proves nothing about extended capability.
        clock.Advance(TimeSpan.FromMinutes(5));
        cache.RecordOutcome(Port, Peer, dialedExtended: false, observedIsExtended: false,
            dialedPreConnectXid: false, observedSrejEnabled: false);

        cache.All().Single().SupportsExtended.Should().BeTrue();   // preserved
    }

    [Fact]
    public void RecordOutcome_dialed_mod8_starting_from_unknown_leaves_extended_null()
    {
        var cache = new PeerCapabilityCache(store: null, time: Clock());

        cache.RecordOutcome(Port, Peer, dialedExtended: false, observedIsExtended: false,
            dialedPreConnectXid: false, observedSrejEnabled: false);

        cache.All().Single().SupportsExtended.Should().BeNull();   // never learned ⇒ stays unknown
    }

    [Fact]
    public void RecordOutcome_dialed_xid_sets_supports_srej_via_xid()
    {
        var cache = new PeerCapabilityCache(store: null, time: Clock());

        cache.RecordOutcome(Port, Peer, dialedExtended: false, observedIsExtended: false,
            dialedPreConnectXid: true, observedSrejEnabled: true);

        var rec = cache.All().Single();
        rec.SupportsSrejViaXid.Should().BeTrue();
        rec.SupportsExtended.Should().BeNull();     // never probed ⇒ stays null
    }

    [Fact]
    public void RecordOutcome_no_xid_does_NOT_touch_supports_srej_via_xid()
    {
        var clock = Clock();
        var cache = new PeerCapabilityCache(store: null, time: clock);
        // Learn SREJ via XID first.
        cache.RecordOutcome(Port, Peer, dialedExtended: false, observedIsExtended: false,
            dialedPreConnectXid: true, observedSrejEnabled: true);

        // A later dial that sends no pre-connect XID must preserve the learned XID dimension.
        clock.Advance(TimeSpan.FromMinutes(5));
        cache.RecordOutcome(Port, Peer, dialedExtended: true, observedIsExtended: true,
            dialedPreConnectXid: false, observedSrejEnabled: false);

        var rec = cache.All().Single();
        rec.SupportsSrejViaXid.Should().BeTrue();   // preserved
        rec.SupportsExtended.Should().BeTrue();     // the extended dimension this dial probed
    }

    [Fact]
    public void RecordOutcome_sets_last_refused_on_an_extended_degrade()
    {
        var clock = Clock();
        var cache = new PeerCapabilityCache(store: null, time: clock);

        cache.RecordOutcome(Port, Peer, dialedExtended: true, observedIsExtended: false,
            dialedPreConnectXid: false, observedSrejEnabled: false);

        cache.All().Single().LastRefused.Should().Be(T0);
    }

    [Fact]
    public void RecordOutcome_does_not_set_last_refused_on_a_clean_extended_dial()
    {
        var cache = new PeerCapabilityCache(store: null, time: Clock());

        cache.RecordOutcome(Port, Peer, dialedExtended: true, observedIsExtended: true,
            dialedPreConnectXid: false, observedSrejEnabled: false);

        cache.All().Single().LastRefused.Should().BeNull();
    }

    [Fact]
    public void RecordOutcome_carries_last_refused_forward_on_a_non_degrade_dial()
    {
        var clock = Clock();
        var cache = new PeerCapabilityCache(store: null, time: clock);
        // Degrade stamps LastRefused = T0.
        cache.RecordOutcome(Port, Peer, dialedExtended: true, observedIsExtended: false,
            dialedPreConnectXid: false, observedSrejEnabled: false);

        // A later mod-8 dial (not an extended degrade) must carry the prior stamp forward.
        clock.Advance(TimeSpan.FromMinutes(5));
        cache.RecordOutcome(Port, Peer, dialedExtended: false, observedIsExtended: false,
            dialedPreConnectXid: true, observedSrejEnabled: true);

        cache.All().Single().LastRefused.Should().Be(T0);   // carried forward, not cleared
    }

    [Fact]
    public void RecordOutcome_stamps_last_probed_with_the_clock()
    {
        var clock = Clock();
        var cache = new PeerCapabilityCache(store: null, time: clock);
        clock.Advance(TimeSpan.FromHours(3));

        cache.RecordOutcome(Port, Peer, dialedExtended: true, observedIsExtended: true,
            dialedPreConnectXid: false, observedSrejEnabled: false);

        cache.All().Single().LastProbed.Should().Be(T0 + TimeSpan.FromHours(3));
    }

    // --- per-link keying + Forget ------------------------------------------------------------

    [Fact]
    public void Records_are_keyed_per_port_and_peer()
    {
        var cache = new PeerCapabilityCache(store: null, time: Clock());
        cache.RecordOutcome("vhf0", Peer, dialedExtended: true, observedIsExtended: true,
            dialedPreConnectXid: false, observedSrejEnabled: false);
        cache.RecordOutcome("hf0", Peer, dialedExtended: true, observedIsExtended: false,
            dialedPreConnectXid: false, observedSrejEnabled: false);

        cache.All().Should().HaveCount(2);
        cache.PlanDial("vhf0", Peer, PeerDialPolicy.Interlink).Extended.Should().BeTrue();
        cache.PlanDial("hf0", Peer, PeerDialPolicy.UserConnect).Extended.Should().BeFalse();
    }

    [Fact]
    public void Forget_removes_the_hot_entry()
    {
        var cache = new PeerCapabilityCache(store: null, time: Clock());
        cache.RecordOutcome(Port, Peer, dialedExtended: true, observedIsExtended: true,
            dialedPreConnectXid: false, observedSrejEnabled: false);

        cache.Forget(Port, Peer).Should().BeTrue();
        cache.All().Should().BeEmpty();
        cache.Forget(Port, Peer).Should().BeFalse();   // already gone
    }

    // --- store integration: hydrate + write-through ------------------------------------------

    [Fact]
    public void Construction_hydrates_the_hot_cache_from_the_store()
    {
        var store = new FakeStore();
        store.Upsert(new PeerCapabilityRecord(Port, Peer, SupportsExtended: true,
            SupportsSrejViaXid: null, LastProbed: T0, LastRefused: null));

        var cache = new PeerCapabilityCache(store, Clock());

        // The hydrated positive is honoured without any further probe.
        cache.PlanDial(Port, Peer, PeerDialPolicy.Interlink).Extended.Should().BeTrue();
    }

    [Fact]
    public void RecordOutcome_writes_through_to_the_store()
    {
        var store = new FakeStore();
        var cache = new PeerCapabilityCache(store, Clock());

        cache.RecordOutcome(Port, Peer, dialedExtended: true, observedIsExtended: true,
            dialedPreConnectXid: false, observedSrejEnabled: false);

        store.Find(Port, Peer)!.SupportsExtended.Should().BeTrue();   // persisted
    }

    [Fact]
    public void Forget_clears_the_store_too()
    {
        var store = new FakeStore();
        var cache = new PeerCapabilityCache(store, Clock());
        cache.RecordOutcome(Port, Peer, dialedExtended: true, observedIsExtended: true,
            dialedPreConnectXid: false, observedSrejEnabled: false);

        cache.Forget(Port, Peer);

        store.Find(Port, Peer).Should().BeNull();
    }

    // A trivial in-memory IPeerCapabilityStore so the cache integration can be asserted without
    // a database (the SQLite round-trip is covered by SqlitePeerCapabilityStoreTests).
    private sealed class FakeStore : IPeerCapabilityStore
    {
        private readonly Dictionary<(string, string), PeerCapabilityRecord> rows = new();

        public void Upsert(PeerCapabilityRecord record) => rows[(record.PortId, record.Peer)] = record;

        public PeerCapabilityRecord? Find(string portId, string peer) =>
            rows.TryGetValue((portId, peer), out var r) ? r : null;

        public IReadOnlyList<PeerCapabilityRecord> All() => rows.Values.ToList();

        public bool Clear(string portId, string peer) => rows.Remove((portId, peer));

        public int ClearAll()
        {
            int n = rows.Count;
            rows.Clear();
            return n;
        }
    }
}
