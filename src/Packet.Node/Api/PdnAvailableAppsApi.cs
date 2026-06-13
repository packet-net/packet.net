using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Packet.Node.Core.Applications.Catalog;
using Packet.Node.Core.Applications.Packages;
using Packet.Node.Core.Audit;
using Packet.Node.Core.Configuration;

namespace Packet.Node.Api;

/// <summary>
/// The "Available apps" API (<c>docs/app-catalog.md</c> § Surfaces): the catalog of vetted apps
/// the node can install (<c>GET /api/v1/apps/available</c>), each projected with the node's
/// installed-state view (installed? at what version? is an update available? installable on this
/// box's runtime?), and the one-click install action
/// (<c>POST /api/v1/apps/available/{id}/install</c>).
/// </summary>
/// <remarks>
/// <para>
/// <b>Sibling of <see cref="PdnAppPackagesApi"/>.</b> That API manages <i>installed</i> packages
/// (the enable/disable trust toggle, restart, uninstall, upload); this one is the menu you
/// install <i>from</i>. The two catalogs are deliberately distinct: <see cref="IAppCatalog"/> is
/// the available index, <see cref="IAppPackageCatalog"/> is what's discovered on disk. This API
/// left-joins the former onto the latter.
/// </para>
/// <para>
/// <b>Scopes.</b> Listing the catalog is <c>read</c>; installing is <c>admin</c> and audited —
/// fetching+staging an app's bytes is a privileged action, the same gate enable carries. (Install
/// still lands a <i>disabled</i> package: install ≠ enable. The owner's separate enable grant is
/// what runs the code.)
/// </para>
/// <para>
/// <b>Total at the client edge.</b> The installer never throws to the caller — failures come back
/// as an <see cref="InstallOutcome"/> with <c>Ok=false</c> and a reason, which this API maps to a
/// 422. An unknown id is 404, a not-installable entry (no artifact for this box's runtime) is 409.
/// </para>
/// </remarks>
public static class PdnAvailableAppsApi
{
    /// <summary>Map the available-apps endpoints under <c>/api/v1/apps/available</c>. Called
    /// from the node composition root beside <see cref="PdnAppPackagesApi.MapPdnAppPackagesApi"/>
    /// and before the SPA fallback (specific routes win over the <c>/api/{**rest}</c> catch-all).</summary>
    public static void MapPdnAvailableAppsApi(this WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);

        var group = app.MapGroup("/api/v1/apps/available");

        // The catalog left-joined with installed state. Read-gated like the other reads.
        group.MapGet("",
            (IConfigProvider config, IAppCatalog catalog, IAppPackageCatalog packages, IAppInstaller installer) =>
            {
                var rid = RuntimeIds.Current();
                var discovered = packages.Discover(config.Current);
                var available = catalog.List()
                    .Select(entry => Project(entry, rid, discovered, installer))
                    .ToArray();
                return Results.Ok(available);
            }).RequireAuthorization(PdnAuthPolicies.Read);

        // Install (or update) a catalog entry for this box's runtime. Admin + audited:
        // fetching+staging bytes is a privileged action. 404 unknown id; 409 not-installable
        // (no artifact for this RID); 422 on an installer failure (sha mismatch, unreachable
        // host, …). On success the package appears discovered-but-DISABLED — the owner's
        // separate enable grant is what runs it.
        group.MapPost("/{id}/install",
            async (string id, HttpContext ctx, IAppCatalog catalog, IAppInstaller installer,
                IAuditLog audit, TimeProvider clock, CancellationToken ct) =>
            {
                audit.RecordRest(ctx, clock, "install_app", id, "requested", "");

                var entry = catalog.List()
                    .FirstOrDefault(e => string.Equals(e.Id, id, StringComparison.OrdinalIgnoreCase));
                if (entry is null)
                {
                    return Results.NotFound();
                }

                var rid = RuntimeIds.Current();
                if (!IsInstallable(entry, rid))
                {
                    return Results.Json(
                        new { error = $"App '{entry.Id}' has no artifact for this node's runtime ('{rid}')." },
                        statusCode: StatusCodes.Status409Conflict);
                }

                var outcome = await installer.InstallFromCatalogAsync(entry, ct).ConfigureAwait(false);
                return outcome.Ok
                    ? Results.Ok(outcome)
                    : Results.UnprocessableEntity(new { ok = false, id = outcome.Id, error = outcome.Error });
            }).RequireAuthorization(PdnAuthPolicies.Admin);
    }

    /// <summary>Project one catalog entry with the node's installed-state view.</summary>
    private static AvailableApp Project(
        AppCatalogEntry entry, string rid, IReadOnlyList<DiscoveredAppPackage> discovered, IAppInstaller installer)
    {
        var package = discovered.FirstOrDefault(p =>
            string.Equals(p.Id, entry.Id, StringComparison.OrdinalIgnoreCase));
        var installed = package is not null;

        // The installed version: prefer the install marker's recorded version (the catalog
        // version pinned at install time) over the discovered manifest's version, which can lag
        // the release tag and would otherwise show a spurious update badge (O4). Fall back to
        // the manifest version for a marker-less (hand-sideloaded) package.
        string? installedVersion = null;
        if (installed)
        {
            installedVersion = installer.GetInstalled(entry.Id)?.Version ?? package!.Manifest?.Version;
        }

        var updateAvailable = installed
            && installedVersion is not null
            && !string.Equals(installedVersion, entry.Version, StringComparison.Ordinal);

        return new AvailableApp(
            Id: entry.Id,
            Name: entry.Name ?? entry.Id,
            Version: entry.Version ?? "",
            Description: entry.Description,
            Icon: entry.Icon,
            Capabilities: entry.Capabilities,
            Homepage: entry.Homepage,
            Kind: KindName(entry.Artifact?.Kind),
            Installed: installed,
            InstalledVersion: installedVersion,
            UpdateAvailable: updateAvailable,
            Installable: IsInstallable(entry, rid));
    }

    /// <summary>Whether the catalog entry carries an artifact for <paramref name="rid"/> — an
    /// <c>assets</c> binary, a <c>deb</c>, or a <c>pdnapp</c> variant (or its rid-independent
    /// single tarball).</summary>
    private static bool IsInstallable(AppCatalogEntry entry, string rid)
    {
        var artifact = entry.Artifact;
        return artifact?.Kind switch
        {
            ArtifactKind.Assets => artifact.Assets?.Binaries.ContainsKey(rid) == true,
            ArtifactKind.Deb => artifact.Deb?.Debs.ContainsKey(rid) == true,
            ArtifactKind.Pdnapp => artifact.Pdnapp?.Pdnapp is not null
                || artifact.Pdnapp?.Variants?.ContainsKey(rid) == true,
            _ => false,
        };
    }

    private static string KindName(ArtifactKind? kind) => kind switch
    {
        ArtifactKind.Assets => "assets",
        ArtifactKind.Deb => "deb",
        ArtifactKind.Pdnapp => "pdnapp",
        _ => "",
    };

    /// <summary>One available-apps row (the <c>/api/v1/apps/available</c> shape — camelCase on
    /// the wire). <see cref="Kind"/> is <c>assets</c>|<c>deb</c>|<c>pdnapp</c>;
    /// <see cref="Installed"/>/<see cref="InstalledVersion"/>/<see cref="UpdateAvailable"/> are
    /// the catalog⋈installed left-join; <see cref="Installable"/> is whether this box's runtime
    /// has a pinned artifact.</summary>
    public sealed record AvailableApp(
        string Id,
        string Name,
        string Version,
        string? Description,
        string? Icon,
        IReadOnlyList<string> Capabilities,
        string? Homepage,
        string Kind,
        bool Installed,
        string? InstalledVersion,
        bool UpdateAvailable,
        bool Installable);
}
