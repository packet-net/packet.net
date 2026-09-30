using Packet.Node.Core.Auth.Oauth;
using Packet.Node.Tests.Support;

namespace Packet.Node.Tests.Auth;

/// <summary>
/// The OAuth client store's row cap (#426): registration is open and unauthenticated, so the
/// table is bounded and a flood past the cap evicts the oldest registrations rather than
/// growing pdn.db without limit.
/// </summary>
[Trait("Category", "Node")]
public sealed class SqliteOauthClientStoreTests : IDisposable
{
    private static readonly DateTimeOffset T0 = new(2026, 9, 30, 12, 0, 0, TimeSpan.Zero);

    private readonly string dir;
    private readonly string dbPath;

    public SqliteOauthClientStoreTests()
    {
        dir = TestPaths.NewPath("packetnet-oauthclient");
        Directory.CreateDirectory(dir);
        dbPath = Path.Combine(dir, "pdn.db");
    }

    [Fact]
    public void Registration_past_the_cap_evicts_the_oldest_clients()
    {
        var store = new SqliteOauthClientStore(dbPath, rowCap: 3);
        var first = store.Register("first", ["https://a.example/cb"], T0)!;
        var second = store.Register("second", ["https://b.example/cb"], T0 + TimeSpan.FromSeconds(1))!;
        var third = store.Register("third", ["https://c.example/cb"], T0 + TimeSpan.FromSeconds(2))!;
        var fourth = store.Register("fourth", ["https://d.example/cb"], T0 + TimeSpan.FromSeconds(3))!;

        store.List().Should().HaveCount(3);
        store.Find(first.ClientId).Should().BeNull("the oldest registration is the one evicted");
        store.Find(second.ClientId).Should().NotBeNull();
        store.Find(third.ClientId).Should().NotBeNull();
        store.Find(fourth.ClientId).Should().NotBeNull();
    }

    [Fact]
    public void The_default_cap_is_generous_for_a_node_and_small_for_a_database()
    {
        SqliteOauthClientStore.RowCap.Should().Be(256);
        var act = () => new SqliteOauthClientStore(dbPath, rowCap: 0);
        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    public void Dispose()
    {
        try { Directory.Delete(dir, recursive: true); } catch (IOException) { }
    }
}
