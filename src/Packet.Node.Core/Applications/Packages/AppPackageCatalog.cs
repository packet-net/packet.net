using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Packet.Node.Core.Configuration;
using Packet.Node.Core.Console;

namespace Packet.Node.Core.Applications.Packages;

/// <summary>
/// The app-package catalog (<see cref="IAppPackageCatalog"/>): scans the package roots for
/// <c>pdn-app.yaml</c> manifests, validates each against the contract
/// (<c>docs/app-packages.md</c>), and merges in the owner's <c>apps:</c> override. Total and
/// side-effect free: <see cref="Discover"/> never throws for a bad package (it yields an
/// <see cref="DiscoveredAppPackage.Error"/> entry instead, so the owner sees the problem in
/// the UI rather than losing the whole inventory) and never touches the filesystem beyond
/// reading — directories (state dirs included) are created by the consumers that use them.
/// </summary>
public sealed partial class AppPackageCatalog(ILoggerFactory loggerFactory) : IAppPackageCatalog
{
    /// <summary>The manifest file every package directory must carry.</summary>
    public const string ManifestFileName = "pdn-app.yaml";

    /// <summary>The standard discovery roots, scanned in order — later roots win on id
    /// collision (an owner-installed package overrides a distro-installed one). Replaced
    /// entirely by <see cref="NodeConfig.AppPackageRoots"/> when that is set.</summary>
    public static readonly IReadOnlyList<string> DefaultRoots =
        ["/usr/share/packetnet/apps", "/var/lib/packetnet/apps"];

    private const string StateRootDir = "/var/lib/packetnet/apps";

    private readonly ILogger<AppPackageCatalog> log = loggerFactory.CreateLogger<AppPackageCatalog>();

    /// <inheritdoc/>
    public IReadOnlyList<DiscoveredAppPackage> Discover(NodeConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);

        // When the roots are overridden (dev/test), state dirs live inside each package dir
        // instead of under /var/lib — a test run must never compute paths into system dirs.
        bool rootsOverridden = config.AppPackageRoots is not null;
        IReadOnlyList<string> roots = config.AppPackageRoots ?? DefaultRoots;

        var drafts = ScanRoots(roots, rootsOverridden, config);

        // Cross-package rule: two packages resolving the same effective session verb can't
        // both go live — mark BOTH (the owner disambiguates with an apps[].match override).
        foreach (var group in drafts.Where(d => d.EffectiveVerb is not null)
                     .GroupBy(d => d.EffectiveVerb!, StringComparer.OrdinalIgnoreCase)
                     .Where(g => g.Count() > 1))
        {
            foreach (var draft in group)
            {
                var others = string.Join(", ", group.Where(o => !ReferenceEquals(o, draft))
                    .Select(o => $"'{o.Id}'"));
                draft.Problems.Add(
                    $"session verb '{draft.EffectiveVerb}' collides with package(s) {others} — " +
                    "override apps[].match to disambiguate.");
            }
        }

        var result = new List<DiscoveredAppPackage>(drafts.Count);
        foreach (var draft in drafts)
        {
            var entry = draft.Build();
            if (entry.Error is not null)
            {
                LogBrokenPackage(entry.Id, entry.PackageDir, entry.Error);
            }
            result.Add(entry);
        }

        int enabled = result.Count(p => p.Enabled);
        int broken = result.Count(p => p.Error is not null);
        LogDiscovered(result.Count, enabled, broken);
        return result;
    }

    /// <summary>Scan every root's immediate subdirectories for a manifest; later roots
    /// replace an earlier entry with the same directory name (the package id).</summary>
    private List<PackageDraft> ScanRoots(IReadOnlyList<string> roots, bool rootsOverridden, NodeConfig config)
    {
        var byId = new Dictionary<string, string>(StringComparer.Ordinal);  // dir name -> package dir
        var order = new List<string>();                                     // first-seen order

        foreach (var root in roots)
        {
            if (string.IsNullOrWhiteSpace(root))
            {
                continue;   // flagged by NodeConfigValidator; discovery stays total regardless.
            }

            string[] subdirs;
            try
            {
                subdirs = Directory.Exists(root) ? Directory.GetDirectories(root) : [];
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                LogUnscannableRoot(ex, root);
                continue;
            }

            Array.Sort(subdirs, StringComparer.Ordinal);   // deterministic discovery order
            foreach (var dir in subdirs)
            {
                if (!File.Exists(Path.Combine(dir, ManifestFileName)))
                {
                    continue;   // not a package — an unrelated directory is fine to ignore.
                }

                var name = Path.GetFileName(dir);
                if (byId.TryAdd(name, dir))
                {
                    order.Add(name);
                }
                else
                {
                    byId[name] = dir;   // later root wins on id collision.
                }
            }
        }

        var drafts = new List<PackageDraft>(order.Count);
        foreach (var name in order)
        {
            drafts.Add(InspectPackage(name, byId[name], rootsOverridden, config));
        }
        return drafts;
    }

    /// <summary>Parse + validate one package directory and resolve its owner override. Any
    /// problem lands in <see cref="PackageDraft.Problems"/> — never thrown.</summary>
    private static PackageDraft InspectPackage(string dirName, string packageDir, bool rootsOverridden, NodeConfig config)
    {
        var draft = new PackageDraft
        {
            Id = dirName,
            PackageDir = packageDir,
            // The state-dir convention: /var/lib/packetnet/apps/<id> normally (for an
            // owner-installed package that is also the package dir — deliberate); under an
            // overridden root, <packageDir>/state so tests never compute system paths.
            // Computed only — Discover is a pure read; the supervisor/host create on use.
            StateDir = rootsOverridden
                ? Path.Combine(packageDir, "state")
                : Path.Combine(StateRootDir, dirName),
            Override = config.Apps.FirstOrDefault(a =>
                string.Equals(a.Id, dirName, StringComparison.OrdinalIgnoreCase)),
        };

        try
        {
            draft.Manifest = AppPackageManifestYaml.Parse(
                File.ReadAllText(Path.Combine(packageDir, ManifestFileName)));
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException)
        {
            draft.Problems.Add(ex.Message);
            return draft;
        }

        ValidateManifest(draft, dirName, config);
        return draft;
    }

    private static void ValidateManifest(PackageDraft draft, string dirName, NodeConfig config)
    {
        var manifest = draft.Manifest!;
        var problems = draft.Problems;

        if (manifest.Manifest != 1)
        {
            problems.Add($"manifest: schema version must be 1 (found {manifest.Manifest}).");
        }

        // The id rules: required, lowercase [a-z0-9-], equal to the directory name. The id is
        // nominally `required`, but YAML binding can leave it null — validate, don't trust.
        if (string.IsNullOrWhiteSpace(manifest.Id))
        {
            problems.Add("id: is required.");
        }
        else if (!IdPattern().IsMatch(manifest.Id))
        {
            problems.Add($"id: '{manifest.Id}' must be lowercase [a-z0-9-].");
        }
        else if (!string.Equals(manifest.Id, dirName, StringComparison.Ordinal))
        {
            problems.Add($"id: '{manifest.Id}' must equal the package directory name '{dirName}'.");
        }

        if (manifest.Session is null && manifest.Service is null && manifest.Ui is null)
        {
            problems.Add("manifest must declare at least one of session, service, or ui.");
        }

        if (manifest.Session is { } session)
        {
            if (string.IsNullOrWhiteSpace(session.Match))
            {
                problems.Add("session.match: the console verb is required.");
            }
            if (session.Kind == ApplicationKind.Process && string.IsNullOrWhiteSpace(session.Command))
            {
                problems.Add("session.command: required when session.kind is process.");
            }
            if (session.Kind == ApplicationKind.Socket && string.IsNullOrWhiteSpace(session.SocketPath))
            {
                problems.Add("session.socketPath: required when session.kind is socket.");
            }
        }

        if (manifest.Service is { } service && string.IsNullOrWhiteSpace(service.Command))
        {
            problems.Add("service.command: is required.");
        }

        // Same rule as ApplicationConfigValidator: the gateway reverse-proxies to upstream,
        // so anything but an absolute http(s) URL is unusable config.
        if (manifest.Ui is { } ui && !IsAbsoluteHttpUrl(ui.Upstream))
        {
            problems.Add($"ui.upstream: '{ui.Upstream}' must be an absolute http(s) URL (e.g. http://127.0.0.1:9090).");
        }

        // Identity collision across the two sources — the contract makes this an error
        // (docs/app-packages.md § Owner state): pdn can't serve two apps under one id.
        var inlineIdClash = config.Applications.FirstOrDefault(a =>
            string.Equals(a.Id, dirName, StringComparison.OrdinalIgnoreCase));
        if (inlineIdClash is not null)
        {
            problems.Add($"id: '{dirName}' collides with the inline applications: entry '{inlineIdClash.Id}' — remove one.");
        }

        // The effective session verb (owner override wins over the manifest) — checked
        // against the built-in console verbs and the inline applications here; against the
        // other packages' effective verbs in the cross-package pass.
        var verb = (draft.Override?.Match ?? manifest.Session?.Match)?.Trim();
        if (manifest.Session is not null && !string.IsNullOrWhiteSpace(verb))
        {
            draft.EffectiveVerb = verb;

            if (NodeCommandParser.Parse(verb) is not (UnknownCommand or EmptyCommand))
            {
                problems.Add($"session verb '{verb}' collides with a built-in console verb " +
                    "(CONNECT/BYE/NODES/INFO/HELP/SYSOP/SESSIONS/KICK/PORT/RELOAD or an abbreviation) — pick another.");
            }

            var inlineVerbClash = config.Applications.FirstOrDefault(a =>
                !string.IsNullOrWhiteSpace(a.Match)
                && string.Equals(a.Match.Trim(), verb, StringComparison.OrdinalIgnoreCase));
            if (inlineVerbClash is not null)
            {
                problems.Add($"session verb '{verb}' collides with inline application '{inlineVerbClash.Id}'.");
            }
        }
    }

    private static bool IsAbsoluteHttpUrl(string? url) =>
        !string.IsNullOrWhiteSpace(url)
        && Uri.TryCreate(url, UriKind.Absolute, out var u)
        && (u.Scheme == Uri.UriSchemeHttp || u.Scheme == Uri.UriSchemeHttps);

    [GeneratedRegex("^[a-z0-9-]+$")]
    private static partial Regex IdPattern();

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "App package '{Id}' ({PackageDir}) is broken and stays disabled: {Error}")]
    private partial void LogBrokenPackage(string id, string packageDir, string error);

    [LoggerMessage(Level = LogLevel.Debug,
        Message = "Discovered {Count} app package(s) ({Enabled} enabled, {Broken} broken).")]
    private partial void LogDiscovered(int count, int enabled, int broken);

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "Cannot scan app package root {Root}; skipping it.")]
    private partial void LogUnscannableRoot(Exception ex, string root);

    /// <summary>Mutable working shape for one package while the per-package and cross-package
    /// rules accumulate problems; <see cref="Build"/> freezes it into the public record.</summary>
    private sealed class PackageDraft
    {
        public required string Id { get; init; }
        public required string PackageDir { get; init; }
        public required string StateDir { get; init; }
        public AppPackageManifest? Manifest { get; set; }
        public AppOverrideConfig? Override { get; init; }
        public string? EffectiveVerb { get; set; }
        public List<string> Problems { get; } = [];

        public DiscoveredAppPackage Build()
        {
            var error = Problems.Count == 0 ? null : string.Join(" ", Problems);
            return new DiscoveredAppPackage
            {
                Id = Id,
                PackageDir = PackageDir,
                StateDir = StateDir,
                Manifest = Manifest,
                Override = Override,
                // Broken never runs — the error forces the trust switch off regardless of
                // what the owner's apps: entry says.
                Enabled = error is null && (Override?.Enabled ?? false),
                Error = error,
            };
        }
    }
}
