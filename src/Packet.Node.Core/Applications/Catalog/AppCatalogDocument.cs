namespace Packet.Node.Core.Applications.Catalog;

/// <summary>
/// The pdn <b>app catalog</b> — the curated index of <i>available</i> apps (the
/// <c>catalog/apps.yaml</c> file baked into the node .deb at
/// <c>/usr/share/packetnet/catalog/apps.yaml</c>). This is the C# shape of that file; the full
/// contract is <c>docs/app-catalog.md</c>.
/// </summary>
/// <remarks>
/// Distinct from the <c>Packages</c> namespace: that discovers <i>installed</i> packages
/// (<c>pdn-app.yaml</c> on disk). This describes the menu of things the owner <i>could</i>
/// install — each entry pins, per RID, an https artifact by sha256.
/// </remarks>
public sealed record AppCatalogDocument
{
    /// <summary>Catalog schema version. Only <c>1</c> is understood; anything else is a
    /// validation problem (forward-incompatible by design, like the manifest's
    /// <c>manifest:</c> gate).</summary>
    public int Catalog { get; init; }

    /// <summary>The available apps, in file order.</summary>
    public IReadOnlyList<AppCatalogEntry> Apps { get; init; } = [];
}

/// <summary>One available app in the catalog: its identity, presentation, and the
/// per-RID, sha256-pinned artifact the installer fetches.</summary>
public sealed record AppCatalogEntry
{
    /// <summary>Stable app identity — lowercase <c>[a-z0-9-]</c>. Becomes the install
    /// directory name (<c>&lt;appsRoot&gt;/&lt;id&gt;</c>), matching the package-discovery
    /// convention exactly.</summary>
    public required string Id { get; init; }

    /// <summary>Human label for the "Available apps" view. Default: <see cref="Id"/>.</summary>
    public string? Name { get; init; }

    /// <summary>The version this catalog entry pins (the upstream release tag). Shown in the
    /// UI and recorded in the install marker.</summary>
    public string? Version { get; init; }

    /// <summary>One-line human description.</summary>
    public string? Description { get; init; }

    /// <summary>Optional lucide icon name (cosmetic).</summary>
    public string? Icon { get; init; }

    /// <summary>Declared capabilities — shown to the owner at install/enable time, not
    /// enforced here.</summary>
    public IReadOnlyList<string> Capabilities { get; init; } = [];

    /// <summary>Optional project homepage (shown in the catalog UI).</summary>
    public string? Homepage { get; init; }

    /// <summary>The artifact spec: which packaging shape this app ships as, and the pinned
    /// per-RID download(s).</summary>
    public ArtifactSpec? Artifact { get; init; }
}

/// <summary>The packaging shape of a catalog entry's artifact.</summary>
public enum ArtifactKind
{
    /// <summary>A separately-fetched manifest plus a per-RID raw binary placed at a
    /// <c>dest</c> with a <c>mode</c> (DAPPS publishes exactly this shape).</summary>
    Assets,

    /// <summary>A per-arch <c>.deb</c>, extracted (<c>dpkg-deb -x</c>) not installed; the
    /// manifest + binary come from the deb's
    /// <c>usr/share/packetnet/apps/&lt;id&gt;/</c> subtree.</summary>
    Deb,

    /// <summary>A <c>.pdnapp</c> tarball of the package dir (manifest at root), optionally
    /// with per-RID variants.</summary>
    Pdnapp,
}

/// <summary>The artifact for one catalog entry — exactly one of the kind-specific sub-objects
/// is populated, selected by <see cref="Kind"/>.</summary>
public sealed record ArtifactSpec
{
    /// <summary>Which packaging shape; selects which sub-object below is meaningful.</summary>
    public ArtifactKind Kind { get; init; }

    /// <summary>Populated when <see cref="Kind"/> is <see cref="ArtifactKind.Assets"/>.</summary>
    public AssetsArtifact? Assets { get; init; }

    /// <summary>Populated when <see cref="Kind"/> is <see cref="ArtifactKind.Deb"/>.</summary>
    public DebArtifact? Deb { get; init; }

    /// <summary>Populated when <see cref="Kind"/> is <see cref="ArtifactKind.Pdnapp"/>.</summary>
    public PdnappArtifact? Pdnapp { get; init; }
}

/// <summary>A single sha256-pinned https artifact reference.</summary>
public sealed record ArtifactRef
{
    /// <summary>The download URL — MUST be <c>https://</c> (validated).</summary>
    public required string Url { get; init; }

    /// <summary>The expected sha256 of the fetched bytes — a 64-char lowercase hex string.
    /// A mismatch is a hard install refusal.</summary>
    public required string Sha256 { get; init; }
}

/// <summary>A sha256-pinned binary plus where (and with what mode) it lands in the package
/// dir — the <c>assets</c> kind's per-RID binary.</summary>
public sealed record BinaryRef
{
    /// <summary>The download URL — MUST be <c>https://</c>.</summary>
    public required string Url { get; init; }

    /// <summary>The expected sha256 (64-char lowercase hex).</summary>
    public required string Sha256 { get; init; }

    /// <summary>The destination filename inside the package dir (e.g. <c>dapps</c>).</summary>
    public required string Dest { get; init; }

    /// <summary>The Unix mode the placed file should carry, octal text (e.g. <c>"0755"</c>).</summary>
    public string? Mode { get; init; }
}

/// <summary>The <c>assets</c> artifact: a fetched manifest plus per-RID raw binaries.</summary>
public sealed record AssetsArtifact
{
    /// <summary>The <c>pdn-app.yaml</c> manifest fetched separately and placed at the package
    /// dir root.</summary>
    public required ArtifactRef Manifest { get; init; }

    /// <summary>Per-RID binary placements, keyed by runtime id (<c>linux-x64</c> etc.).</summary>
    public IReadOnlyDictionary<string, BinaryRef> Binaries { get; init; } =
        new Dictionary<string, BinaryRef>();
}

/// <summary>The <c>deb</c> artifact: a per-RID <c>.deb</c> to extract.</summary>
public sealed record DebArtifact
{
    /// <summary>Per-RID <c>.deb</c> references, keyed by runtime id.</summary>
    public IReadOnlyDictionary<string, ArtifactRef> Debs { get; init; } =
        new Dictionary<string, ArtifactRef>();
}

/// <summary>The <c>pdnapp</c> artifact: a single tarball (or per-RID variants).</summary>
public sealed record PdnappArtifact
{
    /// <summary>The single, rid-independent <c>.pdnapp</c> tarball. Null when only
    /// <see cref="Variants"/> are published.</summary>
    public ArtifactRef? Pdnapp { get; init; }

    /// <summary>Optional per-RID <c>.pdnapp</c> variants, keyed by runtime id. Null when a
    /// single <see cref="Pdnapp"/> covers every RID.</summary>
    public IReadOnlyDictionary<string, ArtifactRef>? Variants { get; init; }
}
