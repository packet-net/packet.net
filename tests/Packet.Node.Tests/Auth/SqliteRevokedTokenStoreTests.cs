using Packet.Node.Core.Auth;
using Packet.Node.Tests.Support;

namespace Packet.Node.Tests.Auth;

/// <summary>The per-token revocation denylist (#428): listed ids are refused, the list
/// survives a restart, and an id is dropped once its token would have expired anyway.</summary>
[Trait("Category", "Node")]
public sealed class SqliteRevokedTokenStoreTests : IDisposable
{
    private static readonly DateTimeOffset T0 = new(2026, 9, 30, 12, 0, 0, TimeSpan.Zero);

    private readonly string dir;
    private readonly string dbPath;

    public SqliteRevokedTokenStoreTests()
    {
        dir = TestPaths.NewPath("packetnet-revoked");
        Directory.CreateDirectory(dir);
        dbPath = Path.Combine(dir, "pdn.db");
    }

    [Fact]
    public void A_revoked_id_is_refused_survives_a_reopen_and_is_pruned_at_its_expiry()
    {
        var store = new SqliteRevokedTokenStore(dbPath);
        store.IsRevoked("abc").Should().BeFalse();

        store.Revoke("abc", expiresUtc: T0 + TimeSpan.FromHours(1), now: T0).Should().BeTrue();
        store.Revoke("abc", expiresUtc: T0 + TimeSpan.FromHours(1), now: T0).Should().BeTrue("revoking twice is idempotent");
        store.IsRevoked("abc").Should().BeTrue();
        store.IsRevoked("abd").Should().BeFalse();

        // A fresh instance over the same file (a node restart) still refuses it.
        var reopened = new SqliteRevokedTokenStore(dbPath);
        reopened.IsRevoked("abc").Should().BeTrue();
        reopened.Count.Should().Be(1);

        // Not yet expired: kept. Expired: dropped, here and on disk.
        reopened.PruneExpired(T0 + TimeSpan.FromMinutes(59)).Should().Be(0);
        reopened.IsRevoked("abc").Should().BeTrue();
        reopened.PruneExpired(T0 + TimeSpan.FromHours(1)).Should().Be(1);
        reopened.IsRevoked("abc").Should().BeFalse();
        new SqliteRevokedTokenStore(dbPath).Count.Should().Be(0);
    }

    [Fact]
    public void A_broken_store_still_revokes_for_the_life_of_the_process()
    {
        var broken = new SqliteRevokedTokenStore(Path.Combine(dir, "no-such-dir", "pdn.db"));
        broken.Revoke("abc", T0 + TimeSpan.FromHours(1), T0).Should().BeFalse("nothing was persisted");
        broken.IsRevoked("abc").Should().BeTrue("the in-memory list holds until restart");
        broken.PruneExpired(T0 + TimeSpan.FromHours(2)).Should().Be(1);
    }

    public void Dispose()
    {
        try { Directory.Delete(dir, recursive: true); } catch (IOException) { }
    }
}
