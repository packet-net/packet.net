using Packet.Node.Core.Api;
using Packet.Node.Core.Traffic;
using Packet.Node.Tests.Support;

namespace Packet.Node.Tests.Traffic;

/// <summary>
/// The SQLite traffic log (<c>traffic.db</c> - deliberately separate from
/// <c>pdn.db</c>): append/query round-trip incl. the nullable I/S/U field shapes,
/// newest-first ordering + filters, the per-frame raw cap, prune-by-age,
/// prune-to-size (the hard cap really bounds the file), and the resilience
/// contract - a store that cannot open degrades (no throw, false/empty/0) rather
/// than take the node down.
/// </summary>
[Trait("Category", "Node")]
public sealed class SqliteTrafficStoreTests : IDisposable
{
    private readonly string dir;
    private readonly string dbPath;

    public SqliteTrafficStoreTests()
    {
        dir = TestPaths.NewPath("packetnet-traffic");
        Directory.CreateDirectory(dir);
        dbPath = Path.Combine(dir, "traffic.db");
    }

    private SqliteTrafficStore Open() => new(dbPath);

    private static DateTimeOffset At(int seconds)
        => new DateTimeOffset(2026, 6, 11, 12, 0, 0, TimeSpan.Zero).AddSeconds(seconds);

    private static TrafficRecord Frame(
        DateTimeOffset at, string port = "vhf", string direction = "rx", string kind = "I",
        int? ns = 1, int? nr = 2, int? pid = 0xF0, int rawLength = 20)
        => new(
            TimestampUtc: at,
            PortId: port,
            Direction: direction,
            Source: "M0LTE-1",
            Dest: "G7XYZ-2",
            Kind: kind,
            Ns: ns,
            Nr: nr,
            Pf: 1,
            Control: 0x32,
            Pid: pid,
            InfoLength: rawLength - 17,
            Raw: Enumerable.Range(0, rawLength).Select(i => (byte)i).ToArray());

    [Fact]
    public void Append_then_query_round_trips_every_field()
    {
        var store = Open();
        var iFrame = Frame(At(0));
        store.Append([iFrame]).Should().BeTrue();

        var rows = store.Query(portId: null, sinceUtc: null, untilUtc: null, limit: 10);
        rows.Should().HaveCount(1);

        var row = rows[0];
        row.Timestamp.Should().Be(At(0));
        row.PortId.Should().Be("vhf");
        row.Direction.Should().Be("rx");
        row.Source.Should().Be("M0LTE-1");
        row.Dest.Should().Be("G7XYZ-2");
        row.Kind.Should().Be("I");
        row.Ns.Should().Be(1);
        row.Nr.Should().Be(2);
        row.Pf.Should().Be(1);
        row.Control.Should().Be(0x32);
        row.Pid.Should().Be(0xF0);
        row.InfoLength.Should().Be(3);
        row.Raw.Should().Equal(Enumerable.Range(0, 20));
    }

    [Fact]
    public void Nullable_fields_round_trip_as_null_for_a_U_frame()
    {
        // A SABM carries no N(S)/N(R)/PID - the columns must come back null, not 0.
        var store = Open();
        store.Append([Frame(At(0), kind: "SABM", ns: null, nr: null, pid: null)]).Should().BeTrue();

        var row = store.Query(null, null, null, 1).Single();
        row.Kind.Should().Be("SABM");
        row.Ns.Should().BeNull();
        row.Nr.Should().BeNull();
        row.Pid.Should().BeNull();
    }

    [Fact]
    public void Rows_survive_a_reopen()
    {
        Open().Append([Frame(At(0))]).Should().BeTrue();

        // A fresh store over the same file (the "restart") sees the row.
        Open().Count().Should().Be(1);
    }

    [Fact]
    public void Query_returns_newest_first_and_honours_the_limit()
    {
        var store = Open();
        store.Append([Frame(At(0), kind: "SABM", ns: null, nr: null, pid: null), Frame(At(1)), Frame(At(2), kind: "RR", ns: null)]).Should().BeTrue();

        var rows = store.Query(null, null, null, 2);
        rows.Should().HaveCount(2);
        rows[0].Kind.Should().Be("RR", "the newest row comes first");
        rows[1].Kind.Should().Be("I");
    }

    [Fact]
    public void Query_filters_by_port_and_time_range()
    {
        var store = Open();
        store.Append(
        [
            Frame(At(0), port: "vhf"),
            Frame(At(10), port: "hf"),
            Frame(At(20), port: "vhf"),
            Frame(At(30), port: "vhf"),
        ]).Should().BeTrue();

        store.Query("hf", null, null, 10).Should().ContainSingle()
            .Which.Timestamp.Should().Be(At(10));

        // since/until are inclusive UTC bounds.
        var ranged = store.Query("vhf", sinceUtc: At(10), untilUtc: At(20), limit: 10);
        ranged.Should().ContainSingle().Which.Timestamp.Should().Be(At(20));

        store.Query(null, sinceUtc: At(21), untilUtc: null, limit: 10)
            .Should().ContainSingle().Which.Timestamp.Should().Be(At(30));
    }

    [Fact]
    public void Prune_by_age_deletes_only_rows_older_than_the_cutoff()
    {
        var store = Open();
        store.Append([Frame(At(0)), Frame(At(10)), Frame(At(20))]).Should().BeTrue();

        store.PruneOlderThan(At(10)).Should().Be(1, "only the t=0 row is strictly older than the cutoff");

        var remaining = store.Query(null, null, null, 10);
        remaining.Should().HaveCount(2);
        remaining.Select(r => r.Timestamp).Should().BeEquivalentTo([At(10), At(20)]);
    }

    [Fact]
    public void Prune_to_size_caps_the_file_and_keeps_the_newest_rows()
    {
        var store = Open();

        // ~300 rows × 2 KB raw ≈ 600 KB of blob data - comfortably over the cap.
        var rows = Enumerable.Range(0, 300)
            .Select(i => Frame(At(i), rawLength: SqliteTrafficStore.RawCapBytes))
            .ToArray();
        store.Append(rows).Should().BeTrue();
        store.DatabaseSizeBytes().Should().BeGreaterThan(256 * 1024, "the fixture must exceed the cap to prove pruning");

        const long cap = 128 * 1024;
        int pruned = store.PruneToSize(cap);

        pruned.Should().BeGreaterThan(0);
        store.DatabaseSizeBytes().Should().BeLessThanOrEqualTo(cap, "the cap is a hard bound (incremental vacuum returns freed pages)");

        // The survivors are the NEWEST rows - pruning eats from the oldest end.
        var remaining = store.Query(null, null, null, 1000);
        remaining.Should().NotBeEmpty();
        remaining[0].Timestamp.Should().Be(At(299));
        long count = store.Count();
        remaining.Select(r => r.Timestamp)
            .Should().BeEquivalentTo(Enumerable.Range(300 - (int)count, (int)count).Select(At));
    }

    [Fact]
    public void Prune_to_size_on_an_empty_store_is_a_no_op()
        => Open().PruneToSize(1).Should().Be(0, "an empty table has nothing to delete — the loop must terminate");

    [Fact]
    public void A_store_that_cannot_open_degrades_instead_of_throwing()
    {
        // A directory path is unopenable as a SQLite db file.
        var bad = new SqliteTrafficStore(dir);

        bad.Append([Frame(At(0))]).Should().BeFalse();
        bad.Query(null, null, null, 10).Should().BeEmpty();
        bad.PruneOlderThan(At(0)).Should().Be(0);
        bad.PruneToSize(1).Should().Be(0);
        bad.Count().Should().Be(0);
        bad.DatabaseSizeBytes().Should().Be(0);
    }

    public void Dispose()
    {
        try { Directory.Delete(dir, recursive: true); } catch { /* best effort */ }
    }
}
