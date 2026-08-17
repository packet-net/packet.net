using Packet.Node.Core.Applications.Packages;
using Packet.Node.Core.Configuration;
using Packet.Node.Tests.Support;

namespace Packet.Node.Tests.Applications.Packages;

/// <summary>
/// A hand-rolled <see cref="IAppPackageCatalog"/> returning canned
/// <see cref="DiscoveredAppPackage"/> snapshots — the supervisor / union tests own the catalog
/// seam without touching the real discovery/YAML implementation (a sibling deliverable).
/// <see cref="Set"/> swaps the snapshot, standing in for "the owner edited config / dropped a
/// package and the catalog re-scanned".
/// </summary>
internal sealed class FakeAppPackageCatalog : IAppPackageCatalog
{
    private readonly object gate = new();
    private IReadOnlyList<DiscoveredAppPackage> packages = [];

    public void Set(params DiscoveredAppPackage[] discovered)
    {
        lock (gate)
        {
            packages = discovered;
        }
    }

    public IReadOnlyList<DiscoveredAppPackage> Discover(NodeConfig config)
    {
        lock (gate)
        {
            return packages;
        }
    }
}

/// <summary>
/// One on-disk package fixture: a real temp package dir (where scripts live, exercising the
/// package-dir path-resolution rule) and a state dir path the code under test is expected to
/// create. Children are real <c>/bin/sh</c> processes — Linux-only, like the existing app
/// platform tests (CI is Linux).
/// </summary>
internal sealed class TempAppPackage : IDisposable
{
    public TempAppPackage(string id)
    {
        Id = id;
        Root = TestPaths.NewPath("pdn-pkg");
        PackageDir = Path.Combine(Root, id);
        StateDir = Path.Combine(Root, "state", id);
        Directory.CreateDirectory(PackageDir);
        // StateDir is deliberately NOT created here — creating it is the code under test's job.
    }

    public string Id { get; }
    public string Root { get; }
    public string PackageDir { get; }
    public string StateDir { get; }

    /// <summary>Write an executable script into the package dir; returns its absolute path.</summary>
    public string WriteScript(string name, string body)
    {
        var path = Path.Combine(PackageDir, name);
        File.WriteAllText(path, body.ReplaceLineEndings("\n"));
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(path,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
                UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
        }
        return path;
    }

    /// <summary>An absolute path under the (supervisor-created) state dir.</summary>
    public string StatePath(string name) => Path.Combine(StateDir, name);

    /// <summary>A discovered package whose manifest declares a <c>service:</c> running
    /// <c>/bin/sh &lt;scriptName&gt; [extraArgs…]</c> (the script name stays relative, so the
    /// spawn also exercises the package-dir resolution rule).</summary>
    public DiscoveredAppPackage Service(
        string scriptName,
        IReadOnlyList<string>? extraArgs = null,
        bool enabled = true,
        AppServiceRestart restart = AppServiceRestart.OnFailure,
        AppServiceManaged managed = AppServiceManaged.Pdn,
        IReadOnlyDictionary<string, string>? environment = null,
        AppOverrideConfig? @override = null,
        string? error = null)
    {
        var manifest = new AppPackageManifest
        {
            Manifest = 1,
            Id = Id,
            Service = new AppServiceSpec
            {
                Command = "/bin/sh",
                Args = [scriptName, .. extraArgs ?? []],
                Restart = restart,
                Managed = managed,
                Environment = environment ?? new Dictionary<string, string>(),
            },
        };
        return Discovered(manifest, enabled, @override, error);
    }

    /// <summary>A discovered package around an arbitrary manifest.</summary>
    public DiscoveredAppPackage Discovered(
        AppPackageManifest manifest,
        bool enabled = true,
        AppOverrideConfig? @override = null,
        string? error = null) => new()
        {
            Id = Id,
            Manifest = manifest,
            PackageDir = PackageDir,
            StateDir = StateDir,
            Enabled = enabled,
            Override = @override,
            Error = error,
        };

    public void Dispose()
    {
        try
        {
            Directory.Delete(Root, recursive: true);
        }
        catch (Exception)
        {
            // Best-effort temp cleanup only.
        }
    }
}

internal static class PackageTestSupport
{
    /// <summary>A minimal valid node config (the RHP block is what the supervisor reads).</summary>
    public static NodeConfig Node(bool rhpEnabled = false) => new()
    {
        Identity = new Identity { Callsign = "M0LTE-1" },
        Rhp = new RhpConfig { Enabled = rhpEnabled, Bind = "127.0.0.1", Port = 9123 },
    };

    /// <summary>True once no live process holds <paramref name="pid"/> (Linux: its /proc entry
    /// is gone — direct children are reaped by the supervisor, grandchildren by init).</summary>
    public static bool ProcessGone(int pid) => !Directory.Exists($"/proc/{pid}");

    /// <summary>Whether <c>setsid(1)</c> is available — when it is, the supervisor spawns each
    /// service as a process-group leader and a stop signals the whole group (grandchildren
    /// included).</summary>
    public static bool SetsidAvailable => File.Exists("/usr/bin/setsid") || File.Exists("/bin/setsid");
}
