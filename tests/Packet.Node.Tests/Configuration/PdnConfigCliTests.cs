using Packet.Node.Cli;
using Packet.Node.Core.Configuration;
using Packet.Node.Tests.Support;

namespace Packet.Node.Tests.Configuration;

/// <summary>
/// <c>pdn config export|import</c> - the headless round-trip that IS the operator's pre-upgrade
/// backup step (#738 item 1, found by the GB7RDG upgrade rehearsal).
/// </summary>
/// <remarks>
/// Both verbs used to resolve the database with the node's CREATING resolver, so an
/// <c>export</c> run from a directory holding no <c>pdn.db</c> built one, seeded the N0CALL
/// template into it, wrote THAT as the "backup" and exited 0; <c>import</c> wrote into the same
/// orphan and reported success as the literal string <c>pdn.db</c>, never the resolved path.
/// They now share <c>pdn auth</c>'s existing-only resolution (<c>NodeStatePaths</c>) and name
/// the resolved database in every line they print - the same class of bug as #727 item 3.
/// </remarks>
[Trait("Category", "Node")]
public sealed class PdnConfigCliTests : IDisposable
{
    private readonly string dir;
    private readonly string dbPath;
    private readonly string legacyYaml;
    private readonly string savedCwd;
    private readonly Dictionary<string, string?> savedEnv = [];

    private const string SeedYaml = """
        schemaVersion: 1
        identity:
          callsign: M0LTE-7
          alias: LONDON
        ports: []
        management:
          auth:
            enabled: false
          telnet:
            enabled: false
          http:
            bind: 127.0.0.1
            port: 8080
        """;

    public PdnConfigCliTests()
    {
        dir = TestPaths.NewPath("pdn-configcli");
        Directory.CreateDirectory(dir);
        dbPath = Path.Combine(dir, "pdn.db");
        legacyYaml = Path.Combine(dir, "packetnet.yaml");
        savedCwd = Directory.GetCurrentDirectory();

        // The resolution under test is "what the operator's shell gives us": an ambient
        // PACKETNET_DB (several host-boot suites set one and never clear it) short-circuits the
        // whole state-dir/cwd walk, so whether these cases saw one would come down to class
        // ORDER. Clear the whole PACKETNET_* set for the duration and put it back.
        foreach (var name in (string[])
                 ["PACKETNET_DB", "PACKETNET_CONFIG", "PACKETNET_CONFIG_SEED", "PACKETNET_CONFIG_TEMPLATE"])
        {
            savedEnv[name] = Environment.GetEnvironmentVariable(name);
            Environment.SetEnvironmentVariable(name, null);
        }
    }

    /// <summary>Build a real node database at <see cref="dbPath"/> holding
    /// <paramref name="callsign"/>'s config - the store the verbs must operate on.</summary>
    private void SeedDatabase(string callsign = "M0LTE-7")
    {
        File.WriteAllText(legacyYaml, SeedYaml.Replace("M0LTE-7", callsign, StringComparison.Ordinal));
        using var provider = new SqliteConfigProvider(
            new SqliteConfigStore(dbPath, TimeProvider.System),
            configPath: legacyYaml,
            markerDir: dir,
            clock: TimeProvider.System);
        provider.Current.Identity.Callsign.Should().Be(callsign);
    }

    /// <summary>Run a verb with stdout/stderr captured (the YAML payload and the operator
    /// lines are separate surfaces, and the resolved-path assertions are about stderr).</summary>
    private static async Task<(int Exit, string Stdout, string Stderr)> RunAsync(params string[] args)
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var savedOut = System.Console.Out;
        var savedError = System.Console.Error;
        try
        {
            System.Console.SetOut(stdout);
            System.Console.SetError(stderr);
            var exit = await PdnConfigCli.RunAsync(args);
            return (exit, stdout.ToString(), stderr.ToString());
        }
        finally
        {
            System.Console.SetOut(savedOut);
            System.Console.SetError(savedError);
        }
    }

    // --- the refusals: never create a database, never write a phantom backup --------------

    [SkippableFact]
    public async Task Export_from_a_directory_with_no_database_refuses_and_writes_nothing()
    {
        // The rehearsal's finding, verbatim: `pdn config export --out backup.yaml` from a shell
        // whose working directory holds no pdn.db used to CREATE one, seed the N0CALL template
        // and write that as the operator's pre-upgrade backup, exit 0.
        Skip.If(File.Exists(Path.Combine(NodeStatePaths.DefaultStateDirectory, NodeStatePaths.DbFileName)),
            "this box has a real node database in the packaged state directory, which is a legitimate candidate");

        var backup = Path.Combine(dir, "backup.yaml");
        Directory.SetCurrentDirectory(dir);

        var (exit, _, stderr) = await RunAsync("config", "export", "--out", backup);

        exit.Should().Be(1, "refusing is the only honest answer - there is no config here to back up");
        File.Exists(dbPath).Should().BeFalse("the verb must never create the database it was asked to read");
        File.Exists(backup).Should().BeFalse("a backup of a database that does not exist is worse than none");
        stderr.Should().Contain("no node database found");
        stderr.Should().Contain(Path.Combine(NodeStatePaths.DefaultStateDirectory, NodeStatePaths.DbFileName));
        stderr.Should().Contain("--db");
    }

    [Fact]
    public async Task Export_against_a_named_database_that_is_not_there_refuses_and_names_it()
    {
        var missing = Path.Combine(dir, "not-here.db");
        var backup = Path.Combine(dir, "backup.yaml");

        var (exit, _, stderr) = await RunAsync("config", "export", "--db", missing, "--out", backup);

        exit.Should().Be(1);
        File.Exists(missing).Should().BeFalse("an explicitly named database is never created either");
        File.Exists(backup).Should().BeFalse();
        stderr.Should().Contain(missing, "the operator named that path - report it back");
        stderr.Should().Contain("Refusing to create one");
    }

    [Fact]
    public async Task Import_against_a_database_that_is_not_there_refuses_and_creates_nothing()
    {
        var missing = Path.Combine(dir, "not-here.db");
        var candidate = Path.Combine(dir, "candidate.yaml");
        File.WriteAllText(candidate, SeedYaml);

        var (exit, _, stderr) = await RunAsync("config", "import", candidate, "--db", missing);

        exit.Should().Be(1, "importing into a brand-new database writes the config nowhere the node reads");
        File.Exists(missing).Should().BeFalse();
        stderr.Should().Contain(missing);
        stderr.Should().Contain("Refusing to create one");
    }

    // --- the success lines name the RESOLVED database --------------------------------------

    [Fact]
    public async Task Export_to_a_file_names_the_database_it_read_and_the_file_it_wrote()
    {
        SeedDatabase();
        var backup = Path.Combine(dir, "backup.yaml");

        var (exit, _, stderr) = await RunAsync("config", "export", "--db", dbPath, "--out", backup);

        exit.Should().Be(0);
        stderr.Should().Contain(dbPath, "which database was backed up is the fact the operator needs");
        stderr.Should().Contain(backup);
        NodeConfigYaml.Parse(File.ReadAllText(backup)).Identity.Callsign.Should().Be("M0LTE-7");
    }

    [Fact]
    public async Task Export_to_stdout_keeps_the_YAML_clean_and_names_the_database_on_stderr()
    {
        SeedDatabase();

        var (exit, stdout, stderr) = await RunAsync("config", "export", "--db", dbPath);

        exit.Should().Be(0);
        // `pdn config export > backup.yaml` must still produce a parseable document.
        NodeConfigYaml.Parse(stdout).Identity.Callsign.Should().Be("M0LTE-7");
        stdout.Should().NotContain(dbPath, "the provenance line belongs on stderr, not in the document");
        stderr.Should().Contain(dbPath);
    }

    [Fact]
    public async Task Import_reports_the_resolved_path_not_the_literal_pdn_db()
    {
        SeedDatabase();
        var edited = Path.Combine(dir, "edited.yaml");
        File.WriteAllText(edited, SeedYaml.Replace("M0LTE-7", "GB7RDG-1", StringComparison.Ordinal));

        var (exit, _, stderr) = await RunAsync("config", "import", edited, "--db", dbPath);

        exit.Should().Be(0);
        stderr.Should().Contain($"into {dbPath}", "the old line said \"into pdn.db\" whatever it had written");
        stderr.Should().Contain("GB7RDG-1");
    }

    // --- the round-trip the whole verb pair exists for --------------------------------------

    [Fact]
    public async Task Export_edit_import_round_trips_through_the_database()
    {
        SeedDatabase();
        var backup = Path.Combine(dir, "backup.yaml");

        (await RunAsync("config", "export", "--db", dbPath, "--out", backup)).Exit.Should().Be(0);

        File.WriteAllText(backup, File.ReadAllText(backup).Replace("M0LTE-7", "GB7RDG-1", StringComparison.Ordinal));
        (await RunAsync("config", "import", backup, "--db", dbPath)).Exit.Should().Be(0);

        // Re-export from the same store: the edit landed where the node will read it.
        var (exit, stdout, _) = await RunAsync("config", "export", "--db", dbPath);
        exit.Should().Be(0);
        NodeConfigYaml.Parse(stdout).Identity.Callsign.Should().Be("GB7RDG-1");
    }

    [Fact]
    public async Task An_unparseable_or_rejected_file_leaves_the_database_alone_and_says_so()
    {
        SeedDatabase();
        var garbage = Path.Combine(dir, "garbage.yaml");
        File.WriteAllText(garbage, "identity: [this is not a config\n");

        var (exit, _, stderr) = await RunAsync("config", "import", garbage, "--db", dbPath);

        exit.Should().Be(1);
        stderr.Should().Contain($"{dbPath} is unchanged");

        var (_, stdout, _) = await RunAsync("config", "export", "--db", dbPath);
        NodeConfigYaml.Parse(stdout).Identity.Callsign.Should().Be("M0LTE-7");
    }

    // --- the resolution itself, pinned exactly as `pdn auth`'s is --------------------------

    [Fact]
    public void The_state_directory_database_wins_over_the_working_directory()
    {
        var stateDir = Path.Combine(dir, "state");
        var cwd = Path.Combine(dir, "cwd");
        Directory.CreateDirectory(stateDir);
        Directory.CreateDirectory(cwd);
        var stateDb = Path.Combine(stateDir, "pdn.db");
        File.WriteAllText(stateDb, string.Empty);
        File.WriteAllText(Path.Combine(cwd, "pdn.db"), string.Empty);

        // The packaged unit sets WorkingDirectory=/var/lib/packetnet for the SERVICE only, so an
        // interactive verb never inherits it - the state dir is looked at first, the shell's cwd
        // only after, and only when a database is already there.
        PdnConfigCli.ResolveDbPath([], stateDir, cwd).Should().Be(stateDb);
        PdnConfigCli.ResolveDbPath([], Path.Combine(dir, "no-state"), cwd)
            .Should().Be(Path.Combine(cwd, "pdn.db"));
        PdnConfigCli.ResolveDbPath([], Path.Combine(dir, "no-state"), Path.Combine(dir, "no-cwd"))
            .Should().BeNull("with no candidate at all the verb has something specific to refuse with");
    }

    public void Dispose()
    {
        Directory.SetCurrentDirectory(savedCwd);
        foreach (var (name, value) in savedEnv)
        {
            Environment.SetEnvironmentVariable(name, value);
        }
        try { Directory.Delete(dir, recursive: true); } catch { /* best effort */ }
    }
}
