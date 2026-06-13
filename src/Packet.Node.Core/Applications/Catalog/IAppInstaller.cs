namespace Packet.Node.Core.Applications.Catalog;

/// <summary>
/// Installs / updates / uninstalls app packages from the catalog (or an operator upload),
/// laying down a discoverable <c>&lt;appsRoot&gt;/&lt;id&gt;/</c> directory that the existing
/// package discovery (<c>IAppPackageCatalog</c>) then picks up with zero change. The contract
/// is <c>docs/app-catalog.md</c>. Every method is total at the public surface: failures come
/// back as <see cref="InstallOutcome"/> with <see cref="InstallOutcome.Ok"/> false and an
/// <see cref="InstallOutcome.Error"/>, never as a thrown exception.
/// </summary>
/// <remarks>
/// These signatures are stable — slice 6b's HTTP/UI surface depends on them.
/// </remarks>
public interface IAppInstaller
{
    /// <summary>Install (or update) the catalog entry for the target runtime id
    /// (<c>linux-x64</c> / <c>linux-arm64</c> / <c>linux-arm</c>): fetch + sha-verify the
    /// pinned artifact, assemble the payload, and place it at <c>&lt;appsRoot&gt;/&lt;id&gt;/</c>,
    /// preserving any app-created state. A sha mismatch (or any failure) stages nothing.</summary>
    Task<InstallOutcome> InstallFromCatalogAsync(AppCatalogEntry entry, string rid, CancellationToken cancellationToken);

    /// <summary>Install the catalog entry for THIS box's runtime id
    /// (<see cref="RuntimeIds.Current"/>).</summary>
    Task<InstallOutcome> InstallFromCatalogAsync(AppCatalogEntry entry, CancellationToken cancellationToken);

    /// <summary>Install an operator-supplied <c>.pdnapp</c> (a tar.gz of a package dir with the
    /// manifest at root). There is no sha pin — the operator uploading the bytes IS the trust.
    /// Path-traversal entries are rejected.</summary>
    Task<InstallOutcome> InstallFromUploadAsync(Stream pdnappTarGz, CancellationToken cancellationToken);

    /// <summary>Uninstall an app the caller has already disabled: delete exactly the
    /// installer-recorded payload files + the marker, and remove the directory only if now
    /// empty (state left behind). Refused (Ok=false) for a marker-less, hand-sideloaded dir.</summary>
    Task<InstallOutcome> UninstallAsync(string id, CancellationToken cancellationToken);

    /// <summary>Read the install marker (<c>.pdn-install.json</c>) for <paramref name="id"/>
    /// from <c>&lt;appsRoot&gt;/&lt;id&gt;/</c>, or <c>null</c> when there is no marker (a
    /// hand-sideloaded or absent package). The marker's recorded version is the one to show
    /// for an installed catalog/upload app — it pins the catalog version at install time, which
    /// avoids a spurious "update available" when the app's in-repo manifest version lags its
    /// release tag (O4 in <c>docs/app-catalog.md</c>). Total: an unreadable marker is null.</summary>
    InstalledApp? GetInstalled(string id);
}

/// <summary>What the installer recorded about an installed app, read back from its
/// <c>.pdn-install.json</c> marker: the catalog/upload version pinned at install time and how it
/// got there. Distinct from the discovered manifest's version (which may lag the release tag).</summary>
public sealed record InstalledApp(string Id, string? Version, string Source, string Kind);

/// <summary>The result of an install/update/uninstall: success plus the resolved id/version,
/// or failure with a human-readable reason.</summary>
public sealed record InstallOutcome(bool Ok, string Id, string? Version, string? Error)
{
    /// <summary>A success outcome for <paramref name="id"/> at <paramref name="version"/>.</summary>
    public static InstallOutcome Success(string id, string? version) => new(true, id, version, null);

    /// <summary>A failure outcome for <paramref name="id"/> with <paramref name="error"/>.</summary>
    public static InstallOutcome Failure(string id, string error) => new(false, id, null, error);
}
