using System.Globalization;
using Microsoft.Data.Sqlite;
using Packet.Ax25;
using Packet.Ax25.Session;
using Packet.Ax25.Transport;
using Packet.Core;
using Packet.Node.Core.Api;
using Packet.Node.Core.Traffic;

namespace Packet.Node.Tests.Traffic;

/// <summary>
/// RSSI/SNR/noise-floor persisted through the traffic log: the projection from a
/// <see cref="MonitorEvent"/>, the SQLite store round-trip, and the additive column migration for a
/// db created before the radio-signal columns existed.
/// </summary>
[Trait("Category", "Node")]
public sealed class TrafficRssiTests : IDisposable
{
    private static readonly DateTimeOffset T0 = new(2026, 6, 1, 12, 0, 0, TimeSpan.Zero);
    private static readonly Callsign Local = Callsign.Parse("M0LTE-1");
    private static readonly Callsign Peer = Callsign.Parse("G7XYZ-2");
    private readonly string dir;
    private readonly string dbPath;

    public TrafficRssiTests()
    {
        dir = Path.Combine(Path.GetTempPath(), "packetnet-trafficrssi-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        dbPath = Path.Combine(dir, "traffic.db");
    }

    [Fact]
    public void Traffic_record_carries_the_radio_signal_from_a_monitor_event()
    {
        var args = new Ax25FrameEventArgs
        {
            Frame = Ax25Frame.Ui(Local, Peer, "x"u8),
            Direction = FrameDirection.Received,
            Timestamp = T0,
        };
        var evt = MonitorEventFactory.From(1, "vhf", args, new RadioMetadata(RssiDbm: -82f, SnrDb: 30f, NoiseFloorDbm: -112f));

        var record = TrafficRecord.From(evt);
        record.RssiDbm.Should().Be(-82f);
        record.SnrDb.Should().Be(30f);
        record.NoiseFloorDbm.Should().Be(-112f);
    }

    [Fact]
    public void Store_round_trips_the_radio_signal_columns()
    {
        var store = new SqliteTrafficStore(dbPath);
        store.Append([new TrafficRecord(
            T0, "vhf", "rx", "G0ABC", "M0LTE-1", "UI", null, null, 0, 3, 0xF0, 2, [1, 2, 3],
            RssiDbm: -82f, SnrDb: 30f, NoiseFloorDbm: -112f)]).Should().BeTrue();

        var frame = store.Query(null, null, null, 10).Single();
        frame.RssiDbm.Should().Be(-82f);
        frame.SnrDb.Should().Be(30f);
        frame.NoiseFloorDbm.Should().Be(-112f);
    }

    [Fact]
    public void A_frame_with_no_radio_reads_back_null_signal()
    {
        var store = new SqliteTrafficStore(dbPath);
        store.Append([new TrafficRecord(
            T0, "vhf", "tx", "M0LTE-1", "G0ABC", "UI", null, null, 0, 3, 0xF0, 0, [1])]);

        var frame = store.Query(null, null, null, 10).Single();
        frame.RssiDbm.Should().BeNull();
        frame.SnrDb.Should().BeNull();
        frame.NoiseFloorDbm.Should().BeNull();
    }

    [Fact]
    public void Opening_a_pre_rssi_traffic_db_migrates_in_the_columns()
    {
        var cs = new SqliteConnectionStringBuilder { DataSource = dbPath }.ToString();
        using (var conn = new SqliteConnection(cs))
        {
            conn.Open();
            using var create = conn.CreateCommand();
            create.CommandText =
                "CREATE TABLE traffic (id INTEGER PRIMARY KEY AUTOINCREMENT, ts_utc TEXT NOT NULL, " +
                "port TEXT NOT NULL, direction TEXT NOT NULL, source TEXT NOT NULL, dest TEXT NOT NULL, " +
                "kind TEXT NOT NULL, ns INTEGER, nr INTEGER, pf INTEGER NOT NULL, control INTEGER NOT NULL, " +
                "pid INTEGER, info_len INTEGER NOT NULL, raw BLOB NOT NULL);";
            create.ExecuteNonQuery();
            using var insert = conn.CreateCommand();
            insert.CommandText =
                "INSERT INTO traffic (ts_utc, port, direction, source, dest, kind, pf, control, info_len, raw) " +
                "VALUES (@t,'vhf','rx','G0ABC','M0LTE-1','UI',0,3,0,@raw);";
            insert.Parameters.AddWithValue("@t", T0.ToString("o", CultureInfo.InvariantCulture));
            insert.Parameters.AddWithValue("@raw", new byte[] { 1, 2, 3 });
            insert.ExecuteNonQuery();
        }

        var store = new SqliteTrafficStore(dbPath);   // runs the migration
        store.Query(null, null, null, 10).Single().RssiDbm.Should().BeNull("the pre-migration row had no RSSI");

        // A new frame with RSSI persists through the migrated columns.
        store.Append([new TrafficRecord(
            T0.AddMinutes(1), "vhf", "rx", "G4XYZ", "M0LTE-1", "UI", null, null, 0, 3, 0xF0, 1, [9],
            RssiDbm: -60f)]);
        store.Query(null, null, null, 10).Should().Contain(f => f.Source == "G4XYZ" && f.RssiDbm == -60f);
    }

    public void Dispose()
    {
        try { Directory.Delete(dir, recursive: true); } catch { /* best effort */ }
    }
}
