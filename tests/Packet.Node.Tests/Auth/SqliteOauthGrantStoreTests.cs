using Packet.Node.Core.Auth.Oauth;
using Packet.Node.Tests.Support;

namespace Packet.Node.Tests.Auth;

/// <summary>The grant behind an OAuth refresh-token family (#428): stored, found, listed, forgotten.</summary>
[Trait("Category", "Node")]
public sealed class SqliteOauthGrantStoreTests : IDisposable
{
    private static readonly DateTimeOffset T0 = new(2026, 9, 30, 12, 0, 0, TimeSpan.Zero);

    private readonly string dir;
    private readonly string dbPath;

    public SqliteOauthGrantStoreTests()
    {
        dir = TestPaths.NewPath("packetnet-oauthgrant");
        Directory.CreateDirectory(dir);
        dbPath = Path.Combine(dir, "pdn.db");
    }

    [Fact]
    public void A_grant_round_trips_by_family_lists_by_owner_and_is_forgotten_once()
    {
        var store = new SqliteOauthGrantStore(dbPath);
        store.Add(new OauthGrant("fam-1", "pdn-a", "op", "operate", T0)).Should().BeTrue();
        store.Add(new OauthGrant("fam-2", "pdn-b", "op", "read", T0 + TimeSpan.FromMinutes(1))).Should().BeTrue();
        store.Add(new OauthGrant("fam-1", "pdn-c", "op", "read", T0)).Should().BeFalse("a family has one grant");

        var found = store.FindByFamily("fam-1");
        found.Should().NotBeNull();
        found!.ClientId.Should().Be("pdn-a");
        found.Scope.Should().Be("operate");
        found.CreatedUtc.Should().Be(T0);
        store.FindByFamily("nope").Should().BeNull();

        store.ListByUser("op").Select(g => g.Family).Should().Equal("fam-2", "fam-1");
        store.ListByUser("nobody").Should().BeEmpty();

        store.Delete("fam-1").Should().BeTrue();
        store.Delete("fam-1").Should().BeFalse();
        store.FindByFamily("fam-1").Should().BeNull();
    }

    [Fact]
    public void A_broken_store_degrades_and_never_throws()
    {
        var broken = new SqliteOauthGrantStore(Path.Combine(dir, "no-such-dir", "pdn.db"));
        broken.Add(new OauthGrant("fam", "pdn-a", "op", "read", T0)).Should().BeFalse();
        broken.FindByFamily("fam").Should().BeNull();
        broken.ListByUser("op").Should().BeEmpty();
        broken.Delete("fam").Should().BeFalse();
    }

    public void Dispose()
    {
        try { Directory.Delete(dir, recursive: true); } catch (IOException) { }
    }
}
