using Microsoft.Data.Sqlite;
using Packet.Node.Core.Auth;
using Packet.Node.Tests.Support;

namespace Packet.Node.Tests.Auth;

/// <summary>
/// Round-trips the SQLite refresh-token store on a temp db: insert/find by hash,
/// single-token revoke, whole-family revoke, prune of expired tokens, persistence
/// across a reopen, and the degrade-not-throw resilience of a broken store.
/// </summary>
[Trait("Category", "Node")]
public sealed class SqliteRefreshTokenStoreTests : IDisposable
{
    private readonly string dir;
    private readonly string dbPath;

    public SqliteRefreshTokenStoreTests()
    {
        dir = TestPaths.NewPath("packetnet-rtstore");
        Directory.CreateDirectory(dir);
        dbPath = Path.Combine(dir, "pdn.db");
    }

    private SqliteRefreshTokenStore Open() => new(dbPath);

    private static RefreshTokenRecord NewToken(string hash, string family, bool revoked = false,
        DateTimeOffset? expires = null) =>
        new(hash, "m0lte", family, DateTimeOffset.UnixEpoch,
            expires ?? new DateTimeOffset(2030, 1, 1, 0, 0, 0, TimeSpan.Zero), revoked);

    [Fact]
    public void Insert_then_find_by_hash_round_trips()
    {
        var store = Open();
        store.Insert(NewToken("hash-1", "famA")).Should().BeTrue();

        var found = store.FindByHash("hash-1");
        found.Should().NotBeNull();
        found!.TokenHash.Should().Be("hash-1");
        found.Family.Should().Be("famA");
        found.Username.Should().Be("m0lte");
        found.Revoked.Should().BeFalse();

        store.FindByHash("nope").Should().BeNull();
    }

    [Fact]
    public void Revoke_marks_a_single_token_and_stamps_the_consume_time()
    {
        var store = Open();
        store.Insert(NewToken("hash-1", "famA")).Should().BeTrue();
        store.Insert(NewToken("hash-2", "famA")).Should().BeTrue();

        var consumedAt = new DateTimeOffset(2026, 6, 12, 8, 0, 0, TimeSpan.Zero);
        store.Revoke("hash-1", consumedAt).Should().BeTrue();
        var revoked = store.FindByHash("hash-1")!;
        revoked.Revoked.Should().BeTrue();
        revoked.RevokedUtc.Should().Be(consumedAt);               // leeway-eligible stamp persisted
        store.FindByHash("hash-2")!.Revoked.Should().BeFalse();   // sibling untouched

        // A hard revoke (null) leaves no leeway stamp.
        store.Revoke("hash-2", null).Should().BeTrue();
        store.FindByHash("hash-2")!.RevokedUtc.Should().BeNull();

        store.Revoke("absent", consumedAt).Should().BeFalse();
    }

    [Fact]
    public void Revoke_is_a_conditional_consume_a_second_consume_matches_zero_rows()
    {
        // The atomicity core of rotation: only a LIVE token is revoked, so two
        // concurrent rotations of the same token can't both consume it - the second
        // UPDATE matches 0 rows and must NOT overwrite the winner's leeway stamp.
        var store = Open();
        store.Insert(NewToken("hash-1", "famA")).Should().BeTrue();

        var winnerStamp = new DateTimeOffset(2026, 6, 12, 8, 0, 0, TimeSpan.Zero);
        store.Revoke("hash-1", winnerStamp).Should().BeTrue();

        store.Revoke("hash-1", null).Should().BeFalse();
        var row = store.FindByHash("hash-1")!;
        row.Revoked.Should().BeTrue();
        row.RevokedUtc.Should().Be(winnerStamp);   // the winner's stamp survives
    }

    [Fact]
    public void HasLiveToken_tracks_whether_a_family_still_has_an_unrevoked_token()
    {
        var store = Open();
        store.Insert(NewToken("a1", "famA")).Should().BeTrue();
        store.Insert(NewToken("a2", "famA")).Should().BeTrue();

        store.HasLiveToken("famA").Should().BeTrue();
        store.HasLiveToken("famZ").Should().BeFalse();            // unknown family

        store.Revoke("a1", null).Should().BeTrue();
        store.HasLiveToken("famA").Should().BeTrue();             // a2 still live
        store.Revoke("a2", null).Should().BeTrue();
        store.HasLiveToken("famA").Should().BeFalse();            // family now dead
    }

    [Fact]
    public void RevokeFamily_marks_every_unrevoked_row_in_the_family()
    {
        var store = Open();
        store.Insert(NewToken("a1", "famA")).Should().BeTrue();
        store.Insert(NewToken("a2", "famA")).Should().BeTrue();
        store.Insert(NewToken("b1", "famB")).Should().BeTrue();

        store.RevokeFamily("famA").Should().Be(2);
        store.FindByHash("a1")!.Revoked.Should().BeTrue();
        store.FindByHash("a2")!.Revoked.Should().BeTrue();
        store.FindByHash("b1")!.Revoked.Should().BeFalse();       // other family untouched

        // Idempotent - re-revoking an already-revoked family changes 0 rows.
        store.RevokeFamily("famA").Should().Be(0);
    }

    [Fact]
    public void PruneExpired_removes_only_already_expired_tokens()
    {
        var store = Open();
        var cutoff = new DateTimeOffset(2026, 6, 9, 0, 0, 0, TimeSpan.Zero);
        store.Insert(NewToken("old", "famA", expires: cutoff - TimeSpan.FromDays(1))).Should().BeTrue();
        store.Insert(NewToken("fresh", "famA", expires: cutoff + TimeSpan.FromDays(1))).Should().BeTrue();

        store.PruneExpired(cutoff).Should().Be(1);
        store.FindByHash("old").Should().BeNull();
        store.FindByHash("fresh").Should().NotBeNull();
    }

    [Fact]
    public void Opening_a_pre_leeway_db_migrates_in_the_revoked_utc_column()
    {
        // Simulate a node upgraded in place: a refresh_token table created before the
        // revoked_utc column existed. Opening the store must ALTER it in, not fail.
        var cs = new SqliteConnectionStringBuilder { DataSource = dbPath }.ToString();
        using (var conn = new SqliteConnection(cs))
        {
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText =
                "CREATE TABLE refresh_token (token_hash TEXT PRIMARY KEY, username TEXT NOT NULL, " +
                "family TEXT NOT NULL, issued_utc TEXT NOT NULL, expires_utc TEXT NOT NULL, " +
                "revoked INTEGER NOT NULL DEFAULT 0);";
            cmd.ExecuteNonQuery();
        }

        var store = Open();   // runs the migration
        store.Insert(NewToken("hash-1", "famA")).Should().BeTrue();
        var consumedAt = new DateTimeOffset(2026, 6, 12, 8, 0, 0, TimeSpan.Zero);
        store.Revoke("hash-1", consumedAt).Should().BeTrue();
        store.FindByHash("hash-1")!.RevokedUtc.Should().Be(consumedAt);

        // Idempotent - a second open over the now-migrated table doesn't re-ALTER/throw.
        Open().FindByHash("hash-1").Should().NotBeNull();
    }

    [Fact]
    public void Data_persists_across_a_reopen()
    {
        Open().Insert(NewToken("hash-1", "famA")).Should().BeTrue();

        var reopened = Open();
        reopened.FindByHash("hash-1").Should().NotBeNull();
    }

    [Fact]
    public void Revoke_all_for_user_burns_every_family_that_user_owns()
    {
        var store = Open();
        // Two live families for one user (two devices), plus another user's family.
        store.Insert(NewToken("h-a", "famA")).Should().BeTrue();
        store.Insert(NewToken("h-b", "famB")).Should().BeTrue();
        store.Insert(new RefreshTokenRecord(
            "h-c", "someone-else", "famC", DateTimeOffset.UnixEpoch,
            new DateTimeOffset(2030, 1, 1, 0, 0, 0, TimeSpan.Zero), Revoked: false)).Should().BeTrue();

        // The account-lifecycle kill: deleting a user must leave no live chain behind for a
        // recreated same-name account to inherit (C055).
        store.RevokeAllForUser("m0lte").Should().Be(2);

        store.HasLiveToken("famA").Should().BeFalse();
        store.HasLiveToken("famB").Should().BeFalse();
        store.HasLiveToken("famC").Should().BeTrue();   // untouched

        // A hard kill, so no leeway stamp - the burned family can never be resurrected
        // through the reuse-leeway grace path.
        store.FindByHash("h-a")!.RevokedUtc.Should().BeNull();

        store.RevokeAllForUser("m0lte").Should().Be(0);   // idempotent
    }

    [Fact]
    public void A_broken_store_degrades_and_never_throws()
    {
        // A db path under a non-existent directory can't be opened → the schema init
        // logs + degrades, and every op returns the safe default rather than throwing.
        var broken = new SqliteRefreshTokenStore(Path.Combine(dir, "no-such-dir", "pdn.db"));

        broken.Insert(NewToken("h", "f")).Should().BeFalse();
        broken.FindByHash("h").Should().BeNull();
        broken.Revoke("h", null).Should().BeFalse();
        broken.RevokeFamily("f").Should().Be(0);
        broken.RevokeAllForUser("m0lte").Should().Be(0);
        broken.HasLiveToken("f").Should().BeFalse();
        broken.PruneExpired(DateTimeOffset.UnixEpoch).Should().Be(0);
    }

    public void Dispose()
    {
        try { Directory.Delete(dir, recursive: true); } catch { /* best effort */ }
    }
}
