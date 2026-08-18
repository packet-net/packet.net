using Packet.Node.Core.Capabilities;
using Packet.Node.Tests.Support;

namespace Packet.Node.Tests.Capabilities;

/// <summary>
/// Round-trips the SQLite peer-capability store on a temp db: upsert/find, the
/// INSERT..ON CONFLICT update path, the nullable bool? ⇔ nullable INTEGER mapping (including
/// the never-probed null), All(), Clear/ClearAll, persistence across a reopen, and the
/// degrade-not-throw resilience of a broken store. Same shape as the refresh-token store test.
/// </summary>
[Trait("Category", "Node")]
public sealed class SqlitePeerCapabilityStoreTests : IDisposable
{
    private static readonly DateTimeOffset Probed = new(2026, 6, 1, 12, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Refused = new(2026, 6, 1, 12, 5, 0, TimeSpan.Zero);

    private readonly string dir;
    private readonly string dbPath;

    public SqlitePeerCapabilityStoreTests()
    {
        dir = TestPaths.NewPath("packetnet-pcstore");
        Directory.CreateDirectory(dir);
        dbPath = Path.Combine(dir, "pdn.db");
    }

    private SqlitePeerCapabilityStore Open() => new(dbPath);

    private static PeerCapabilityRecord Rec(
        string port, string peer, bool? ext, bool? srej,
        DateTimeOffset? refused = null) =>
        new(port, peer, ext, srej, Probed, refused);

    [Fact]
    public void Upsert_then_find_round_trips_all_fields()
    {
        var store = Open();
        store.Upsert(Rec("vhf0", "GB7RDG-7", ext: true, srej: false, refused: Refused));

        var found = store.Find("vhf0", "GB7RDG-7");
        found.Should().NotBeNull();
        found!.PortId.Should().Be("vhf0");
        found.Peer.Should().Be("GB7RDG-7");
        found.SupportsExtended.Should().BeTrue();
        found.SupportsSrejViaXid.Should().BeFalse();
        found.LastProbed.Should().Be(Probed);
        found.LastRefused.Should().Be(Refused);

        store.Find("vhf0", "absent").Should().BeNull();
        store.Find("other", "GB7RDG-7").Should().BeNull();   // keyed per (port, peer)
    }

    [Fact]
    public void Null_dimensions_and_null_refused_round_trip_as_null()
    {
        var store = Open();
        store.Upsert(Rec("vhf0", "M0LTE", ext: null, srej: null, refused: null));

        var found = store.Find("vhf0", "M0LTE")!;
        found.SupportsExtended.Should().BeNull();    // never-probed extended
        found.SupportsSrejViaXid.Should().BeNull();  // never-probed XID
        found.LastRefused.Should().BeNull();
    }

    [Fact]
    public void False_dimension_round_trips_distinctly_from_null()
    {
        var store = Open();
        store.Upsert(Rec("vhf0", "M0LTE", ext: false, srej: false, refused: null));

        var found = store.Find("vhf0", "M0LTE")!;
        found.SupportsExtended.Should().BeFalse();   // learned-false, not unknown
        found.SupportsSrejViaXid.Should().BeFalse();
    }

    [Fact]
    public void Upsert_on_conflict_updates_the_existing_row()
    {
        var store = Open();
        store.Upsert(Rec("vhf0", "GB7RDG-7", ext: false, srej: null, refused: Refused));

        // Same key, new values - must UPDATE, not duplicate.
        var laterProbe = Probed + TimeSpan.FromDays(1);
        store.Upsert(new PeerCapabilityRecord("vhf0", "GB7RDG-7",
            SupportsExtended: true, SupportsSrejViaXid: true, LastProbed: laterProbe, LastRefused: null));

        store.All().Should().HaveCount(1);
        var found = store.Find("vhf0", "GB7RDG-7")!;
        found.SupportsExtended.Should().BeTrue();
        found.SupportsSrejViaXid.Should().BeTrue();
        found.LastProbed.Should().Be(laterProbe);
        found.LastRefused.Should().BeNull();
    }

    [Fact]
    public void All_returns_every_row()
    {
        var store = Open();
        store.Upsert(Rec("vhf0", "GB7RDG-7", ext: true, srej: null));
        store.Upsert(Rec("hf0", "GB7RDG-7", ext: false, srej: true));
        store.Upsert(Rec("vhf0", "M0LTE", ext: null, srej: null));

        store.All().Should().HaveCount(3);
    }

    [Fact]
    public void Clear_removes_one_row_and_returns_whether_it_existed()
    {
        var store = Open();
        store.Upsert(Rec("vhf0", "GB7RDG-7", ext: true, srej: null));
        store.Upsert(Rec("hf0", "GB7RDG-7", ext: false, srej: null));

        store.Clear("vhf0", "GB7RDG-7").Should().BeTrue();
        store.Find("vhf0", "GB7RDG-7").Should().BeNull();
        store.Find("hf0", "GB7RDG-7").Should().NotBeNull();   // sibling untouched

        store.Clear("vhf0", "GB7RDG-7").Should().BeFalse();    // already gone
    }

    [Fact]
    public void ClearAll_removes_everything_and_returns_the_count()
    {
        var store = Open();
        store.Upsert(Rec("vhf0", "GB7RDG-7", ext: true, srej: null));
        store.Upsert(Rec("hf0", "M0LTE", ext: false, srej: null));

        store.ClearAll().Should().Be(2);
        store.All().Should().BeEmpty();
        store.ClearAll().Should().Be(0);   // idempotent
    }

    [Fact]
    public void Data_persists_across_a_reopen()
    {
        Open().Upsert(Rec("vhf0", "GB7RDG-7", ext: true, srej: false, refused: Refused));

        var reopened = Open();
        var found = reopened.Find("vhf0", "GB7RDG-7")!;
        found.SupportsExtended.Should().BeTrue();
        found.SupportsSrejViaXid.Should().BeFalse();
        found.LastRefused.Should().Be(Refused);
    }

    [Fact]
    public void A_broken_store_degrades_and_never_throws()
    {
        // A db path under a non-existent directory can't be opened → schema init logs + degrades,
        // and every op returns the safe default rather than throwing.
        var broken = new SqlitePeerCapabilityStore(Path.Combine(dir, "no-such-dir", "pdn.db"));

        broken.Invoking(b => b.Upsert(Rec("vhf0", "GB7RDG-7", ext: true, srej: null)))
            .Should().NotThrow();
        broken.Find("vhf0", "GB7RDG-7").Should().BeNull();
        broken.All().Should().BeEmpty();
        broken.Clear("vhf0", "GB7RDG-7").Should().BeFalse();
        broken.ClearAll().Should().Be(0);
    }

    public void Dispose()
    {
        try { Directory.Delete(dir, recursive: true); } catch { /* best effort */ }
    }
}
