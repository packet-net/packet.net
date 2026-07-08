using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Time.Testing;
using Packet.Node.Core.Heard;

namespace Packet.Node.Tests.Heard;

/// <summary>
/// The passive excess-TXDELAY plumb through the MHeard log and its SQLite store
/// (docs/research/txdelay-optimisation.md, layer 1): the rolling per-station median of
/// measured pre-data carrier samples in <see cref="HeardLog"/>, the node-wide merge,
/// the store round-trip, and the additive column migration for a db created before the
/// fields existed (mirroring the RSSI/SNR migrations).
/// </summary>
[Trait("Category", "Node")]
public sealed class HeardPreDataCarrierTests : IDisposable
{
    private static readonly DateTimeOffset T0 = new(2026, 6, 1, 12, 0, 0, TimeSpan.Zero);
    private readonly string dir;
    private readonly string dbPath;

    public HeardPreDataCarrierTests()
    {
        dir = Path.Combine(Path.GetTempPath(), "packetnet-heardpredata-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        dbPath = Path.Combine(dir, "pdn.db");
    }

    [Fact]
    public void Record_aggregates_a_rolling_median_not_a_newest_wins_value()
    {
        var log = new HeardLog(store: null);
        log.Record("vhf", "GB7XXX", T0, preDataCarrierMs: 400f);
        log.Record("vhf", "GB7XXX", T0.AddMinutes(1), preDataCarrierMs: 500f);
        log.Record("vhf", "GB7XXX", T0.AddMinutes(2), preDataCarrierMs: 420f);

        var entry = log.ForPort("vhf").Single();
        entry.MedianPreDataCarrierMs.Should().Be(420f, "the median of 400/500/420, not the newest 420 by luck of order");
        entry.PreDataCarrierSamples.Should().Be(3);
    }

    [Fact]
    public void Frames_without_a_measurement_advance_nothing()
    {
        var log = new HeardLog(store: null);
        log.Record("vhf", "GB7XXX", T0, preDataCarrierMs: 400f);
        // Later frames in a train (and frames on a radio-less port) carry no pre-data.
        log.Record("vhf", "GB7XXX", T0.AddSeconds(1));
        log.Record("vhf", "GB7XXX", T0.AddSeconds(2), rssiDbm: -80f);

        var entry = log.ForPort("vhf").Single();
        entry.MedianPreDataCarrierMs.Should().Be(400f);
        entry.PreDataCarrierSamples.Should().Be(1);
        entry.Count.Should().Be(3, "every hearing still counts");
    }

    [Fact]
    public void The_window_keeps_the_last_32_samples()
    {
        var log = new HeardLog(store: null);
        // 8 ancient 900 ms readings, then 32 modern ~200 ms ones: the old age out.
        for (int i = 0; i < 8; i++)
        {
            log.Record("vhf", "GB7XXX", T0.AddSeconds(i), preDataCarrierMs: 900f);
        }
        for (int i = 0; i < 32; i++)
        {
            log.Record("vhf", "GB7XXX", T0.AddMinutes(1 + i), preDataCarrierMs: 200f);
        }

        var entry = log.ForPort("vhf").Single();
        entry.PreDataCarrierSamples.Should().Be(32);
        entry.MedianPreDataCarrierMs.Should().Be(200f);
    }

    [Fact]
    public void Node_wide_median_follows_the_most_recently_heard_port()
    {
        var log = new HeardLog(store: null);
        log.Record("vhf", "GB7XXX", T0, preDataCarrierMs: 400f);
        log.Record("uhf", "GB7XXX", T0.AddMinutes(30), preDataCarrierMs: 150f);

        var summary = log.NodeWide().Single(s => s.Callsign == "GB7XXX");
        summary.MedianPreDataCarrierMs.Should().Be(150f);
        summary.PreDataCarrierSamples.Should().Be(1);
    }

    [Fact]
    public void Median_and_sample_count_round_trip_through_the_store()
    {
        // A fixed clock near the samples keeps the 30-day retention from pruning them on hydration.
        var clock = new FakeTimeProvider(T0.AddMinutes(2));
        var store = new SqliteHeardStore(dbPath);
        var log = new HeardLog(store, clock);
        log.Record("vhf", "GB7XXX", T0, preDataCarrierMs: 400f);
        log.Record("vhf", "GB7XXX", T0.AddMinutes(1), preDataCarrierMs: 420f);

        // A fresh log hydrated from the same store sees the persisted median/count.
        var rehydrated = new HeardLog(new SqliteHeardStore(dbPath), clock);
        var entry = rehydrated.ForPort("vhf").Single();
        entry.MedianPreDataCarrierMs.Should().Be(410f);
        entry.PreDataCarrierSamples.Should().Be(2);
    }

    [Fact]
    public void After_a_restart_the_window_restarts_but_a_new_sample_honestly_resets_the_count()
    {
        var clock = new FakeTimeProvider(T0.AddMinutes(2));
        var store = new SqliteHeardStore(dbPath);
        new HeardLog(store, clock).Record("vhf", "GB7XXX", T0, preDataCarrierMs: 400f);

        var rehydrated = new HeardLog(new SqliteHeardStore(dbPath), clock);
        rehydrated.Record("vhf", "GB7XXX", T0.AddMinutes(1), preDataCarrierMs: 200f);

        var entry = rehydrated.ForPort("vhf").Single();
        entry.MedianPreDataCarrierMs.Should().Be(200f, "the persisted median was display-only; the live window restarts");
        entry.PreDataCarrierSamples.Should().Be(1);
    }

    [Fact]
    public void A_pre_predata_database_gains_the_columns_additively()
    {
        // A heard_log created before the pre-data columns existed (the RSSI/SNR-era shape).
        using (var conn = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = dbPath }.ToString()))
        {
            conn.Open();
            using var create = conn.CreateCommand();
            create.CommandText = """
                CREATE TABLE heard_log (
                    port_id         TEXT NOT NULL,
                    callsign        TEXT NOT NULL,
                    first_heard_utc TEXT NOT NULL,
                    last_heard_utc  TEXT NOT NULL,
                    count           INTEGER NOT NULL,
                    last_rssi_dbm   REAL,
                    last_snr_db     REAL,
                    PRIMARY KEY (port_id, callsign));
                INSERT INTO heard_log VALUES ('vhf', 'M0LTE-1', '2026-06-01T12:00:00.0000000+00:00',
                    '2026-06-01T12:00:00.0000000+00:00', 7, -80.0, 20.0);
                """;
            create.ExecuteNonQuery();
        }

        var store = new SqliteHeardStore(dbPath);
        var rows = store.All();
        rows.Should().ContainSingle();
        rows[0].MedianPreDataCarrierMs.Should().BeNull("the legacy row has no measurement");
        rows[0].PreDataCarrierSamples.Should().Be(0);
        rows[0].LastRssiDbm.Should().Be(-80f, "the legacy columns are untouched");

        // And the migrated table accepts the new fields.
        store.Upsert(rows[0] with { MedianPreDataCarrierMs = 412f, PreDataCarrierSamples = 12 });
        store.All().Single().MedianPreDataCarrierMs.Should().Be(412f);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try
        {
            Directory.Delete(dir, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
