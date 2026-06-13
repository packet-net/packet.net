using System.Text.Json;
using System.Text.Json.Serialization;

namespace Packet.Node.Core.Applications.Catalog;

/// <summary>
/// The <c>.pdn-install.json</c> marker the installer writes into <c>&lt;appsRoot&gt;/&lt;id&gt;/</c>
/// to record exactly which files it placed (O1 in <c>docs/app-catalog.md</c>). It is the basis
/// of safe update + uninstall: on (re)install the installer deletes precisely the
/// previously-recorded <see cref="Payload"/> and writes the new set, so app-CREATED state
/// (e.g. <c>dapps.db</c>, <c>convers.yaml</c>) is never touched; on uninstall it deletes the
/// recorded payload + the marker and leaves state behind. A package with no marker was
/// hand-sideloaded — the installer refuses to uninstall it.
/// </summary>
public sealed record InstallMarker
{
    /// <summary>The app id (== the install directory name).</summary>
    public required string Id { get; init; }

    /// <summary>Where the install came from: <c>"catalog"</c> (sha-pinned fetch) or
    /// <c>"upload"</c> (operator-supplied .pdnapp).</summary>
    public required string Source { get; init; }

    /// <summary>The artifact kind that produced this install (<c>assets</c> / <c>deb</c> /
    /// <c>pdnapp</c>), lowercase.</summary>
    public required string Kind { get; init; }

    /// <summary>The version installed (the catalog pin, or the uploaded manifest's version).</summary>
    public string? Version { get; init; }

    /// <summary>When the install happened (from the injected <see cref="TimeProvider"/>).</summary>
    public required DateTimeOffset InstalledUtc { get; init; }

    /// <summary>The sha256s the install verified, keyed by a human label (e.g.
    /// <c>"manifest"</c>, <c>"binary"</c>, <c>"deb"</c>). Empty for an unpinned upload.</summary>
    public IReadOnlyDictionary<string, string> Sha256s { get; init; } =
        new Dictionary<string, string>();

    /// <summary>The package-dir-relative paths the installer placed — exactly the set it will
    /// delete on the next update or on uninstall. Never includes app-created state.</summary>
    public IReadOnlyList<string> Payload { get; init; } = [];
}

/// <summary>Source-generated JSON context for the install marker (AOT-/trim-clean).</summary>
[JsonSourceGenerationOptions(
    WriteIndented = true,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(InstallMarker))]
public sealed partial class InstallMarkerJsonContext : JsonSerializerContext;
