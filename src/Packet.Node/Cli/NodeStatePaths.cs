namespace Packet.Node.Cli;

/// <summary>
/// The one place that decides which <c>pdn.db</c> a pdn process operates on, shared by the web
/// host's composition root and the offline CLI verbs (<c>pdn config</c>, <c>pdn auth</c>).
/// </summary>
/// <remarks>
/// <para>
/// The node and the CLI verbs used to carry three private copies of the same resolution, and
/// they disagreed in the case that matters. <c>pdn auth rotate-signing-key</c> fell back to
/// <c>&lt;cwd&gt;/pdn.db</c> and <see cref="Packet.Node.Core.Auth.SqliteUserStore"/>'s
/// constructor CREATES a database that is not there, so running the verb from any shell whose
/// working directory was not the state directory (a <c>sudo pdn auth rotate-signing-key</c>
/// from <c>/root</c>, say) made an empty <c>/root/pdn.db</c>, rotated a key nobody reads,
/// printed "Signing key rotated" and exited 0. The operator restarted the node believing a
/// leaked token was dead; it authenticated for its full lifetime (#727 item 3).
/// </para>
/// <para>
/// The packaged unit sets <c>WorkingDirectory=/var/lib/packetnet</c> for the SERVICE only, so
/// an interactive verb never inherits it. <see cref="ResolveExistingDbPath"/> therefore falls
/// back to the packaged state directory before the working directory, and only ever names a
/// file that already exists, leaving "it is not there" for the caller to report rather than
/// papering over it with a fresh database.
/// </para>
/// </remarks>
internal static class NodeStatePaths
{
    /// <summary>The writable state directory the <c>.deb</c> creates and the unit runs in.</summary>
    public const string DefaultStateDirectory = "/var/lib/packetnet";

    /// <summary>The node's SQLite store: config, routing, auth, heard, audit.</summary>
    public const string DbFileName = "pdn.db";

    /// <summary>
    /// The resolution the <b>node</b> uses: <c>--db &lt;path&gt;</c> wins, then
    /// <c>PACKETNET_DB</c>, then <c>pdn.db</c> in the working directory (which on the packaged
    /// node IS the state directory). Always returns a path; the node legitimately creates the
    /// database on a first boot.
    /// </summary>
    public static string ResolveDbPath(string[] args, string? workingDirectory = null)
    {
        ArgumentNullException.ThrowIfNull(args);
        return Explicit(args) ?? Path.Combine(workingDirectory ?? Directory.GetCurrentDirectory(), DbFileName);
    }

    /// <summary>
    /// The resolution an <b>offline verb that must not create anything</b> uses. An explicit
    /// <c>--db</c> / <c>PACKETNET_DB</c> is honoured verbatim (the operator named it, so report
    /// it back even when it is missing); otherwise the packaged state directory's database when
    /// it exists, then the working directory's when THAT exists. Returns null when no candidate
    /// exists, so the caller can refuse with the paths it looked at.
    /// </summary>
    public static string? ResolveExistingDbPath(
        string[] args, string? stateDirectory = null, string? workingDirectory = null)
    {
        ArgumentNullException.ThrowIfNull(args);
        if (Explicit(args) is { } named)
        {
            return named;
        }

        var stateDb = Path.Combine(stateDirectory ?? DefaultStateDirectory, DbFileName);
        if (File.Exists(stateDb))
        {
            return stateDb;
        }

        var cwdDb = Path.Combine(workingDirectory ?? Directory.GetCurrentDirectory(), DbFileName);
        return File.Exists(cwdDb) ? cwdDb : null;
    }

    /// <summary>The candidate paths <see cref="ResolveExistingDbPath"/> considered, for the
    /// error message when it found none.</summary>
    public static IReadOnlyList<string> DefaultCandidates(
        string? stateDirectory = null, string? workingDirectory = null) =>
    [
        Path.Combine(stateDirectory ?? DefaultStateDirectory, DbFileName),
        Path.Combine(workingDirectory ?? Directory.GetCurrentDirectory(), DbFileName),
    ];

    // --db <path> then PACKETNET_DB; null when the operator named neither.
    private static string? Explicit(string[] args)
    {
        for (int i = 0; i < args.Length - 1; i++)
        {
            if (args[i] == "--db" && args[i + 1].Length > 0)
            {
                return args[i + 1];
            }
        }

        var env = Environment.GetEnvironmentVariable("PACKETNET_DB");
        return string.IsNullOrWhiteSpace(env) ? null : env;
    }
}
