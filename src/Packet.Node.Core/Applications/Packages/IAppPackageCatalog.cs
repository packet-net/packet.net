using Packet.Node.Core.Configuration;

namespace Packet.Node.Core.Applications.Packages;

/// <summary>
/// Discovers app packages (<c>pdn-app.yaml</c> under the package roots), merges each manifest
/// with the owner's <c>apps:</c> override, and validates the result. The catalog is re-scanned
/// at startup and on every config apply — discovery is cheap and the result is a pure snapshot.
/// Contract: <c>docs/app-packages.md</c> § Discovery.
/// </summary>
public interface IAppPackageCatalog
{
    /// <summary>Scan the package roots (the config's <c>appPackageRoots:</c> when set, else
    /// the defaults) and return every discovered package merged with its override from
    /// <paramref name="config"/>. Unreadable or invalid manifests are returned as
    /// <see cref="DiscoveredAppPackage.Error"/> entries rather than thrown — the owner sees
    /// the problem in the UI instead of losing the whole inventory.</summary>
    IReadOnlyList<DiscoveredAppPackage> Discover(NodeConfig config);
}

/// <summary>One discovered package: the manifest, where it lives, and the owner-resolved state.</summary>
public sealed record DiscoveredAppPackage
{
    /// <summary>The parsed manifest. Null only when <see cref="Error"/> is set.</summary>
    public AppPackageManifest? Manifest { get; init; }

    /// <summary>The package id (from the manifest, or the directory name for an error entry).</summary>
    public required string Id { get; init; }

    /// <summary>Absolute package directory (where <c>pdn-app.yaml</c> was found).</summary>
    public required string PackageDir { get; init; }

    /// <summary>Absolute per-app state directory (<c>/var/lib/packetnet/apps/&lt;id&gt;</c> by
    /// convention; under the test/dev root when overridden).</summary>
    public required string StateDir { get; init; }

    /// <summary>The owner's resolved trust state — false unless an <c>apps:</c> entry enables it.</summary>
    public bool Enabled { get; init; }

    /// <summary>The owner's override entry, when present.</summary>
    public AppOverrideConfig? Override { get; init; }

    /// <summary>Human-readable manifest/validation problem; non-null marks the entry broken
    /// (broken entries are never enabled, sessions never resolve, services never start).</summary>
    public string? Error { get; init; }

    /// <summary>The effective console verb — the owner's <see cref="AppOverrideConfig.Match"/>
    /// when set, else the manifest's session match. Null when there is no session block (or
    /// the manifest failed to parse). This is the value the catalog's verb-collision rules
    /// run on and the value the session-verb resolution uses. Computed, never stored — not
    /// part of record equality.</summary>
    public string? EffectiveMatch => Override?.Match ?? Manifest?.Session?.Match;

    /// <summary>The effective service environment: the manifest's <c>environment</c> map with
    /// the owner's override merged over it key-by-key (owner wins) — the order the contract
    /// pins for the supervised child (after the <c>PDN_APP_*</c> injections, which are the
    /// supervisor's concern). Empty when neither side declares anything.</summary>
    public IReadOnlyDictionary<string, string> EffectiveEnvironment
    {
        get
        {
            var manifestEnv = Manifest?.Service?.Environment;
            var overrideEnv = Override?.Environment;
            if (overrideEnv is null or { Count: 0 })
            {
                return manifestEnv ?? new Dictionary<string, string>();
            }

            var merged = manifestEnv is null
                ? new Dictionary<string, string>(StringComparer.Ordinal)
                : new Dictionary<string, string>(manifestEnv, StringComparer.Ordinal);
            foreach (var (key, value) in overrideEnv)
            {
                merged[key] = value;   // the owner's entry wins key-by-key.
            }
            return merged;
        }
    }
}
