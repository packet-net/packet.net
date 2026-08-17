using Packet.Node.Core.Heard;

namespace Packet.Node.Tests.Heard;

/// <summary>
/// Round-trips the SQLite heard store (#454) on a temp db: upsert/All, the INSERT..ON CONFLICT
/// update path, Clear/ClearAll, the Prune age-out + per-port cap, the UTC normalisation those two
/// queries depend on, persistence across a reopen, and the degrade-not-throw resilience of a broken
/// store. Same shape as the capability-store test.
/// </summary>
[Trait("Category", "Node")]
public sealed class SqliteHeardStoreTests : IDisposable
{
    private static readonly DateTimeOffset T0 = new(2026, 6, 1, 12, 0, 0, TimeSpan.Zero);

    private readonly string dir;
    private readonly string dbPath;

    public SqliteHeardStoreTests()
    {
        dir = Path.Combine(Path.GetTempPath(), "packetnet-heardstore-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        dbPath = Path.Combine(dir, "pdn.db");
    }

    private SqliteHeardStore Open() => new(dbPath);

    private static HeardEntry Entry(string port, string call, DateTimeOffset last, long count = 1, DateTimeOffset? first = null)
        => new(port, call, first ?? last, last, count);

    [Fact]
    public void Upsert_then_all_round_trips_all_fields()
    {
        var store = Open();
        store.Upsert(Entry("vhf", "M0LTE-1", T0, count: 3, first: T0.AddMinutes(-5)));

        var all = store.All();
        all.Should().ContainSingle();
        var e = all[0];
        e.PortId.Should().Be("vhf");
        e.Callsign.Should().Be("M0LTE-1");
        e.FirstHeard.Should().Be(T0.AddMinutes(-5));
        e.LastHeard.Should().Be(T0);
        e.Count.Should().Be(3);
    }

    [Fact]
    public void Upsert_on_conflict_updates_the_existing_row()
    {
        var store = Open();
        store.Upsert(Entry("vhf", "M0LTE-1", T0, count: 1));
        store.Upsert(Entry("vhf", "M0LTE-1", T0.AddMinutes(10), count: 2, first: T0));

        store.All().Should().ContainSingle();   // updated, not duplicated
        var e = store.All()[0];
        e.LastHeard.Should().Be(T0.AddMinutes(10));
        e.Count.Should().Be(2);
    }

    [Fact]
    public void Keyed_per_port_and_callsign()
    {
        var store = Open();
        store.Upsert(Entry("vhf", "M0LTE-1", T0));
        store.Upsert(Entry("hf", "M0LTE-1", T0));      // same call, different port → distinct row
        store.Upsert(Entry("vhf", "G0ABC", T0));

        store.All().Should().HaveCount(3);
    }

    [Fact]
    public void Clear_removes_one_row_and_returns_whether_it_existed()
    {
        var store = Open();
        store.Upsert(Entry("vhf", "M0LTE-1", T0));
        store.Upsert(Entry("hf", "M0LTE-1", T0));

        store.Clear("vhf", "M0LTE-1").Should().BeTrue();
        store.All().Should().ContainSingle(e => e.PortId == "hf");
        store.Clear("vhf", "M0LTE-1").Should().BeFalse();   // already gone
    }

    [Fact]
    public void ClearAll_removes_everything_and_returns_the_count()
    {
        var store = Open();
        store.Upsert(Entry("vhf", "M0LTE-1", T0));
        store.Upsert(Entry("hf", "G0ABC", T0));

        store.ClearAll().Should().Be(2);
        store.All().Should().BeEmpty();
        store.ClearAll().Should().Be(0);   // idempotent
    }

    [Fact]
    public void Prune_ages_out_rows_last_heard_before_the_cutoff()
    {
        var store = Open();
        store.Upsert(Entry("vhf", "OLD", T0.AddDays(-40)));     // stale
        store.Upsert(Entry("vhf", "FRESH", T0.AddDays(-1)));    // recent

        int removed = store.Prune(olderThan: T0.AddDays(-30), maxPerPort: 1000);

        removed.Should().Be(1);
        store.All().Should().ContainSingle(e => e.Callsign == "FRESH");
    }

    [Fact]
    public void Prune_caps_each_port_to_max_keeping_the_newest()
    {
        var store = Open();
        // Three on vhf (cap 2 → drop the oldest), one on hf (untouched).
        store.Upsert(Entry("vhf", "A", T0.AddMinutes(-30)));    // oldest on vhf → dropped
        store.Upsert(Entry("vhf", "B", T0.AddMinutes(-20)));
        store.Upsert(Entry("vhf", "C", T0.AddMinutes(-10)));    // newest on vhf
        store.Upsert(Entry("hf", "D", T0.AddMinutes(-60)));

        int removed = store.Prune(olderThan: T0.AddYears(-1), maxPerPort: 2);

        removed.Should().Be(1);
        store.All().Select(e => e.Callsign).Should().BeEquivalentTo(["B", "C", "D"]);
    }

    // last_heard_utc is a TEXT column that Prune range-compares (`< @cutoff`) and ranks
    // (`ORDER BY last_heard_utc DESC`) in SQL. Those only agree with chronological order if
    // every value is normalised to UTC before it is written, which is what SqliteStamps does.
    // The three below feed the store a caller-supplied non-UTC offset: with a raw offset
    // persisted, the stored digits are local wall-clock and both queries answer backwards.

    [Fact]
    public void Prune_ages_out_by_instant_not_by_the_offset_the_caller_used()
    {
        var store = Open();
        // 11:30Z written as 12:30+01:00: lexically LATER than the 12:00Z cutoff, actually earlier.
        store.Upsert(Entry("vhf", "STALE", new DateTimeOffset(2026, 6, 1, 12, 30, 0, TimeSpan.FromHours(1))));
        // 12:50Z written as 11:50-01:00: lexically EARLIER than the cutoff, actually later.
        store.Upsert(Entry("vhf", "FRESH", new DateTimeOffset(2026, 6, 1, 11, 50, 0, TimeSpan.FromHours(-1))));

        int removed = store.Prune(olderThan: T0, maxPerPort: 1000);   // T0 is 12:00Z

        removed.Should().Be(1);
        store.All().Should().ContainSingle(e => e.Callsign == "FRESH");
    }

    [Fact]
    public void Prune_caps_a_port_by_instant_across_mixed_offsets()
    {
        var store = Open();
        store.Upsert(Entry("vhf", "OLDEST", new DateTimeOffset(2026, 6, 1, 12, 30, 0, TimeSpan.FromHours(1))));    // 11:30Z
        store.Upsert(Entry("vhf", "MIDDLE", new DateTimeOffset(2026, 6, 1, 12, 0, 0, TimeSpan.Zero)));             // 12:00Z
        store.Upsert(Entry("vhf", "NEWEST", new DateTimeOffset(2026, 6, 1, 11, 50, 0, TimeSpan.FromHours(-1))));   // 12:50Z

        // Age-out cutoff far in the past, so only the per-port cap can delete anything.
        int removed = store.Prune(olderThan: T0.AddYears(-1), maxPerPort: 2);

        removed.Should().Be(1);
        store.All().Select(e => e.Callsign).Should().BeEquivalentTo(["MIDDLE", "NEWEST"]);
    }

    [Fact]
    public void A_non_utc_stamp_reads_back_as_the_same_instant_in_utc()
    {
        var offsetLocal = new DateTimeOffset(2026, 6, 1, 13, 0, 0, TimeSpan.FromHours(1));   // 12:00Z
        Open().Upsert(Entry("vhf", "M0LTE-1", offsetLocal));

        var e = Open().All().Single();
        e.LastHeard.Offset.Should().Be(TimeSpan.Zero);
        e.LastHeard.UtcDateTime.Should().Be(offsetLocal.UtcDateTime);
    }

    [Fact]
    public void Data_persists_across_a_reopen()
    {
        Open().Upsert(Entry("vhf", "M0LTE-1", T0, count: 7));

        var reopened = Open();
        reopened.All().Should().ContainSingle(e => e.Callsign == "M0LTE-1" && e.Count == 7);
    }

    [Fact]
    public void A_broken_store_degrades_and_never_throws()
    {
        var broken = new SqliteHeardStore(Path.Combine(dir, "no-such-dir", "pdn.db"));

        broken.Invoking(b => b.Upsert(Entry("vhf", "M0LTE-1", T0))).Should().NotThrow();
        broken.All().Should().BeEmpty();
        broken.Clear("vhf", "M0LTE-1").Should().BeFalse();
        broken.ClearAll().Should().Be(0);
        broken.Prune(T0, 100).Should().Be(0);
    }

    public void Dispose()
    {
        try { Directory.Delete(dir, recursive: true); } catch { /* best effort */ }
    }
}
