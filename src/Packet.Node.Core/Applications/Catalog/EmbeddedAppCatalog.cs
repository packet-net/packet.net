using Microsoft.Extensions.Logging;
using Packet.Node.Core.Applications.Packages;

namespace Packet.Node.Core.Applications.Catalog;

/// <summary>
/// <see cref="IAppCatalog"/> backed by the on-disk catalog file (default
/// <c>/usr/share/packetnet/catalog/apps.yaml</c>, <see cref="AppPackagePaths.CatalogFile"/>).
/// Re-reads the file on every <see cref="List"/> (it is tiny, and this keeps a freshly-deployed
/// catalog live with no restart), parses it, drops invalid entries with a logged warning, and
/// returns the valid ones. Totally non-throwing: a missing file is an empty list + a debug log
/// (a node may legitimately have no catalog).
/// </summary>
public sealed partial class EmbeddedAppCatalog : IAppCatalog
{
    private readonly string catalogPath;
    private readonly ILogger<EmbeddedAppCatalog> log;

    /// <summary>Create a catalog reader over <paramref name="catalogPath"/> (defaults to
    /// <see cref="AppPackagePaths.CatalogFile"/>).</summary>
    public EmbeddedAppCatalog(ILoggerFactory loggerFactory, string? catalogPath = null)
    {
        ArgumentNullException.ThrowIfNull(loggerFactory);
        this.catalogPath = catalogPath ?? AppPackagePaths.CatalogFile;
        log = loggerFactory.CreateLogger<EmbeddedAppCatalog>();
    }

    /// <inheritdoc/>
    public IReadOnlyList<AppCatalogEntry> List()
    {
        string text;
        try
        {
            if (!File.Exists(catalogPath))
            {
                LogNoCatalog(catalogPath);
                return [];
            }
            text = File.ReadAllText(catalogPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            LogUnreadable(ex, catalogPath);
            return [];
        }

        AppCatalogDocument doc;
        try
        {
            doc = AppCatalogYaml.Parse(text);
        }
        catch (InvalidDataException ex)
        {
            LogUnparseable(ex, catalogPath);
            return [];
        }

        var valid = new List<AppCatalogEntry>(doc.Apps.Count);
        foreach (var entry in doc.Apps)
        {
            var problems = AppCatalogYaml.Validate(entry);
            if (problems.Count == 0)
            {
                valid.Add(entry);
            }
            else
            {
                LogInvalidEntry(entry.Id, string.Join(" ", problems));
            }
        }

        LogLoaded(valid.Count, doc.Apps.Count, catalogPath);
        return valid;
    }

    [LoggerMessage(Level = LogLevel.Debug,
        Message = "No app catalog at {Path}; the 'Available apps' list is empty.")]
    private partial void LogNoCatalog(string path);

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "App catalog {Path} is unreadable; treating it as empty.")]
    private partial void LogUnreadable(Exception ex, string path);

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "App catalog {Path} could not be parsed; treating it as empty.")]
    private partial void LogUnparseable(Exception ex, string path);

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "App catalog entry '{Id}' is invalid and is dropped: {Problems}")]
    private partial void LogInvalidEntry(string id, string problems);

    [LoggerMessage(Level = LogLevel.Debug,
        Message = "Loaded {Valid} of {Total} app catalog entries from {Path}.")]
    private partial void LogLoaded(int valid, int total, string path);
}
