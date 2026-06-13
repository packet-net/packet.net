namespace Packet.Node.Core.Applications.Packages;

/// <summary>
/// The contract's path-resolution rule (<c>docs/app-packages.md</c> § The manifest): a relative
/// <c>command</c>/<c>args</c> element that names an existing file in the package directory
/// resolves to its absolute path; everything else passes through untouched. Shared by the
/// service supervisor (<see cref="AppServiceSupervisor"/>) and the session-resolution union
/// (<see cref="ApplicationHost"/>) so the two planes can never disagree about what
/// <c>args: [lobby.py]</c> means.
/// </summary>
public static class AppPackagePaths
{
    /// <summary>The default install root for owner-installed packages — the second discovery
    /// root (<c>docs/app-packages.md</c> § Discovery). The catalog installer places each app
    /// at <c>&lt;AppsRoot&gt;/&lt;id&gt;/</c> so the existing discovery picks it up unchanged.</summary>
    public const string AppsRoot = "/var/lib/packetnet/apps";

    /// <summary>The default location of the curated app catalog file
    /// (<c>catalog/apps.yaml</c>), baked into the node .deb. Read by the catalog reader
    /// (<c>EmbeddedAppCatalog</c>); see <c>docs/app-catalog.md</c>.</summary>
    public const string CatalogFile = "/usr/share/packetnet/catalog/apps.yaml";

    /// <summary>Resolve one command/argument element against <paramref name="packageDir"/>:
    /// a relative element naming an existing <b>file</b> there becomes that file's absolute
    /// path; an absolute element, a flag like <c>--socket</c>, or anything that names no file
    /// passes through unchanged.</summary>
    public static string ResolveFile(string element, string packageDir) =>
        Resolve(element, packageDir, File.Exists);

    /// <summary>Resolve a working-directory element the same way, against existing
    /// <b>directories</b> in the package dir.</summary>
    public static string ResolveDirectory(string element, string packageDir) =>
        Resolve(element, packageDir, Directory.Exists);

    private static string Resolve(string element, string packageDir, Func<string, bool> exists)
    {
        if (string.IsNullOrEmpty(element) || string.IsNullOrEmpty(packageDir) || Path.IsPathRooted(element))
        {
            return element;
        }
        try
        {
            var candidate = Path.GetFullPath(Path.Combine(packageDir, element));
            return exists(candidate) ? candidate : element;
        }
        catch (Exception ex) when (ex is ArgumentException or PathTooLongException or NotSupportedException)
        {
            // An element that isn't even a legal path is just an opaque argument.
            return element;
        }
    }
}
