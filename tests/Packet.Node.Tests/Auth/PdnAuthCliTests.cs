using Packet.Node.Cli;
using Packet.Node.Core.Auth;
using Packet.Node.Tests.Support;

namespace Packet.Node.Tests.Auth;

/// <summary>
/// <c>pdn auth rotate-signing-key</c> - the offline verb that revokes every JWT this node ever
/// issued (review item C056).
/// </summary>
/// <remarks>
/// A node-minted token is stateless: no jti, no session row, and the gate never re-checks the
/// subject. The MCP token docs and <c>PdnOauthApi</c> both said "rotate the signing key" to
/// invalidate one, and nothing implemented rotation. The CLI operates on the same
/// <c>pdn.db</c> the host reads, resolved the same way.
/// </remarks>
[Trait("Category", "Node")]
public sealed class PdnAuthCliTests : IDisposable
{
    private readonly string dir;
    private readonly string dbPath;

    public PdnAuthCliTests()
    {
        dir = TestPaths.NewPath("pdn-authcli");
        Directory.CreateDirectory(dir);
        dbPath = Path.Combine(dir, "pdn.db");
    }

    [Fact]
    public async Task Rotate_signing_key_replaces_the_key_the_next_boot_will_read()
    {
        var before = new SqliteUserStore(dbPath).GetOrCreateSigningKey();
        before.Should().NotBeNull();

        var exit = await PdnAuthCli.RunAsync(["auth", "rotate-signing-key", "--db", dbPath]);

        exit.Should().Be(0);
        var after = new SqliteUserStore(dbPath).GetOrCreateSigningKey();
        after.Should().NotBeNull();
        after!.Length.Should().Be(32);
        after.Should().NotEqual(before!);
    }

    [Fact]
    public async Task An_unknown_auth_subcommand_is_a_usage_error()
    {
        (await PdnAuthCli.RunAsync(["auth"])).Should().Be(2);
        (await PdnAuthCli.RunAsync(["auth", "do-something-else"])).Should().Be(2);
    }

    public void Dispose()
    {
        try { Directory.Delete(dir, recursive: true); } catch { /* best effort */ }
    }
}
