using Packet.Node.Core.Auth;

namespace Packet.Node.Tests.Auth;

/// <summary>
/// Round-trips the SQLite user store on a temp db: create/find/delete, the unique
/// username constraint, count, last-login stamp, and the persisted signing key
/// (stable across reopens, 256-bit).
/// </summary>
[Trait("Category", "Node")]
public sealed class SqliteUserStoreTests : IDisposable
{
    private readonly string dir;
    private readonly string dbPath;

    public SqliteUserStoreTests()
    {
        dir = Path.Combine(Path.GetTempPath(), "packetnet-userstore-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        dbPath = Path.Combine(dir, "pdn.db");
    }

    private SqliteUserStore Open() => new(dbPath);

    private static UserRecord NewUser(string name, string scope = AuthScopes.Read) =>
        new(name, PasswordHasher.Hash("pw-" + name), scope, DateTimeOffset.UnixEpoch, null);

    [Fact]
    public void Empty_store_counts_zero()
    {
        var store = Open();
        store.Count().Should().Be(0);
        store.FindByUsername("nobody").Should().BeNull();
    }

    [Fact]
    public void Create_then_find_round_trips()
    {
        var store = Open();
        var user = NewUser("alice", AuthScopes.Admin);
        store.Create(user).Should().BeTrue();

        store.Count().Should().Be(1);
        var found = store.FindByUsername("alice");
        found.Should().NotBeNull();
        found!.Username.Should().Be("alice");
        found.Scope.Should().Be(AuthScopes.Admin);
        found.PasswordHash.Should().Be(user.PasswordHash);
        found.LastLoginUtc.Should().BeNull();
    }

    [Fact]
    public void Duplicate_username_is_rejected()
    {
        var store = Open();
        store.Create(NewUser("bob")).Should().BeTrue();
        store.Create(NewUser("bob", AuthScopes.Admin)).Should().BeFalse();
        store.Count().Should().Be(1);
    }

    [Fact]
    public void Delete_removes_the_user()
    {
        var store = Open();
        store.Create(NewUser("carol")).Should().BeTrue();
        store.Delete("carol").Should().BeTrue();
        store.Count().Should().Be(0);
        store.Delete("carol").Should().BeFalse();   // already gone
    }

    [Fact]
    public void Update_last_login_stamps_the_user()
    {
        var store = Open();
        store.Create(NewUser("dave")).Should().BeTrue();
        var when = new DateTimeOffset(2026, 6, 9, 12, 0, 0, TimeSpan.Zero);
        store.UpdateLastLogin("dave", when);

        store.FindByUsername("dave")!.LastLoginUtc.Should().Be(when);
    }

    [Fact]
    public void List_returns_all_users_without_dropping_hashes()
    {
        var store = Open();
        store.Create(NewUser("a")).Should().BeTrue();
        store.Create(NewUser("b")).Should().BeTrue();

        var all = store.List();
        all.Should().HaveCount(2);
        all.Should().OnlyContain(u => !string.IsNullOrEmpty(u.PasswordHash));
    }

    [Fact]
    public void Signing_key_is_256_bit_and_stable_across_reopens()
    {
        var key1 = Open().GetOrCreateSigningKey();
        key1.Should().NotBeNull();
        key1!.Length.Should().Be(32);   // 256 bits

        // Reopen the same db → same persisted key (tokens survive a restart).
        var key2 = Open().GetOrCreateSigningKey();
        key2.Should().Equal(key1);
    }

    [Fact]
    public void Data_persists_across_a_reopen()
    {
        Open().Create(NewUser("ed", AuthScopes.Operate)).Should().BeTrue();

        var reopened = Open();
        reopened.Count().Should().Be(1);
        reopened.FindByUsername("ed")!.Scope.Should().Be(AuthScopes.Operate);
    }

    // --- over-RF TOTP enrolment (auth part 4) ------------------------------------

    [Fact]
    public void Set_totp_secret_round_trips_via_find_by_username_and_callsign()
    {
        var store = Open();
        store.Create(NewUser("frank")).Should().BeTrue();

        store.SetTotpSecret("frank", "JBSWY3DPEHPK3PXP", "G7XYZ").Should().BeTrue();

        var byName = store.FindByUsername("frank");
        byName.Should().NotBeNull();
        byName!.TotpSecret.Should().Be("JBSWY3DPEHPK3PXP");
        byName.Callsign.Should().Be("G7XYZ");
        byName.LastTotpCounter.Should().BeNull();   // reset to "none accepted" on (re)bind

        // FindByCallsign resolves the same user, case-insensitively (the over-RF lookup).
        store.FindByCallsign("G7XYZ")!.Username.Should().Be("frank");
        store.FindByCallsign("g7xyz")!.Username.Should().Be("frank");
        // An unknown callsign finds nothing.
        store.FindByCallsign("M0AAA").Should().BeNull();
    }

    [Fact]
    public void Callsign_uniqueness_rejects_a_second_user_binding_the_same_callsign()
    {
        var store = Open();
        store.Create(NewUser("gina")).Should().BeTrue();
        store.Create(NewUser("hank")).Should().BeTrue();

        store.SetTotpSecret("gina", "JBSWY3DPEHPK3PXP", "G7XYZ").Should().BeTrue();
        // hank cannot claim the same callsign — even with a different casing.
        store.SetTotpSecret("hank", "GEZDGNBVGY3TQOJQ", "g7xyz").Should().BeFalse();

        // gina still owns it; hank has none.
        store.FindByCallsign("G7XYZ")!.Username.Should().Be("gina");
        store.FindByUsername("hank")!.TotpSecret.Should().BeNull();
    }

    [Fact]
    public void Re_enrolling_the_same_callsign_for_its_own_user_is_allowed()
    {
        var store = Open();
        store.Create(NewUser("ivy")).Should().BeTrue();
        store.SetTotpSecret("ivy", "JBSWY3DPEHPK3PXP", "G7XYZ").Should().BeTrue();
        store.UpdateTotpCounter("ivy", 42).Should().BeTrue();

        // A re-enrol with a fresh secret for the SAME callsign/user succeeds and resets
        // the replay high-water mark.
        store.SetTotpSecret("ivy", "GEZDGNBVGY3TQOJQ", "G7XYZ").Should().BeTrue();
        var found = store.FindByUsername("ivy")!;
        found.TotpSecret.Should().Be("GEZDGNBVGY3TQOJQ");
        found.LastTotpCounter.Should().BeNull();
    }

    [Fact]
    public void Update_totp_counter_persists_the_replay_high_water_mark()
    {
        var store = Open();
        store.Create(NewUser("jack")).Should().BeTrue();
        store.SetTotpSecret("jack", "JBSWY3DPEHPK3PXP", "M0JCK").Should().BeTrue();

        store.UpdateTotpCounter("jack", 12345).Should().BeTrue();
        store.FindByUsername("jack")!.LastTotpCounter.Should().Be(12345);

        // Survives a reopen (it's the persisted single-use guard).
        Open().FindByUsername("jack")!.LastTotpCounter.Should().Be(12345);
    }

    [Fact]
    public void Clear_totp_nulls_the_credential_and_reports_whether_a_row_changed()
    {
        var store = Open();
        store.Create(NewUser("kate")).Should().BeTrue();
        store.SetTotpSecret("kate", "JBSWY3DPEHPK3PXP", "M0KAT").Should().BeTrue();
        store.UpdateTotpCounter("kate", 7).Should().BeTrue();

        store.ClearTotp("kate").Should().BeTrue();
        var found = store.FindByUsername("kate")!;
        found.TotpSecret.Should().BeNull();
        found.Callsign.Should().BeNull();
        found.LastTotpCounter.Should().BeNull();
        store.FindByCallsign("M0KAT").Should().BeNull();

        // A second clear changes nothing (already cleared) → false; idempotent overall.
        store.ClearTotp("kate").Should().BeFalse();
    }

    [Fact]
    public void User_summary_exposes_has_totp_and_callsign_but_never_the_secret()
    {
        var store = Open();
        store.Create(NewUser("liam")).Should().BeTrue();
        store.SetTotpSecret("liam", "JBSWY3DPEHPK3PXP", "M0LIA").Should().BeTrue();

        var summary = UserSummary.From(store.FindByUsername("liam")!);
        summary.HasTotp.Should().BeTrue();
        summary.Callsign.Should().Be("M0LIA");
        // UserSummary has no TotpSecret member — verified at compile time; here we assert
        // the unenrolled projection is HasTotp:false.
        UserSummary.From(NewUser("mona")).HasTotp.Should().BeFalse();
    }

    [Fact]
    public void An_old_pre_migration_db_without_totp_columns_still_opens_and_gains_them()
    {
        // Hand-build a db with the ORIGINAL (pre-TOTP) user schema — no callsign /
        // totp_secret / last_totp_counter columns — then a row, exactly as a node from
        // before this feature would have on disk.
        using (var conn = new Microsoft.Data.Sqlite.SqliteConnection(
            new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder { DataSource = dbPath }.ToString()))
        {
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                CREATE TABLE user (
                    username       TEXT PRIMARY KEY,
                    password_hash  TEXT NOT NULL,
                    scopes         TEXT NOT NULL,
                    created_utc    TEXT NOT NULL,
                    last_login_utc TEXT NULL);
                INSERT INTO user (username, password_hash, scopes, created_utc, last_login_utc)
                VALUES ('legacy', 'hash', 'admin', '2026-01-01T00:00:00.0000000+00:00', NULL);
                """;
            cmd.ExecuteNonQuery();
        }

        // Opening through the store runs the additive migration; the legacy row survives
        // and reads back with null TOTP fields.
        var store = Open();
        var legacy = store.FindByUsername("legacy");
        legacy.Should().NotBeNull();
        legacy!.Scope.Should().Be(AuthScopes.Admin);
        legacy.TotpSecret.Should().BeNull();
        legacy.Callsign.Should().BeNull();
        legacy.LastTotpCounter.Should().BeNull();

        // And the new columns are usable on the migrated db.
        store.SetTotpSecret("legacy", "JBSWY3DPEHPK3PXP", "M0LEG").Should().BeTrue();
        store.FindByCallsign("M0LEG")!.Username.Should().Be("legacy");

        // Re-opening (re-running the migration) is a no-op and preserves the data.
        Open().FindByUsername("legacy")!.TotpSecret.Should().Be("JBSWY3DPEHPK3PXP");
    }

    public void Dispose()
    {
        try { Directory.Delete(dir, recursive: true); } catch { /* best effort */ }
    }
}
