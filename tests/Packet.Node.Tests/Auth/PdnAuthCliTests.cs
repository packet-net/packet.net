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

    // --- #727 item 3: resolve the node's db, and never create one -------------------

    [Fact]
    public async Task Rotating_against_a_database_that_does_not_exist_fails_and_creates_nothing()
    {
        // The bug: SqliteUserStore's ctor runs EnsureSchema, which BUILDS a database on a path
        // that is not there, and a brand-new store rotates its brand-new key perfectly happily.
        // So `sudo pdn auth rotate-signing-key` from /root made /root/pdn.db, printed "Signing
        // key rotated", exited 0 - and the real node's key was untouched, so the leaked token
        // the operator was revoking stayed valid for its whole lifetime.
        var missing = Path.Combine(dir, "not-here.db");

        var exit = await PdnAuthCli.RunAsync(["auth", "rotate-signing-key", "--db", missing]);

        exit.Should().Be(1, "refusing is the only honest answer - rotating an empty database revokes nothing");
        File.Exists(missing).Should().BeFalse("the verb must never create the database it was asked to rotate");
    }

    [Fact]
    public void The_state_directory_database_wins_over_the_working_directory()
    {
        // The packaged unit sets WorkingDirectory=/var/lib/packetnet for the SERVICE only, so an
        // interactive verb does not inherit it. Resolution therefore looks in the state dir
        // first, and only then at the shell's cwd.
        var stateDir = Path.Combine(dir, "state");
        var cwd = Path.Combine(dir, "cwd");
        Directory.CreateDirectory(stateDir);
        Directory.CreateDirectory(cwd);
        var stateDb = Path.Combine(stateDir, "pdn.db");
        File.WriteAllText(stateDb, string.Empty);
        File.WriteAllText(Path.Combine(cwd, "pdn.db"), string.Empty);

        PdnAuthCli.ResolveDbPath([], stateDir, cwd).Should().Be(stateDb);
    }

    [Fact]
    public void The_working_directory_is_used_only_when_a_database_is_already_there()
    {
        var stateDir = Path.Combine(dir, "empty-state");
        var cwd = Path.Combine(dir, "cwd2");
        Directory.CreateDirectory(stateDir);
        Directory.CreateDirectory(cwd);

        // Nothing anywhere: no candidate, so the verb has something specific to refuse with.
        PdnAuthCli.ResolveDbPath([], stateDir, cwd).Should().BeNull();

        var cwdDb = Path.Combine(cwd, "pdn.db");
        File.WriteAllText(cwdDb, string.Empty);
        PdnAuthCli.ResolveDbPath([], stateDir, cwd).Should().Be(cwdDb);
    }

    [Fact]
    public void An_explicit_db_flag_wins_over_both_defaults_even_when_it_is_missing()
    {
        var stateDir = Path.Combine(dir, "state3");
        Directory.CreateDirectory(stateDir);
        File.WriteAllText(Path.Combine(stateDir, "pdn.db"), string.Empty);
        var named = Path.Combine(dir, "operator-named.db");

        // Reported back verbatim: the operator named it, so "that file is not there" is the
        // message they need, not a silent fallback to some other database.
        PdnAuthCli.ResolveDbPath(["auth", "rotate-signing-key", "--db", named], stateDir, dir)
            .Should().Be(named);
    }

    public void Dispose()
    {
        try { Directory.Delete(dir, recursive: true); } catch { /* best effort */ }
    }
}
