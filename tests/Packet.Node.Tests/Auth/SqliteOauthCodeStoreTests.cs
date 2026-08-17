using Packet.Node.Core.Auth.Oauth;

namespace Packet.Node.Tests.Auth;

/// <summary>
/// The SQLite OAuth authorization-code store on a temp db. Covers the issue/consume round trip,
/// the single-use guarantee, and PruneExpired: <c>Consume</c> only ever deletes a code that is
/// actually presented at the token endpoint, so an abandoned authorize used to leave its row in
/// <c>oauth_code</c> for ever. Same temp-file shape as the refresh-token store test.
/// </summary>
[Trait("Category", "Node")]
public sealed class SqliteOauthCodeStoreTests : IDisposable
{
    private static readonly DateTimeOffset T0 = new(2026, 6, 1, 12, 0, 0, TimeSpan.Zero);

    private readonly string dir;
    private readonly string dbPath;

    public SqliteOauthCodeStoreTests()
    {
        dir = Path.Combine(Path.GetTempPath(), "packetnet-oauthcode-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        dbPath = Path.Combine(dir, "pdn.db");
    }

    private SqliteOauthCodeStore Open() => new(dbPath);

    private static OauthCode Code(string code, DateTimeOffset expires) =>
        new(code, "client-1", "https://example.test/cb", "challenge", "read", "https://node.test/mcp", "m0lte", expires);

    [Fact]
    public void Issue_then_consume_round_trips_once()
    {
        var store = Open();
        store.Issue(Code("abc", T0 + TimeSpan.FromMinutes(1)));

        var consumed = store.Consume("abc", T0);
        consumed.Should().NotBeNull();
        consumed!.ClientId.Should().Be("client-1");
        consumed.Username.Should().Be("m0lte");

        store.Consume("abc", T0).Should().BeNull();   // single-use
    }

    [Fact]
    public void PruneExpired_deletes_codes_that_expired_and_keeps_the_live_ones()
    {
        var store = Open();
        store.Issue(Code("abandoned", T0 - TimeSpan.FromMinutes(1)));   // never redeemed, already expired
        store.Issue(Code("live", T0 + TimeSpan.FromMinutes(1)));

        store.PruneExpired(T0).Should().Be(1);

        // The expired one is gone from the table (not merely rejected at consume time), and the
        // live one is untouched and still redeemable.
        store.PruneExpired(T0).Should().Be(0);   // idempotent: nothing expired is left
        store.Consume("live", T0).Should().NotBeNull();
    }

    [Fact]
    public void PruneExpired_compares_by_instant_not_by_the_offset_the_caller_used()
    {
        var store = Open();
        // 11:30Z expressed as 12:30+01:00: lexically later than a 12:00Z cutoff, actually earlier.
        store.Issue(Code("stale", new DateTimeOffset(2026, 6, 1, 12, 30, 0, TimeSpan.FromHours(1))));
        // 12:50Z expressed as 11:50-01:00: lexically earlier than the cutoff, actually later.
        store.Issue(Code("live", new DateTimeOffset(2026, 6, 1, 11, 50, 0, TimeSpan.FromHours(-1))));

        store.PruneExpired(T0).Should().Be(1);
        store.Consume("live", T0).Should().NotBeNull();
        store.Consume("stale", T0).Should().BeNull();
    }

    [Fact]
    public void A_broken_store_degrades_and_never_throws()
    {
        var broken = new SqliteOauthCodeStore(Path.Combine(dir, "no-such-dir", "pdn.db"));

        broken.Invoking(b => b.Issue(Code("abc", T0))).Should().NotThrow();
        broken.Consume("abc", T0).Should().BeNull();
        broken.PruneExpired(T0).Should().Be(0);
    }

    public void Dispose()
    {
        try { Directory.Delete(dir, recursive: true); } catch { /* best effort */ }
    }
}
