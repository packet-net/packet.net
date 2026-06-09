using Packet.Node.Core.Auth;

namespace Packet.Node.Tests.Auth;

/// <summary>
/// Round-trips the SQLite WebAuthn credential store on a temp db: add/get-by-user/
/// get-by-id/get-all-ids, the sign-count + last-used update, the owner-scoped delete
/// (you can only remove your OWN passkey), the duplicate-id rejection, persistence
/// across a reopen, and the degrade-not-throw resilience of a broken store.
/// </summary>
[Trait("Category", "Node")]
public sealed class SqliteWebAuthnCredentialStoreTests : IDisposable
{
    private readonly string dir;
    private readonly string dbPath;

    public SqliteWebAuthnCredentialStoreTests()
    {
        dir = Path.Combine(Path.GetTempPath(), "packetnet-wastore-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        dbPath = Path.Combine(dir, "pdn.db");
    }

    private SqliteWebAuthnCredentialStore Open() => new(dbPath);

    private static WebAuthnCredentialRecord NewCred(
        byte[] id, string username, uint signCount = 0, string? transports = "internal") =>
        new(id, username, PublicKey: [1, 2, 3, 4], signCount, "public-key", transports,
            AaGuid: Guid.NewGuid().ToByteArray(), DateTimeOffset.UnixEpoch, LastUsedUtc: null);

    [Fact]
    public void Add_then_get_by_user_and_by_id_round_trips()
    {
        var store = Open();
        var id = new byte[] { 10, 20, 30 };
        store.Add(NewCred(id, "alice", signCount: 7)).Should().BeTrue();

        var byUser = store.GetByUser("alice");
        byUser.Should().HaveCount(1);
        byUser[0].CredentialId.Should().Equal(id);
        byUser[0].Username.Should().Be("alice");
        byUser[0].SignCount.Should().Be(7u);
        byUser[0].Transports.Should().Be("internal");
        byUser[0].PublicKey.Should().Equal(1, 2, 3, 4);

        var byId = store.GetByCredentialId(id);
        byId.Should().NotBeNull();
        byId!.Username.Should().Be("alice");

        store.GetByUser("nobody").Should().BeEmpty();
        store.GetByCredentialId(new byte[] { 99 }).Should().BeNull();
    }

    [Fact]
    public void Get_all_credential_ids_returns_every_id_across_users()
    {
        var store = Open();
        store.Add(NewCred(new byte[] { 1 }, "alice")).Should().BeTrue();
        store.Add(NewCred(new byte[] { 2 }, "alice")).Should().BeTrue();
        store.Add(NewCred(new byte[] { 3 }, "bob")).Should().BeTrue();

        var ids = store.GetAllCredentialIds();
        ids.Should().HaveCount(3);
        ids.Select(b => b[0]).Should().BeEquivalentTo(new byte[] { 1, 2, 3 });
    }

    [Fact]
    public void Duplicate_credential_id_is_rejected()
    {
        var store = Open();
        var id = new byte[] { 5, 5, 5 };
        store.Add(NewCred(id, "alice")).Should().BeTrue();
        // Same id again (even for a different user) → rejected by the PRIMARY KEY.
        store.Add(NewCred(id, "bob")).Should().BeFalse();
        store.GetAllCredentialIds().Should().HaveCount(1);
    }

    [Fact]
    public void Update_sign_count_advances_the_counter_and_stamps_last_used()
    {
        var store = Open();
        var id = new byte[] { 7 };
        store.Add(NewCred(id, "alice", signCount: 3)).Should().BeTrue();

        var when = new DateTimeOffset(2026, 6, 9, 12, 0, 0, TimeSpan.Zero);
        store.UpdateSignCount(id, newCount: 4, when);

        var c = store.GetByCredentialId(id)!;
        c.SignCount.Should().Be(4u);
        c.LastUsedUtc.Should().Be(when);
    }

    [Fact]
    public void Update_sign_count_round_trips_a_large_uint_losslessly()
    {
        var store = Open();
        var id = new byte[] { 8 };
        store.Add(NewCred(id, "alice")).Should().BeTrue();
        // A value above int.MaxValue exercises the long round-trip + the unchecked cast back.
        store.UpdateSignCount(id, newCount: 4_000_000_000u, DateTimeOffset.UnixEpoch);
        store.GetByCredentialId(id)!.SignCount.Should().Be(4_000_000_000u);
    }

    [Fact]
    public void Delete_only_removes_the_callers_own_credential()
    {
        var store = Open();
        var id = new byte[] { 42 };
        store.Add(NewCred(id, "alice")).Should().BeTrue();

        // Bob cannot delete Alice's passkey by id.
        store.Delete(id, "bob").Should().BeFalse();
        store.GetByCredentialId(id).Should().NotBeNull();

        // Alice can delete her own.
        store.Delete(id, "alice").Should().BeTrue();
        store.GetByCredentialId(id).Should().BeNull();

        // Already-gone delete is a no-op false.
        store.Delete(id, "alice").Should().BeFalse();
    }

    [Fact]
    public void Data_persists_across_a_reopen()
    {
        var id = new byte[] { 1, 1, 1 };
        Open().Add(NewCred(id, "ed", signCount: 11)).Should().BeTrue();

        var reopened = Open();
        reopened.GetByCredentialId(id)!.SignCount.Should().Be(11u);
    }

    [Fact]
    public void A_broken_store_degrades_and_never_throws()
    {
        // A db path under a non-existent directory can't be opened → the schema init
        // logs + degrades, and every op returns the safe default rather than throwing.
        var broken = new SqliteWebAuthnCredentialStore(Path.Combine(dir, "no-such-dir", "pdn.db"));

        broken.Add(NewCred(new byte[] { 1 }, "alice")).Should().BeFalse();
        broken.GetByUser("alice").Should().BeEmpty();
        broken.GetByCredentialId(new byte[] { 1 }).Should().BeNull();
        broken.GetAllCredentialIds().Should().BeEmpty();
        broken.Delete(new byte[] { 1 }, "alice").Should().BeFalse();
        // UpdateSignCount swallows the fault (best-effort) — must not throw.
        broken.Invoking(b => b.UpdateSignCount(new byte[] { 1 }, 2, DateTimeOffset.UnixEpoch))
            .Should().NotThrow();
    }

    public void Dispose()
    {
        try { Directory.Delete(dir, recursive: true); } catch { /* best effort */ }
    }
}
