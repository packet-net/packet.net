using Packet.Node.Core.Audit;

namespace Packet.Node.Tests.Audit;

public sealed class SqliteAuditLogTests : IDisposable
{
    private readonly string dbPath = Path.Combine(Path.GetTempPath(), $"pdn-audit-{Guid.NewGuid():N}.db");

    public void Dispose()
    {
        foreach (var f in new[] { dbPath, dbPath + "-wal", dbPath + "-shm" })
        {
            try { File.Delete(f); } catch (IOException) { /* best-effort temp cleanup */ }
        }
    }

    private static AuditEntry Entry(string action, string target)
        => AuditEntry.New(DateTimeOffset.UtcNow, "op", "mcp:sse", action, target, "requested", "detail", "127.0.0.1");

    [Fact]
    public void Records_and_reads_back_newest_first()
    {
        var log = new SqliteAuditLog(dbPath);
        log.Record(Entry("reset_port", "vhf"));
        log.Record(Entry("disconnect_session", "vhf:M0LTE"));

        var recent = log.Recent(10);

        recent.Should().HaveCount(2);
        recent[0].Action.Should().Be("disconnect_session", "newest first");
        recent[1].Action.Should().Be("reset_port");
        recent[0].Actor.Should().Be("op");
        recent[0].Source.Should().Be("mcp:sse");
        recent[0].ClientIp.Should().Be("127.0.0.1");
    }

    [Fact]
    public void Round_trips_a_null_client_ip()
    {
        var log = new SqliteAuditLog(dbPath);
        log.Record(AuditEntry.New(DateTimeOffset.UtcNow, "local-stdio", "mcp:stdio", "send_ui_frame", "vhf", "requested", "dest=APRS", clientIp: null));

        log.Recent(1)[0].ClientIp.Should().BeNull();
    }

    [Fact]
    public void Prunes_to_the_row_cap()
    {
        var log = new SqliteAuditLog(dbPath, rowCap: 3);
        for (int i = 0; i < 6; i++)
        {
            log.Record(Entry("reset_port", $"p{i}"));
        }

        var recent = log.Recent(100);

        recent.Should().HaveCount(3, "older rows beyond the cap are pruned on insert");
        recent.Select(e => e.Target).Should().Equal("p5", "p4", "p3");
    }

    [Fact]
    public void A_bad_db_path_degrades_without_throwing()
    {
        // A path under a non-existent directory can't be opened — the store must log and
        // carry on (auditing unavailable), never throw, so an action path is never taken down.
        var log = new SqliteAuditLog(Path.Combine("/nonexistent-pdn-dir", Guid.NewGuid().ToString("N"), "a.db"));

        var record = () => log.Record(Entry("reset_port", "vhf"));

        record.Should().NotThrow();
        log.Recent(10).Should().BeEmpty();
    }
}
