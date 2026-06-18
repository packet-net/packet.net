using Microsoft.Extensions.Logging;
using Packet.Node.Core.Configuration;

namespace Packet.Node.Cli;

/// <summary>
/// The <c>pdn config</c> subcommand family: the headless inspect / diff / restore CLI for
/// config-in-DB (#473). Config now lives in <c>pdn.db</c>, not a hand-editable file, so an
/// operator with shell access needs a way to round-trip it as text:
/// <list type="bullet">
/// <item><c>pdn config export [--out &lt;path&gt;]</c> — boot the provider, read
/// <see cref="IConfigProvider.Current"/>, write <see cref="NodeConfigYaml"/> to stdout (or
/// a file). The export-edit-import workflow's first leg + a backup path.</item>
/// <item><c>pdn config import &lt;path&gt;</c> — parse + validate + apply a YAML file
/// through the SAME write seam (<see cref="IWritableConfigProvider.TryApply"/>) the web API
/// uses, persisting to the DB. The explicit apply that replaces the old hot file-watch.</item>
/// </list>
/// Like <c>pdn mcp</c> this short-circuits BEFORE the web host is built: it boots ONLY the
/// <see cref="SqliteConfigProvider"/> over the resolved <c>pdn.db</c> (no Kestrel, no hosted
/// services). The <c>--db</c> / <c>--config</c> args + the <c>PACKETNET_*</c> env vars are
/// honoured exactly as the host honours them, so the CLI reads/writes the very same store.
/// </summary>
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
        var provider = BootProvider(args);
        var yaml = NodeConfigYaml.Serialize(provider.Current);

        var outPath = ArgValue(args, "--out");
        if (outPath is { Length: > 0 })
        {
            File.WriteAllText(outPath, yaml);
            Console.Error.WriteLine($"wrote config to {outPath}");
        }
        else
        {
            Console.Out.Write(yaml);
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

        var provider = BootProvider(args);
        NodeConfig candidate;
        try
        {
            candidate = NodeConfigYaml.Parse(File.ReadAllText(path));
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"pdn config import: {path} did not parse: {ex.Message}");
            provider.Dispose();
            return 1;
        }

        if (!provider.TryApply(candidate, out var errors))
        {
            Console.Error.WriteLine($"pdn config import: {path} rejected:");
            foreach (var e in errors)
            {
                Console.Error.WriteLine($"  - {e.Path}: {e.Message}");
            }
            provider.Dispose();
            return 1;
        }

        Console.Error.WriteLine(
            $"imported {path} into pdn.db (callsign {candidate.Identity.Callsign}, {candidate.Ports.Count} port(s)).");
        provider.Dispose();
        return 0;
    }

    /// <summary>Boot just the config provider over the resolved <c>pdn.db</c> — the same
    /// store the host uses. Logs to stderr so an export's YAML on stdout stays clean.</summary>
    private static SqliteConfigProvider BootProvider(string[] args)
    {
        using var loggers = LoggerFactory.Create(b =>
            b.AddConsole(o => o.LogToStandardErrorThreshold = LogLevel.Trace));

        var dbPath = ResolveDbPath(args);
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

    // The CLI resolves --db / --config / PACKETNET_* identically to Program.cs's resolvers
    // so it operates on the exact same pdn.db + legacy YAML the running host would.
    private static string ResolveDbPath(string[] args)
    {
        var v = ArgValue(args, "--db");
        if (v is { Length: > 0 }) return v;
        return Env("PACKETNET_DB") ?? Path.Combine(Directory.GetCurrentDirectory(), "pdn.db");
    }

    private static string ResolveConfigPath(string[] args)
    {
        var v = ArgValue(args, "--config") ?? ArgValue(args, "-c");
        if (v is { Length: > 0 }) return v;
        return Env("PACKETNET_CONFIG") ?? Path.Combine(Directory.GetCurrentDirectory(), "packetnet.yaml");
    }
}
