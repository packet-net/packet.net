using Microsoft.Extensions.Logging;
using Packet.Node.Core.Configuration;

namespace Packet.Node.Cli;

/// <summary>
/// The <c>pdn config</c> subcommand family: the headless inspect / diff / restore CLI for
/// config-in-DB (#473). Config now lives in <c>pdn.db</c>, not a hand-editable file, so an
/// operator with shell access needs a way to round-trip it as text:
/// <list type="bullet">
/// <item><c>pdn config export [--out &lt;path&gt;]</c> - boot the provider, read
/// <see cref="IConfigProvider.Current"/>, write <see cref="NodeConfigYaml"/> to stdout (or
/// a file). The export-edit-import workflow's first leg + a backup path.</item>
/// <item><c>pdn config import &lt;path&gt;</c> - parse + validate + apply a YAML file
/// through the SAME write seam (<see cref="IWritableConfigProvider.TryApply"/>) the web API
/// uses, persisting to the DB. The explicit apply that replaces the old hot file-watch.</item>
/// </list>
/// Like <c>pdn mcp</c> this short-circuits BEFORE the web host is built: it boots ONLY the
/// <see cref="SqliteConfigProvider"/> over the resolved <c>pdn.db</c> (no Kestrel, no hosted
/// services). The <c>--db</c> / <c>--config</c> args + the <c>PACKETNET_*</c> env vars are
/// honoured exactly as the host honours them, so the CLI reads/writes the very same store.
/// </summary>
/// <remarks>
/// <b>It never creates a database</b> (#738 item 1), the same rule and the same resolution as
/// <see cref="PdnAuthCli"/> (<see cref="NodeStatePaths.ResolveExistingDbPath"/>): an explicit
/// <c>--db</c> / <c>PACKETNET_DB</c>, else <c>/var/lib/packetnet/pdn.db</c>, else the working
/// directory's when it already exists. It used to resolve with the node's CREATING resolver, so
/// an <c>export</c> run from a shell whose working directory held no <c>pdn.db</c> built one,
/// seeded the N0CALL template into it, wrote THAT as the operator's pre-upgrade backup and
/// exited 0 - and an <c>import</c> wrote the config into the same orphan, reporting success as
/// the literal string <c>pdn.db</c>. This is the backup step of an upgrade, so it refuses and
/// names the paths it looked at instead, and every success and failure line names the RESOLVED
/// database so "I backed up the wrong file" is visible at a glance.
/// </remarks>
public static class PdnConfigCli
{
    /// <summary>Run the <c>config</c> subcommand. <paramref name="args"/> is the full argv
    /// (the first element is <c>config</c>). Returns a process exit code (0 = ok).</summary>
    public static async Task<int> RunAsync(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);

        if (args.Length < 2)
        {
            await Console.Error.WriteLineAsync("usage: pdn config <export|import> [args]");
            return 2;
        }

        var verb = args[1];
        return verb switch
        {
            "export" => Export(args),
            "import" => Import(args),
            _ => Usage(verb),
        };
    }

    private static int Usage(string verb)
    {
        Console.Error.WriteLine($"pdn config: unknown subcommand '{verb}' (want export | import).");
        Console.Error.WriteLine("  pdn config export [--out <path>]   write the live config as YAML to stdout/file");
        Console.Error.WriteLine("  pdn config import <path>           validate + apply a YAML file into pdn.db");
        return 2;
    }

    private static int Export(string[] args)
    {
        if (ResolveExistingDb(args, "export") is not { } dbPath)
        {
            return 1;
        }

        var provider = BootProvider(args, dbPath);
        var yaml = NodeConfigYaml.Serialize(provider.Current);

        var outPath = ArgValue(args, "--out");
        if (outPath is { Length: > 0 })
        {
            File.WriteAllText(outPath, yaml);
            Console.Error.WriteLine($"wrote config from {dbPath} to {outPath}");
        }
        else
        {
            // The YAML is the stdout payload; the provenance line goes to stderr so a
            // `pdn config export > backup.yaml` stays a clean document.
            Console.Out.Write(yaml);
            Console.Error.WriteLine($"exported config from {dbPath}");
        }
        provider.Dispose();
        return 0;
    }

    private static int Import(string[] args)
    {
        // The path is the first non-flag positional after `import`.
        var path = args.Skip(2).FirstOrDefault(a => !a.StartsWith('-'));
        if (path is not { Length: > 0 })
        {
            Console.Error.WriteLine("usage: pdn config import <path>");
            return 2;
        }
        if (!File.Exists(path))
        {
            Console.Error.WriteLine($"pdn config import: no such file: {path}");
            return 2;
        }

        if (ResolveExistingDb(args, "import") is not { } dbPath)
        {
            return 1;
        }

        var provider = BootProvider(args, dbPath);
        NodeConfig candidate;
        try
        {
            candidate = NodeConfigYaml.Parse(File.ReadAllText(path));
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"pdn config import: {path} did not parse: {ex.Message}");
            Console.Error.WriteLine($"{dbPath} is unchanged.");
            provider.Dispose();
            return 1;
        }

        if (!provider.TryApply(candidate, out var errors))
        {
            Console.Error.WriteLine($"pdn config import: {path} rejected, {dbPath} is unchanged:");
            foreach (var e in errors)
            {
                Console.Error.WriteLine($"  - {e.Path}: {e.Message}");
            }
            provider.Dispose();
            return 1;
        }

        // Name the RESOLVED database, not the literal "pdn.db" this used to print: the whole
        // point of the resolution is that the file it wrote may not be the one the operator
        // pictured (#738 item 1).
        Console.Error.WriteLine(
            $"imported {path} into {dbPath} (callsign {candidate.Identity.Callsign}, {candidate.Ports.Count} port(s)).");
        provider.Dispose();
        return 0;
    }

    /// <summary>
    /// The database this verb operates on, resolved and proven to exist - or null after
    /// printing the refusal. Identical rules to <c>pdn auth</c>
    /// (<see cref="NodeStatePaths.ResolveExistingDbPath"/>): neither verb may create a store,
    /// because a fresh one seeds the N0CALL template and would be exported as a "backup" of a
    /// node it knows nothing about.
    /// </summary>
    private static string? ResolveExistingDb(string[] args, string verb)
    {
        if (NodeStatePaths.ResolveExistingDbPath(args) is not { } dbPath)
        {
            Console.Error.WriteLine($"pdn config {verb}: no node database found. Looked at:");
            foreach (var candidate in NodeStatePaths.DefaultCandidates())
            {
                Console.Error.WriteLine($"  - {candidate}");
            }
            Console.Error.WriteLine("Name the database with --db <path> (or PACKETNET_DB) and run it again.");
            return null;
        }

        // An explicit --db / PACKETNET_DB is honoured verbatim even when it is missing, so that
        // the operator hears about the path THEY named rather than a silent fallback.
        if (!File.Exists(dbPath))
        {
            Console.Error.WriteLine($"pdn config {verb}: '{Path.GetFullPath(dbPath)}' does not exist.");
            Console.Error.WriteLine(
                "Refusing to create one: a fresh database is the N0CALL template, not this node's config.");
            Console.Error.WriteLine(
                $"The packaged node keeps its store at {NodeStatePaths.DefaultStateDirectory}/{NodeStatePaths.DbFileName}.");
            return null;
        }

        return Path.GetFullPath(dbPath);
    }

    /// <summary>Boot just the config provider over <paramref name="dbPath"/> (already resolved
    /// and proven to exist by <see cref="ResolveExistingDb"/>): the same store the host uses.
    /// Logs to stderr so an export's YAML on stdout stays clean.</summary>
    private static SqliteConfigProvider BootProvider(string[] args, string dbPath)
    {
        using var loggers = LoggerFactory.Create(b =>
            b.AddConsole(o => o.LogToStandardErrorThreshold = LogLevel.Trace));

        var configPath = ResolveConfigPath(args);
        var seedPath = Env("PACKETNET_CONFIG_SEED");
        var templatePath = Env("PACKETNET_CONFIG_TEMPLATE") is { Length: > 0 } t
            ? t
            : "/usr/share/packetnet/packetnet.yaml.example";

        var store = new SqliteConfigStore(dbPath, TimeProvider.System, loggers.CreateLogger<SqliteConfigStore>());
        return new SqliteConfigProvider(
            store,
            configPath,
            seedPath,
            templatePath,
            markerDir: Path.GetDirectoryName(Path.GetFullPath(dbPath)),
            TimeProvider.System,
            loggers.CreateLogger<SqliteConfigProvider>());
    }

    private static string? ArgValue(string[] args, string flag)
    {
        for (int i = 0; i < args.Length - 1; i++)
        {
            if (args[i] == flag)
            {
                return args[i + 1];
            }
        }
        return null;
    }

    private static string? Env(string name)
    {
        var v = Environment.GetEnvironmentVariable(name);
        return string.IsNullOrWhiteSpace(v) ? null : v;
    }

    /// <summary>The database <c>export</c> / <c>import</c> would operate on: exactly
    /// <c>pdn auth</c>'s existing-only resolution (<see cref="NodeStatePaths"/>), null when no
    /// candidate exists. Exposed to the test suite so the resolution is pinned rather than
    /// inferred.</summary>
    internal static string? ResolveDbPath(
        string[] args, string? stateDirectory = null, string? workingDirectory = null) =>
        NodeStatePaths.ResolveExistingDbPath(args, stateDirectory, workingDirectory);

    // The CLI resolves --config / PACKETNET_CONFIG identically to Program.cs's resolver so it
    // reads the exact same legacy YAML the running host would.
    private static string ResolveConfigPath(string[] args)
    {
        var v = ArgValue(args, "--config") ?? ArgValue(args, "-c");
        if (v is { Length: > 0 })
        {
            return v;
        }

        return Env("PACKETNET_CONFIG") ?? Path.Combine(Directory.GetCurrentDirectory(), "packetnet.yaml");
    }
}
