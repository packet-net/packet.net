using System.Globalization;
using Microsoft.Data.Sqlite;
using Packet.Node.Core.Heard;

namespace Packet.Node.Tests.Heard;

/// <summary>
/// Last-heard RSSI through the MHeard log and its SQLite store: the newest-frame-wins semantics in
/// <see cref="HeardLog"/>, the node-wide merge, the store round-trip, and the additive column
/// migration for a db created before the field existed.
/// </summary>
[Trait("Category", "Node")]
public sealed class HeardRssiTests : IDisposable
{
    private static readonly DateTimeOffset T0 = new(2026, 6, 1, 12, 0, 0, TimeSpan.Zero);
    private readonly string dir;
    private readonly string dbPath;

    public HeardRssiTests()
    {
        dir = Path.Combine(Path.GetTempPath(), "packetnet-heardrssi-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        dbPath = Path.Combine(dir, "pdn.db");
    }

    [Fact]
    public void Record_captures_the_last_heard_rssi()
    {
        var log = new HeardLog(store: null);
        log.Record("vhf", "M0LTE-1", T0, rssiDbm: -80f);
        log.ForPort("vhf").Single().LastRssiDbm.Should().Be(-80f);
    }

    [Fact]
    public void A_newer_frame_replaces_the_last_heard_rssi()
    {
        var log = new HeardLog(store: null);
        log.Record("vhf", "M0LTE-1", T0, rssiDbm: -80f);
        log.Record("vhf", "M0LTE-1", T0.AddMinutes(1), rssiDbm: -72f);
        log.ForPort("vhf").Single().LastRssiDbm.Should().Be(-72f);
    }

    [Fact]
    public void A_newer_frame_with_no_rssi_leaves_the_last_real_reading_in_place()
    {
        var log = new HeardLog(store: null);
        log.Record("vhf", "M0LTE-1", T0, rssiDbm: -80f);
        log.Record("vhf", "M0LTE-1", T0.AddMinutes(1), rssiDbm: null);
        log.ForPort("vhf").Single().LastRssiDbm.Should().Be(-80f);
    }

    [Fact]
    public void Node_wide_last_rssi_follows_the_most_recently_heard_port()
    {
        var log = new HeardLog(store: null);
        log.Record("vhf", "M0LTE-1", T0, rssiDbm: -80f);
        log.Record("hf", "M0LTE-1", T0.AddMinutes(30), rssiDbm: -95f);   // heard more recently on hf
        log.NodeWide().Single(s => s.Callsign == "M0LTE-1").LastRssiDbm.Should().Be(-95f);
    }

    [Fact]
    public void Store_round_trips_last_heard_rssi()
    {
        new SqliteHeardStore(dbPath).Upsert(new HeardEntry("vhf", "M0LTE-1", T0, T0, 3, -84.5f));
        new SqliteHeardStore(dbPath).All().Single().LastRssiDbm.Should().Be(-84.5f);
    }

    [Fact]
    public void Opening_a_pre_rssi_db_migrates_in_the_column()
    {
        // A heard_log created before last-heard RSSI existed. Opening the store must ALTER it in.
        var cs = new SqliteConnectionStringBuilder { DataSource = dbPath }.ToString();
        using (var conn = new SqliteConnection(cs))
        {
            conn.Open();
            using var create = conn.CreateCommand();
            create.CommandText =
                "CREATE TABLE heard_log (port_id TEXT NOT NULL, callsign TEXT NOT NULL, " +
                "first_heard_utc TEXT NOT NULL, last_heard_utc TEXT NOT NULL, count INTEGER NOT NULL, " +
                "PRIMARY KEY (port_id, callsign));";
            create.ExecuteNonQuery();
            using var insert = conn.CreateCommand();
            insert.CommandText = "INSERT INTO heard_log VALUES ('vhf','M0LTE-1',@t,@t,1);";
            insert.Parameters.AddWithValue("@t", T0.ToString("o", CultureInfo.InvariantCulture));
            insert.ExecuteNonQuery();
        }

        var store = new SqliteHeardStore(dbPath);   // runs the migration
        store.All().Single().LastRssiDbm.Should().BeNull("the pre-migration row had no RSSI");

        // And a new upsert with RSSI persists through the migrated column.
        store.Upsert(new HeardEntry("vhf", "G0ABC", T0, T0, 1, -70f));
        store.All().Should().Contain(e => e.Callsign == "G0ABC" && e.LastRssiDbm == -70f);
    }

    public void Dispose()
    {
        try { Directory.Delete(dir, recursive: true); } catch { /* best effort */ }
    }
}
