using Microsoft.Extensions.Time.Testing;
using Packet.Node.Core.Heard;

namespace Packet.Node.Tests.Heard;

/// <summary>
/// Unit tests for the <see cref="HeardLog"/> service (#454): recording a hearing (first/last/count),
/// per-port vs node-wide aggregation + dedup/merge of repeated hearings, survival across a simulated
/// restart (a fresh log over the same store re-hydrates), and the retention policy (age-out window +
/// per-port cap). The clock is a <see cref="FakeTimeProvider"/> so timestamps are deterministic.
/// </summary>
[Trait("Category", "Node")]
public sealed class HeardLogTests : IDisposable
{
    private static readonly DateTimeOffset T0 = new(2026, 6, 1, 12, 0, 0, TimeSpan.Zero);
    private readonly FakeTimeProvider clock = new(T0);

    private readonly string dir;
    private readonly string dbPath;

    public HeardLogTests()
    {
        dir = Path.Combine(Path.GetTempPath(), "packetnet-heardlog-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        dbPath = Path.Combine(dir, "pdn.db");
    }

    public void Dispose()
    {
        try { Directory.Delete(dir, recursive: true); } catch { /* best effort */ }
    }

    private HeardLog InMemory(TimeSpan? retention = null, int? max = null)
        => new(store: null, time: clock, retention: retention, maxPerPort: max);

    [Fact]
    public void Record_sets_first_heard_once_advances_last_and_counts()
    {
        var log = InMemory();

        log.Record("vhf", "M0LTE-1", T0);
        log.Record("vhf", "M0LTE-1", T0.AddMinutes(5));
        log.Record("vhf", "M0LTE-1", T0.AddMinutes(10));

        var e = log.ForPort("vhf").Single();
        e.Callsign.Should().Be("M0LTE-1");
        e.FirstHeard.Should().Be(T0);                 // earliest, never overwritten
        e.LastHeard.Should().Be(T0.AddMinutes(10));   // advances
        e.Count.Should().Be(3);                       // deduped/merged into one row + a count
    }

    [Fact]
    public void Per_port_keeps_the_same_callsign_on_two_ports_separate()
    {
        var log = InMemory();
        log.Record("vhf", "M0LTE-1", T0);
        log.Record("hf", "M0LTE-1", T0.AddMinutes(1));

        log.ForPort("vhf").Should().ContainSingle(e => e.Callsign == "M0LTE-1" && e.Count == 1);
        log.ForPort("hf").Should().ContainSingle(e => e.Callsign == "M0LTE-1" && e.Count == 1);
        log.All().Should().HaveCount(2);   // two distinct (port, callsign) rows
    }

    [Fact]
    public void Node_wide_merges_a_callsign_across_ports()
    {
        var log = InMemory();
        log.Record("vhf", "M0LTE-1", T0);
        log.Record("vhf", "M0LTE-1", T0.AddMinutes(2));   // count 2 on vhf
        log.Record("hf", "M0LTE-1", T0.AddMinutes(30));   // count 1 on hf, latest overall
        log.Record("vhf", "G0ABC", T0.AddMinutes(5));

        var wide = log.NodeWide();
        var mlte = wide.Single(s => s.Callsign == "M0LTE-1");
        mlte.FirstHeard.Should().Be(T0);                  // earliest across ports
        mlte.LastHeard.Should().Be(T0.AddMinutes(30));    // latest across ports
        mlte.Count.Should().Be(3);                        // 2 (vhf) + 1 (hf)
        mlte.Ports.Should().Be(2);                        // heard on two ports

        wide.Single(s => s.Callsign == "G0ABC").Ports.Should().Be(1);
    }

    [Fact]
    public void Most_recently_heard_sorts_first()
    {
        var log = InMemory();
        log.Record("vhf", "OLDER", T0);
        log.Record("vhf", "NEWER", T0.AddMinutes(10));

        log.ForPort("vhf")[0].Callsign.Should().Be("NEWER");
        log.NodeWide()[0].Callsign.Should().Be("NEWER");
    }

    [Fact]
    public void The_log_survives_a_simulated_restart()
    {
        // First run: record over a real sqlite store, then shut the log down (which stops the
        // writer and flushes what it still holds - writes are asynchronous now, C077).
        var store1 = new SqliteHeardStore(dbPath);
        var log1 = new HeardLog(store1, clock);
        log1.Record("vhf", "M0LTE-1", T0);
        log1.Record("vhf", "M0LTE-1", T0.AddMinutes(1));
        log1.Record("hf", "G0ABC", T0.AddMinutes(2));
        log1.Dispose();

        // Second run: a brand-new log over a brand-new store on the SAME db file hydrates the
        // persisted state — survival across node restart AND port teardown (no AttachPort needed).
        var store2 = new SqliteHeardStore(dbPath);
        using var log2 = new HeardLog(store2, clock);

        log2.All().Should().HaveCount(2);
        log2.ForPort("vhf").Single().Count.Should().Be(2);     // the count persisted
        log2.NodeWide().Should().Contain(s => s.Callsign == "G0ABC");
    }

    [Fact]
    public void Forget_drops_one_entry_and_clear_drops_all()
    {
        var log = InMemory();
        log.Record("vhf", "M0LTE-1", T0);
        log.Record("vhf", "G0ABC", T0);

        log.Forget("vhf", "M0LTE-1").Should().BeTrue();
        log.ForPort("vhf").Should().ContainSingle(e => e.Callsign == "G0ABC");
        log.Forget("vhf", "M0LTE-1").Should().BeFalse();   // already gone

        log.Clear().Should().Be(1);
        log.All().Should().BeEmpty();
    }

    [Fact]
    public void Prune_ages_out_stations_not_heard_within_the_window()
    {
        var log = InMemory(retention: TimeSpan.FromDays(2));
        log.Record("vhf", "STALE", T0);
        log.Record("vhf", "FRESH", T0);

        // Three days later, refresh only FRESH, then prune.
        clock.SetUtcNow(T0.AddDays(3));
        log.Record("vhf", "FRESH", clock.GetUtcNow());

        log.Prune();

        log.ForPort("vhf").Should().ContainSingle(e => e.Callsign == "FRESH");
    }

    [Fact]
    public void Prune_caps_each_port_keeping_the_newest()
    {
        var log = InMemory(max: 2);
        log.Record("vhf", "A", T0);
        log.Record("vhf", "B", T0.AddMinutes(1));
        log.Record("vhf", "C", T0.AddMinutes(2));   // newest

        log.Prune();

        // Capped to the newest 2 on the port.
        log.ForPort("vhf").Select(e => e.Callsign).Should().BeEquivalentTo(["B", "C"]);
    }

    [Fact]
    public void Construction_re_applies_retention_to_the_hydrated_set()
    {
        // Seed a store with a stale row, then build a log with a 2-day window: construction prunes.
        var store = new SqliteHeardStore(dbPath);
        store.Upsert(new HeardEntry("vhf", "STALE", T0.AddDays(-10), T0.AddDays(-10), 1));
        store.Upsert(new HeardEntry("vhf", "FRESH", T0, T0, 1));

        using var log = new HeardLog(store, clock, retention: TimeSpan.FromDays(2));

        log.ForPort("vhf").Should().ContainSingle(e => e.Callsign == "FRESH");
    }

    // ---- C077: the store write must never sit on the AX.25 inbound pump ------------------

    /// <summary>
    /// Record used to do a synchronous SQLite upsert (and, every 2048 hearings, an inline prune)
    /// on the inbound pump BEFORE DispatchInbound, so a stalled writer stalled port RX. The write
    /// now goes to a bounded drop-oldest channel drained by one writer: Record returns promptly
    /// even while the store is wedged.
    /// </summary>
    [Fact]
    public void Record_returns_promptly_while_the_store_is_blocked()
    {
        using var blocked = new BlockingHeardStore();
        using var log = new HeardLog(blocked, clock);

        // Park the writer inside the store, then keep recording on "the pump".
        log.Record("vhf", "M0LTE-1", T0);
        blocked.EnteredWrite.Wait(TimeSpan.FromSeconds(5)).Should().BeTrue("the writer should have reached the store");

        for (var i = 0; i < 200; i++)
        {
            log.Record("vhf", $"G{i}ABC", T0.AddSeconds(i));
        }

        // Nothing blocked: the hot view is complete and correct with the store still wedged.
        log.ForPort("vhf").Should().HaveCount(201);
        blocked.Release();
    }

    /// <summary>The writer coalesces per (port, callsign): a station heard many times while the
    /// writer is busy costs ONE row write carrying the newest snapshot, not one per frame.</summary>
    [Fact]
    public void The_writer_coalesces_repeated_hearings_of_one_station()
    {
        using var store = new BlockingHeardStore();
        using var log = new HeardLog(store, clock);

        // Park the writer inside its first write, so the rest queue up behind it.
        log.Record("vhf", "M0LTE-1", T0);
        store.EnteredWrite.Wait(TimeSpan.FromSeconds(5)).Should().BeTrue("the writer should have reached the store");
        for (var i = 1; i < 50; i++)
        {
            log.Record("vhf", "M0LTE-1", T0.AddSeconds(i));
        }

        store.Release();
        log.Flush();

        store.Rows.Should().BeLessThan(50, "repeated hearings of one station coalesce into one row write");
        store.Last!.Count.Should().Be(50, "the coalesced row must carry the newest snapshot");
    }

    /// <summary>A store fault must not be visible to the pump, and Dispose must persist the tail
    /// (a clean shutdown loses no hearings).</summary>
    [Fact]
    public void Dispose_flushes_the_tail_to_the_store()
    {
        var store = new SqliteHeardStore(dbPath);
        var log = new HeardLog(store, clock);
        log.Record("vhf", "M0LTE-1", T0);
        log.Dispose();

        new SqliteHeardStore(dbPath).All().Should().ContainSingle()
            .Which.Callsign.Should().Be("M0LTE-1");
    }

    /// <summary>A store whose writes park until released, counting the rows that get through:
    /// the wedged-writer case, and the coalescing evidence.</summary>
    private sealed class BlockingHeardStore : IHeardStore, IDisposable
    {
        private readonly ManualResetEventSlim gate = new(false);
        private int rows;

        public ManualResetEventSlim EnteredWrite { get; } = new(false);

        /// <summary>Rows actually written (the writer is single-threaded, so this is safe).</summary>
        public int Rows => Volatile.Read(ref rows);

        public HeardEntry? Last { get; private set; }

        public void Upsert(HeardEntry entry)
        {
            EnteredWrite.Set();
            gate.Wait(TimeSpan.FromSeconds(30));
            Interlocked.Increment(ref rows);
            Last = entry;
        }

        public void Release() => gate.Set();

        public IReadOnlyList<HeardEntry> All() => [];

        public bool Clear(string portId, string callsign) => false;

        public int ClearAll() => 0;

        public int Prune(DateTimeOffset olderThan, int maxPerPort) => 0;

        public void Dispose()
        {
            gate.Set();
            gate.Dispose();
            EnteredWrite.Dispose();
        }
    }

    [Fact]
    public void Empty_callsign_is_ignored()
    {
        var log = InMemory();
        log.Record("vhf", "", T0);
        log.All().Should().BeEmpty();
    }
}
