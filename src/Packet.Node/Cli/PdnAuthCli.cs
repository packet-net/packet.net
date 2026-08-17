using Microsoft.Extensions.Logging;
using Packet.Node.Core.Auth;

namespace Packet.Node.Cli;

/// <summary>
/// The <c>pdn auth</c> subcommand family. Today it holds one verb:
/// <c>pdn auth rotate-signing-key</c> - replace the node's JWT signing key, which
/// invalidates <b>every token this node ever issued</b>.
/// </summary>
/// <remarks>
/// <para>
/// A node-minted JWT is stateless: there is no jti, no server-side session, and the
/// authorization gate never re-checks that the subject still exists. So the long-lived MCP
/// bearer token (<c>POST /api/v1/mcp/token</c>, up to <c>mcp.tokenLifetimeDays</c>) cannot be
/// individually revoked - the docs and the code have said "rotate the signing key" since it
/// shipped, and nothing implemented rotation (review item C056). This verb does.
/// </para>
/// <para>
/// <b>What it kills:</b> every MCP token and every panel access token, immediately on the next
/// node start. <b>What it does not kill:</b> refresh-token families - they are opaque rows in
/// <c>pdn.db</c>, unaffected by the key, so a logged-in operator's panel session simply mints a
/// fresh access token at its next refresh. That is deliberate: rotating because an MCP token
/// leaked should not log every human out. To end sessions too, delete the user (which now
/// revokes their families and passkeys) or clear the refresh table.
/// </para>
/// <para>
/// <b>Offline by design.</b> A running node holds the key it read at startup, so the new key
/// takes effect at the next restart; the verb says so, rather than pretending a live endpoint
/// could swap the key under the validator. Like <c>pdn config</c> this short-circuits before
/// the web host is built and honours the same <c>--db</c> / <c>PACKETNET_DB</c> resolution, so
/// it operates on exactly the store the host uses.
/// </para>
/// <para>
/// <b>It never creates a database.</b> The resolution falls back to
/// <c>/var/lib/packetnet/pdn.db</c> (see <see cref="NodeStatePaths"/>) before the working
/// directory, and refuses outright when the resolved file is not there, because
/// <c>SqliteUserStore</c> would otherwise build an empty store and rotate its brand-new key
/// with a success banner while the real node's key went untouched - a revocation the operator
/// believed in and did not get (#727 item 3). The banner names the resolved path for the same
/// reason.
/// </para>
/// </remarks>
public static class PdnAuthCli
{
    /// <summary>Run the <c>auth</c> subcommand. <paramref name="args"/> is the full argv (the
    /// first element is <c>auth</c>). Returns a process exit code (0 = ok).</summary>
    public static Task<int> RunAsync(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);

        if (args.Length < 2)
        {
            return Task.FromResult(Usage("(none)"));
        }

        return Task.FromResult(args[1] switch
        {
            "rotate-signing-key" => RotateSigningKey(args),
            var other => Usage(other),
        });
    }

    private static int Usage(string verb)
    {
        Console.Error.WriteLine($"pdn auth: unknown subcommand '{verb}' (want rotate-signing-key).");
        Console.Error.WriteLine("  pdn auth rotate-signing-key   replace the JWT signing key (invalidates every issued token)");
        return 2;
    }

    private static int RotateSigningKey(string[] args)
    {
        using var loggers = LoggerFactory.Create(b =>
            b.AddConsole(o => o.LogToStandardErrorThreshold = LogLevel.Trace));

        if (ResolveDbPath(args) is not { } dbPath)
        {
            Console.Error.WriteLine("pdn auth rotate-signing-key: no node database found. Looked at:");
            foreach (var candidate in NodeStatePaths.DefaultCandidates())
            {
                Console.Error.WriteLine($"  - {candidate}");
            }
            Console.Error.WriteLine("Name the database with --db <path> (or PACKETNET_DB) and run it again.");
            return 1;
        }

        // NEVER create the database (#727 item 3). SqliteUserStore's constructor runs
        // EnsureSchema, which happily builds a fresh store on a path that does not exist, and a
        // brand-new store rotates its brand-new key successfully - so the verb used to report a
        // revocation it had not performed. Existence is checked before the store is opened.
        if (!File.Exists(dbPath))
        {
            Console.Error.WriteLine($"pdn auth rotate-signing-key: '{Path.GetFullPath(dbPath)}' does not exist.");
            Console.Error.WriteLine("Refusing to create one: rotating a key in an empty database revokes nothing.");
            Console.Error.WriteLine($"The packaged node keeps its store at {NodeStatePaths.DefaultStateDirectory}/{NodeStatePaths.DbFileName}.");
            return 1;
        }

        var resolved = Path.GetFullPath(dbPath);
        var users = new SqliteUserStore(resolved, loggers.CreateLogger<SqliteUserStore>());

        if (users.RotateSigningKey() is null)
        {
            Console.Error.WriteLine($"pdn auth rotate-signing-key: could not write a new key to {resolved}.");
            return 1;
        }

        // Never print the key. The operator needs to know what happened, not what it is - and
        // WHICH database it happened to, so "I rotated the wrong file" is visible at a glance.
        Console.Error.WriteLine($"Signing key rotated in {resolved}.");
        Console.Error.WriteLine("  - every MCP token and panel access token this node issued is now invalid");
        Console.Error.WriteLine("  - refresh-token sessions are unaffected (they re-mint on their next refresh)");
        Console.Error.WriteLine("  - RESTART the node for the new key to take effect (systemctl restart packetnet)");
        return 0;
    }

    /// <summary>
    /// The database this verb operates on: an explicit <c>--db</c> / <c>PACKETNET_DB</c>, else
    /// the packaged state directory's <c>pdn.db</c>, else the working directory's when it
    /// already exists. Null when there is no candidate at all. Exposed to the test suite so the
    /// resolution is pinned rather than inferred.
    /// </summary>
    internal static string? ResolveDbPath(
        string[] args, string? stateDirectory = null, string? workingDirectory = null) =>
        NodeStatePaths.ResolveExistingDbPath(args, stateDirectory, workingDirectory);
}
