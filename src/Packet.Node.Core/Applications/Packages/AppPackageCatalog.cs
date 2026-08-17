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

    /// <summary>The roots <see cref="Discover"/> would scan for <paramref name="config"/>: the
    /// config's <c>appPackageRoots:</c> when set, else <see cref="DefaultRoots"/>. Exposed so
    /// the "you configured an app that is not installed" report can say where we looked.</summary>
    public static IReadOnlyList<string> RootsFor(NodeConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        return config.AppPackageRoots ?? DefaultRoots;
    }

    /// <summary>
    /// The owner's <c>apps:</c> overrides that name a package no root holds, in config order:
    /// a configured app whose payload is not installed.
    /// </summary>
    /// <remarks>
    /// Nothing used to say this out loud: <see cref="Discover"/> only reports what it FOUND, the
    /// inventory only projects discovered packages plus inline entries, and the panel therefore
    /// said "No app packages" for a node with four enabled apps whose payloads had gone missing
    /// (#738 item 2; a lost payload looked exactly like a healthy idle node). The supervisor
    /// warns per enabled entry on every bring-up and reconcile, and the API projects the whole
    /// set as not-installed rows. An id that matches an inline <c>applications:</c> entry is not
    /// missing; it is that entry (the catalog flags the collision separately).
    /// </remarks>
    public static IReadOnlyList<AppOverrideConfig> ConfiguredButNotInstalled(
        NodeConfig config, IReadOnlyList<DiscoveredAppPackage> discovered)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(discovered);

        return [.. config.Apps.Where(app =>
            !string.IsNullOrWhiteSpace(app.Id)
            && !discovered.Any(p => string.Equals(p.Id, app.Id, StringComparison.OrdinalIgnoreCase))
            && !config.Applications.Any(a => string.Equals(a.Id, app.Id, StringComparison.OrdinalIgnoreCase)))];
    }

    /// <summary>The tailnet listen port reserved for the web reverse-proxy (the sidecar's own
    /// <c>ListenTLS(":443")</c>) — an app's <c>forward.listen</c> may not claim it.</summary>
    private const int ReservedWebPort = 443;

    /// <summary>The loopback hosts a forward target may name — pdn proxies the tailnet only to
    /// the local host, never to an arbitrary one.</summary>
    private static readonly HashSet<string> LoopbackHosts =
        new(["127.0.0.1", "::1", "localhost"], StringComparer.OrdinalIgnoreCase);

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

        // Cross-package rule: two packages resolving the same effective command verb can't
        // both go live — mark BOTH (the owner disambiguates with an apps[].command override).
        foreach (var group in drafts.Where(d => d.EffectiveVerb is not null)
                     .GroupBy(d => d.EffectiveVerb!, StringComparer.OrdinalIgnoreCase)
                     .Where(g => g.Count() > 1))
        {
            foreach (var draft in group)
            {
                var others = string.Join(", ", group.Where(o => !ReferenceEquals(o, draft))
                    .Select(o => $"'{o.Id}'"));
                draft.Problems.Add(
                    $"command verb '{draft.EffectiveVerb}' collides with package(s) {others} - " +
                    "override apps[].command to disambiguate.");
            }
        }

        // Cross-package rule (same pattern as the session-verb collision): a tailnet listen
        // port can be exposed by only one node — two packages claiming the same forward.listen
        // can't both go live, so mark BOTH. Only forwards whose listen passed the per-package
        // range/reserved checks participate (a draft already flagged for a bad listen is broken
        // regardless, and we don't want a second confusing message on top).
        foreach (var group in drafts
                     .SelectMany(d => d.ValidForwardListens().Select(port => (Draft: d, Port: port)))
                     .GroupBy(x => x.Port)
                     .Where(g => g.Select(x => x.Draft).Distinct().Count() > 1))
        {
            var port = group.Key;
            var ids = group.Select(x => x.Draft).Distinct().ToList();
            foreach (var draft in ids)
            {
                var others = string.Join(", ", ids.Where(o => !ReferenceEquals(o, draft))
                    .Select(o => $"'{o.Id}'"));
                draft.Problems.Add(
                    $"forward listen port {port} collides with package(s) {others} - " +
                    "a tailnet port can be exposed by only one app.");
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
            // The console verb is now packet.command (owner-overridable), not a session field;
            // a session app reachable only by callsign/alias may legitimately omit it.
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

        // The forward: block (docs/network-access.md § App-declared port forwarding). pdn must
        // not proxy the tailnet to arbitrary hosts, so the target is loopback-only; 443 is the
        // web reverse-proxy's, so a listen of 443 is reserved. Each forward validates
        // independently. The Tls enum is closed at parse time, so an out-of-set value never
        // reaches here — but a default(ForwardTls) (e.g. a programmatically-built spec) still
        // checks below for completeness.
        for (var i = 0; i < manifest.Forward.Count; i++)
        {
            var fwd = manifest.Forward[i];
            if (fwd.Listen is < 1 or > 65535)
            {
                problems.Add($"forward[{i}].listen: {fwd.Listen} must be in 1..65535.");
            }
            else if (fwd.Listen == ReservedWebPort)
            {
                problems.Add($"forward[{i}].listen: {ReservedWebPort} is reserved for the web reverse-proxy - pick another port.");
            }

            if (!IsLoopbackHostPort(fwd.Target))
            {
                problems.Add($"forward[{i}].target: '{fwd.Target}' must be a loopback host:port " +
                    "(127.0.0.1 / ::1 / localhost, port 1..65535) - pdn never proxies the tailnet to a non-loopback host.");
            }

            if (!Enum.IsDefined(fwd.Tls))
            {
                problems.Add($"forward[{i}].tls: '{fwd.Tls}' is not a valid value (terminate | raw).");
            }
        }

        // Identity collision across the two sources — the contract makes this an error
        // (docs/app-packages.md § Owner state): pdn can't serve two apps under one id.
        var inlineIdClash = config.Applications.FirstOrDefault(a =>
            string.Equals(a.Id, dirName, StringComparison.OrdinalIgnoreCase));
        if (inlineIdClash is not null)
        {
            problems.Add($"id: '{dirName}' collides with the inline applications: entry '{inlineIdClash.Id}' - remove one.");
        }

        // The effective command verb (owner override wins over the manifest packet.command) —
        // checked against the built-in console verbs and the inline applications here; against
        // the other packages' effective verbs in the cross-package pass. A packet app may omit
        // the verb entirely (reachable only by callsign/alias), in which case nothing registers.
        var verb = (draft.Override?.Command ?? manifest.Packet?.Command)?.Trim();
        if (!string.IsNullOrWhiteSpace(verb))
        {
            draft.EffectiveVerb = verb;

            if (NodeCommandParser.Parse(verb) is not (UnknownCommand or EmptyCommand))
            {
                problems.Add($"command verb '{verb}' collides with a built-in console verb " +
                    "(CONNECT/BYE/NODES/INFO/HELP/SYSOP/SESSIONS/KICK/PORT/RELOAD or an abbreviation) - pick another.");
            }

            var inlineVerbClash = config.Applications.FirstOrDefault(a =>
                !string.IsNullOrWhiteSpace(a.Command)
                && string.Equals(a.Command.Trim(), verb, StringComparison.OrdinalIgnoreCase));
            if (inlineVerbClash is not null)
            {
                problems.Add($"command verb '{verb}' collides with inline application '{inlineVerbClash.Id}'.");
            }
        }
    }

    private static bool IsAbsoluteHttpUrl(string? url) =>
        !string.IsNullOrWhiteSpace(url)
        && Uri.TryCreate(url, UriKind.Absolute, out var u)
        && (u.Scheme == Uri.UriSchemeHttp || u.Scheme == Uri.UriSchemeHttps);

    /// <summary>True when <paramref name="target"/> is a <c>host:port</c> with a loopback host
    /// (127.0.0.1 / ::1 / localhost) and a 1..65535 port. The host:port split is on the LAST
    /// colon so an IPv6 loopback (<c>::1:993</c>) parses correctly.</summary>
    private static bool IsLoopbackHostPort(string? target)
    {
        if (string.IsNullOrWhiteSpace(target))
        {
            return false;
        }
        var lastColon = target.LastIndexOf(':');
        if (lastColon <= 0 || lastColon == target.Length - 1)
        {
            return false;   // no port, or an empty host/port half.
        }
        var host = target[..lastColon];
        var portText = target[(lastColon + 1)..];
        return LoopbackHosts.Contains(host)
            && int.TryParse(portText, out var port)
            && port is >= 1 and <= 65535;
    }

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

        /// <summary>The forward listen ports that passed the per-package range/reserved checks —
        /// the candidates the cross-package dup-listen pass groups on (a listen already flagged
        /// for being out of range or reserved is broken regardless, so it is excluded here to
        /// avoid a confusing second message).</summary>
        public IEnumerable<int> ValidForwardListens() =>
            (Manifest?.Forward ?? [])
                .Select(f => f.Listen)
                .Where(p => p is >= 1 and <= 65535 && p != ReservedWebPort);

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
