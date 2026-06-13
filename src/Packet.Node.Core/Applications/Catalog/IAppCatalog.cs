namespace Packet.Node.Core.Applications.Catalog;

/// <summary>
/// The curated index of <b>available</b> apps — the <c>catalog/apps.yaml</c> file the node
/// ships (<c>docs/app-catalog.md</c>). This is the menu the owner installs <i>from</i>.
/// </summary>
/// <remarks>
/// DISTINCT from <c>IAppPackageCatalog</c>, which discovers <i>installed</i> packages
/// (<c>pdn-app.yaml</c> directories on disk). One describes what you could install; the other
/// describes what is installed. The installer (<see cref="IAppInstaller"/>) is the bridge: it
/// takes an <see cref="AppCatalogEntry"/> from here and lays down a discoverable package the
/// other catalog then sees.
/// </remarks>
public interface IAppCatalog
{
    /// <summary>The available apps, valid entries only. Total and side-effect free: a missing
    /// or unreadable catalog file yields an empty list (a node may legitimately ship none),
    /// and an invalid entry is dropped with a logged warning rather than thrown.</summary>
    IReadOnlyList<AppCatalogEntry> List();
}
