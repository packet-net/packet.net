using System.Formats.Tar;
using System.IO.Compression;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Packet.Node.Core.Applications.Packages;

namespace Packet.Node.Core.Applications.Catalog;

/// <summary>
/// The catalog installer (<see cref="IAppInstaller"/>). Fetches sha-pinned artifacts (or an
/// operator upload), verifies them, assembles the payload in a temp staging dir, then commits
/// it into <c>&lt;appsRoot&gt;/&lt;id&gt;/</c> behind a <c>.pdn-install.json</c> marker (O1 in
/// <c>docs/app-catalog.md</c>) so updates/uninstalls touch only the files the installer placed
/// — app-created state survives. The placed <c>pdn-app.yaml</c> sits at the package-dir root
/// exactly as a hand-installed package, so the existing discovery picks it up unchanged.
/// </summary>
public sealed partial class AppInstaller : IAppInstaller
{
    /// <summary>The marker filename written into each installed package dir.</summary>
    public const string MarkerFileName = ".pdn-install.json";

    /// <summary>The manifest filename a package dir must carry (shared with discovery).</summary>
    public const string ManifestFileName = AppPackageCatalog.ManifestFileName;

    private const string Source_Catalog = "catalog";
    private const string Source_Upload = "upload";

    // The data subtree a deb-kind app's payload lives under.
    private const string DebAppSubtreePrefix = "usr/share/packetnet/apps";

    private readonly IArtifactFetcher fetcher;
    private readonly IDebExtractor debExtractor;
    private readonly TimeProvider timeProvider;
    private readonly string appsRoot;
    private readonly long maxBytes;
    private readonly ILogger<AppInstaller> log;

    /// <summary>Create an installer. <paramref name="appsRoot"/> defaults to
    /// <see cref="AppPackagePaths.AppsRoot"/>; tests pass a temp dir. The root is created on
    /// first use.</summary>
    public AppInstaller(
        IArtifactFetcher fetcher,
        IDebExtractor debExtractor,
        TimeProvider timeProvider,
        ILoggerFactory loggerFactory,
        string? appsRoot = null,
        long maxBytes = HttpArtifactFetcher.DefaultMaxBytes)
    {
        ArgumentNullException.ThrowIfNull(fetcher);
        ArgumentNullException.ThrowIfNull(debExtractor);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(loggerFactory);
        this.fetcher = fetcher;
        this.debExtractor = debExtractor;
        this.timeProvider = timeProvider;
        this.appsRoot = appsRoot ?? AppPackagePaths.AppsRoot;
        this.maxBytes = maxBytes;
        log = loggerFactory.CreateLogger<AppInstaller>();
    }

    /// <inheritdoc/>
    public Task<InstallOutcome> InstallFromCatalogAsync(AppCatalogEntry entry, CancellationToken cancellationToken) =>
        InstallFromCatalogAsync(entry, RuntimeIds.Current(), cancellationToken);

    /// <inheritdoc/>
    public async Task<InstallOutcome> InstallFromCatalogAsync(AppCatalogEntry entry, string rid, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(entry);
        ArgumentException.ThrowIfNullOrEmpty(rid);

        var problems = AppCatalogYaml.Validate(entry);
        if (problems.Count > 0)
        {
            return InstallOutcome.Failure(entry.Id ?? "(unknown)", $"catalog entry is invalid: {string.Join(" ", problems)}");
        }

        var id = entry.Id;
        var staging = NewStagingDir();
        try
        {
            // Build the payload into the staging dir, returning the set of placed relative
            // paths and the verified sha256s for the marker.
            var assembled = entry.Artifact!.Kind switch
            {
                ArtifactKind.Assets => await AssembleAssetsAsync(entry, rid, staging, cancellationToken).ConfigureAwait(false),
                ArtifactKind.Deb => await AssembleDebAsync(entry, rid, staging, cancellationToken).ConfigureAwait(false),
                ArtifactKind.Pdnapp => await AssemblePdnappAsync(entry, rid, staging, cancellationToken).ConfigureAwait(false),
                _ => Assembled.Fail($"unknown artifact kind '{entry.Artifact!.Kind}'."),
            };

            if (assembled.Error is not null)
            {
                return InstallOutcome.Failure(id, assembled.Error);
            }

            // The staged manifest's id must match the catalog id — never commit a mislabelled
            // payload into the wrong dir.
            var manifestId = ReadManifestId(Path.Combine(staging, ManifestFileName));
            if (manifestId is null)
            {
                return InstallOutcome.Failure(id, "the staged payload has no readable pdn-app.yaml manifest.");
            }
            if (!string.Equals(manifestId, id, StringComparison.Ordinal))
            {
                return InstallOutcome.Failure(id,
                    $"staged manifest id '{manifestId}' does not match the catalog id '{id}'.");
            }

            var marker = new InstallMarker
            {
                Id = id,
                Source = Source_Catalog,
                Kind = entry.Artifact!.Kind.ToString().ToLowerInvariant(),
                Version = entry.Version,
                InstalledUtc = timeProvider.GetUtcNow(),
                Sha256s = assembled.Sha256s,
                Payload = assembled.Payload,
            };

            Commit(id, staging, marker);
            LogInstalled(id, entry.Version ?? "(none)", rid);
            return InstallOutcome.Success(id, entry.Version);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogInstallFailed(ex, id);
            return InstallOutcome.Failure(id, ex.Message);
        }
        finally
        {
            TryDeleteDir(staging);
        }
    }

    /// <inheritdoc/>
    public async Task<InstallOutcome> InstallFromUploadAsync(Stream pdnappTarGz, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(pdnappTarGz);

        var staging = NewStagingDir();
        try
        {
            await SafeUntarGzAsync(pdnappTarGz, staging, cancellationToken).ConfigureAwait(false);

            var manifestPath = Path.Combine(staging, ManifestFileName);
            var id = ReadManifestId(manifestPath);
            if (id is null)
            {
                return InstallOutcome.Failure("(unknown)",
                    "the uploaded .pdnapp has no pdn-app.yaml at its root, or it has no id.");
            }

            var version = ReadManifestVersion(manifestPath);
            var payload = RelativePaths(staging);

            var marker = new InstallMarker
            {
                Id = id,
                Source = Source_Upload,
                Kind = ArtifactKind.Pdnapp.ToString().ToLowerInvariant(),
                Version = version,
                InstalledUtc = timeProvider.GetUtcNow(),
                Sha256s = new Dictionary<string, string>(),  // operator-supplied: no pin.
                Payload = payload,
            };

            Commit(id, staging, marker);
            LogInstalled(id, version ?? "(none)", "upload");
            return InstallOutcome.Success(id, version);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogInstallFailed(ex, "(upload)");
            return InstallOutcome.Failure("(upload)", ex.Message);
        }
        finally
        {
            TryDeleteDir(staging);
        }
    }

    /// <inheritdoc/>
    public Task<InstallOutcome> UninstallAsync(string id, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(id);
        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            var dir = Path.Combine(appsRoot, id);
            var markerPath = Path.Combine(dir, MarkerFileName);
            if (!File.Exists(markerPath))
            {
                return Task.FromResult(InstallOutcome.Failure(id,
                    "no install marker — this package was sideloaded by hand, not catalog-installed; " +
                    "refusing to delete files we did not place."));
            }

            var marker = ReadMarker(markerPath);
            if (marker is null)
            {
                return Task.FromResult(InstallOutcome.Failure(id, "the install marker is unreadable."));
            }

            DeletePayload(dir, marker.Payload);
            File.Delete(markerPath);
            RemoveDirIfEmpty(dir);

            LogUninstalled(id);
            return Task.FromResult(InstallOutcome.Success(id, marker.Version));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogInstallFailed(ex, id);
            return Task.FromResult(InstallOutcome.Failure(id, ex.Message));
        }
    }

    // ---- per-kind assembly into the staging dir --------------------------------------------

    private async Task<Assembled> AssembleAssetsAsync(AppCatalogEntry entry, string rid, string staging, CancellationToken ct)
    {
        var assets = entry.Artifact!.Assets!;
        if (!assets.Binaries.TryGetValue(rid, out var bin))
        {
            return Assembled.Fail($"no '{rid}' binary in the catalog entry for '{entry.Id}'.");
        }

        // Manifest -> pdn-app.yaml at the package-dir root.
        var manifestTmp = await FetchVerifiedAsync(assets.Manifest.Url, assets.Manifest.Sha256, ct).ConfigureAwait(false);
        var manifestDest = Path.Combine(staging, ManifestFileName);
        File.Move(manifestTmp, manifestDest, overwrite: true);

        // Binary -> <dest> with <mode>.
        var binTmp = await FetchVerifiedAsync(bin.Url, bin.Sha256, ct).ConfigureAwait(false);
        var binDest = Path.Combine(staging, bin.Dest);
        EnsureParent(binDest);
        File.Move(binTmp, binDest, overwrite: true);
        ApplyMode(binDest, bin.Mode);

        return new Assembled
        {
            Payload = [ManifestFileName, bin.Dest],
            Sha256s = new Dictionary<string, string>
            {
                ["manifest"] = assets.Manifest.Sha256,
                ["binary"] = bin.Sha256,
            },
        };
    }

    private async Task<Assembled> AssembleDebAsync(AppCatalogEntry entry, string rid, string staging, CancellationToken ct)
    {
        var deb = entry.Artifact!.Deb!;
        if (!deb.Debs.TryGetValue(rid, out var debRef))
        {
            return Assembled.Fail($"no '{rid}' .deb in the catalog entry for '{entry.Id}'.");
        }

        var debTmp = await FetchVerifiedAsync(debRef.Url, debRef.Sha256, ct).ConfigureAwait(false);
        var extractDir = Path.Combine(Path.GetTempPath(), $"pdn-deb-{Guid.NewGuid():N}");
        try
        {
            await debExtractor.ExtractDataAsync(debTmp, extractDir, ct).ConfigureAwait(false);

            // The payload is usr/share/packetnet/apps/<id>/.
            var subtree = Path.Combine(extractDir, DebAppSubtreePrefix.Replace('/', Path.DirectorySeparatorChar), entry.Id);
            if (!Directory.Exists(subtree))
            {
                return Assembled.Fail(
                    $".deb has no '{DebAppSubtreePrefix}/{entry.Id}/' subtree to install.");
            }

            var subManifest = Path.Combine(subtree, ManifestFileName);
            var subManifestId = ReadManifestId(subManifest);
            if (subManifestId is null)
            {
                return Assembled.Fail($".deb subtree has no readable {ManifestFileName}.");
            }
            if (!string.Equals(subManifestId, entry.Id, StringComparison.Ordinal))
            {
                return Assembled.Fail(
                    $".deb subtree manifest id '{subManifestId}' does not match the catalog id '{entry.Id}'.");
            }

            CopyTree(subtree, staging);
            return new Assembled
            {
                Payload = RelativePaths(staging),
                Sha256s = new Dictionary<string, string> { ["deb"] = debRef.Sha256 },
            };
        }
        finally
        {
            TryDeleteDir(extractDir);
            TryDeleteFile(debTmp);
        }
    }

    private async Task<Assembled> AssemblePdnappAsync(AppCatalogEntry entry, string rid, string staging, CancellationToken ct)
    {
        var pdnapp = entry.Artifact!.Pdnapp!;
        ArtifactRef? chosen = null;
        if (pdnapp.Variants is { } variants && variants.TryGetValue(rid, out var variant))
        {
            chosen = variant;
        }
        chosen ??= pdnapp.Pdnapp;
        if (chosen is null)
        {
            return Assembled.Fail($"no '{rid}' .pdnapp variant (and no single pdnapp) for '{entry.Id}'.");
        }

        var tmp = await FetchVerifiedAsync(chosen.Url, chosen.Sha256, ct).ConfigureAwait(false);
        try
        {
            await using var fs = File.OpenRead(tmp);
            await SafeUntarGzAsync(fs, staging, ct).ConfigureAwait(false);
        }
        finally
        {
            TryDeleteFile(tmp);
        }

        return new Assembled
        {
            Payload = RelativePaths(staging),
            Sha256s = new Dictionary<string, string> { ["pdnapp"] = chosen.Sha256 },
        };
    }

    // ---- fetch + verify --------------------------------------------------------------------

    /// <summary>Fetch an artifact to a temp file and verify its sha256 against the pin. A
    /// mismatch is a HARD failure (the temp file is deleted, the exception propagates so
    /// nothing is staged).</summary>
    private async Task<string> FetchVerifiedAsync(string url, string expectedSha, CancellationToken ct)
    {
        var temp = await fetcher.FetchToTempAsync(new Uri(url, UriKind.Absolute), maxBytes, ct).ConfigureAwait(false);
        try
        {
            var actual = await Sha256.OfFileHexAsync(temp, ct).ConfigureAwait(false);
            if (!string.Equals(actual, expectedSha, StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    $"sha256 mismatch for '{url}': expected {expectedSha}, got {actual}.");
            }
            return temp;
        }
        catch
        {
            TryDeleteFile(temp);
            throw;
        }
    }

    // ---- commit / payload management -------------------------------------------------------

    /// <summary>Atomically (best-effort) replace the previous payload with the staged one:
    /// read any existing marker, delete exactly its recorded files, copy the staged payload +
    /// the new marker in. App-created state in the dir is never touched.</summary>
    private void Commit(string id, string staging, InstallMarker marker)
    {
        Directory.CreateDirectory(appsRoot);
        var dir = Path.Combine(appsRoot, id);
        Directory.CreateDirectory(dir);

        var existing = ReadMarker(Path.Combine(dir, MarkerFileName));
        if (existing is not null)
        {
            DeletePayload(dir, existing.Payload);
        }

        foreach (var rel in marker.Payload)
        {
            var from = Path.Combine(staging, rel);
            var to = Path.Combine(dir, rel);
            EnsureParent(to);
            File.Copy(from, to, overwrite: true);
            CopyMode(from, to);
        }

        var markerJson = JsonSerializer.Serialize(marker, InstallMarkerJsonContext.Default.InstallMarker);
        File.WriteAllText(Path.Combine(dir, MarkerFileName), markerJson);
    }

    private static void DeletePayload(string dir, IReadOnlyList<string> payload)
    {
        foreach (var rel in payload)
        {
            TryDeleteFile(Path.Combine(dir, rel));
        }
        // Prune now-empty subdirectories the payload created (deepest first), but never the
        // package dir itself and never a dir still holding app state.
        foreach (var sub in payload
                     .Select(p => Path.GetDirectoryName(p))
                     .Where(d => !string.IsNullOrEmpty(d))
                     .Distinct()
                     .OrderByDescending(d => d!.Length))
        {
            var full = Path.Combine(dir, sub!);
            RemoveDirIfEmpty(full);
        }
    }

    // ---- staging / fs helpers --------------------------------------------------------------

    private static string NewStagingDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"pdn-stage-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        return dir;
    }

    /// <summary>Extract a tar.gz into <paramref name="destDir"/>, rejecting any entry whose
    /// resolved path escapes the destination (the path-traversal guard for untrusted
    /// archives).</summary>
    private static async Task SafeUntarGzAsync(Stream tarGz, string destDir, CancellationToken ct)
    {
        Directory.CreateDirectory(destDir);
        var root = Path.GetFullPath(destDir) + Path.DirectorySeparatorChar;

        await using var gz = new GZipStream(tarGz, CompressionMode.Decompress, leaveOpen: true);
        await using var reader = new TarReader(gz, leaveOpen: true);

        TarEntry? entry;
        while ((entry = await reader.GetNextEntryAsync(cancellationToken: ct).ConfigureAwait(false)) is not null)
        {
            var name = entry.Name.Replace('\\', '/').TrimStart('/');
            if (string.IsNullOrEmpty(name))
            {
                continue;
            }

            var target = Path.GetFullPath(Path.Combine(destDir, name));
            if (!target.StartsWith(root, StringComparison.Ordinal)
                && !string.Equals(target, Path.GetFullPath(destDir), StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    $"refusing tar entry '{entry.Name}': it escapes the extraction directory.");
            }

            switch (entry.EntryType)
            {
                case TarEntryType.Directory:
                    Directory.CreateDirectory(target);
                    break;
                case TarEntryType.RegularFile or TarEntryType.V7RegularFile:
                    EnsureParent(target);
                    await using (var outFile = new FileStream(
                                     target, FileMode.Create, FileAccess.Write, FileShare.None))
                    {
                        if (entry.DataStream is { } data)
                        {
                            await data.CopyToAsync(outFile, ct).ConfigureAwait(false);
                        }
                    }
                    ApplyTarMode(target, entry.Mode);
                    break;
                default:
                    // Symlinks / hardlinks / devices are not part of a package dir; skip them
                    // (and never follow a link out of the staging dir).
                    break;
            }
        }
    }

    /// <summary>Copy a directory tree's contents (files + subdirs) into <paramref name="dest"/>.</summary>
    private static void CopyTree(string source, string dest)
    {
        Directory.CreateDirectory(dest);
        foreach (var srcDir in Directory.GetDirectories(source, "*", SearchOption.AllDirectories))
        {
            Directory.CreateDirectory(Path.Combine(dest, Path.GetRelativePath(source, srcDir)));
        }
        foreach (var srcFile in Directory.GetFiles(source, "*", SearchOption.AllDirectories))
        {
            var to = Path.Combine(dest, Path.GetRelativePath(source, srcFile));
            EnsureParent(to);
            File.Copy(srcFile, to, overwrite: true);
            CopyMode(srcFile, to);
        }
    }

    /// <summary>Every regular file under <paramref name="dir"/>, as package-dir-relative,
    /// forward-slash paths (the marker's payload spelling) — sorted for determinism.</summary>
    private static string[] RelativePaths(string dir)
    {
        if (!Directory.Exists(dir))
        {
            return [];
        }
        return Directory.GetFiles(dir, "*", SearchOption.AllDirectories)
            .Select(f => Path.GetRelativePath(dir, f).Replace(Path.DirectorySeparatorChar, '/'))
            .OrderBy(p => p, StringComparer.Ordinal)
            .ToArray();
    }

    private static void EnsureParent(string path)
    {
        var parent = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(parent))
        {
            Directory.CreateDirectory(parent);
        }
    }

    private static void ApplyMode(string path, string? mode)
    {
        if (OperatingSystem.IsWindows() || string.IsNullOrWhiteSpace(mode))
        {
            return;
        }
        try
        {
            var bits = Convert.ToInt32(mode, 8);
            File.SetUnixFileMode(path, (UnixFileMode)bits);
        }
        catch (Exception ex) when (ex is FormatException or OverflowException or ArgumentException)
        {
            // A malformed mode in the catalog is non-fatal; the file is still placed.
        }
    }

    private static void ApplyTarMode(string path, UnixFileMode mode)
    {
        if (OperatingSystem.IsWindows() || mode == 0)
        {
            return;
        }
        try
        {
            File.SetUnixFileMode(path, mode);
        }
        catch (Exception)
        {
            // Best-effort.
        }
    }

    private static void CopyMode(string from, string to)
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }
        try
        {
            File.SetUnixFileMode(to, File.GetUnixFileMode(from));
        }
        catch (Exception)
        {
            // Best-effort.
        }
    }

    private static string? ReadManifestId(string manifestPath)
    {
        try
        {
            if (!File.Exists(manifestPath))
            {
                return null;
            }
            var manifest = AppPackageManifestYaml.Parse(File.ReadAllText(manifestPath));
            return string.IsNullOrWhiteSpace(manifest.Id) ? null : manifest.Id;
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static string? ReadManifestVersion(string manifestPath)
    {
        try
        {
            if (!File.Exists(manifestPath))
            {
                return null;
            }
            return AppPackageManifestYaml.Parse(File.ReadAllText(manifestPath)).Version;
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static InstallMarker? ReadMarker(string markerPath)
    {
        try
        {
            if (!File.Exists(markerPath))
            {
                return null;
            }
            return JsonSerializer.Deserialize(File.ReadAllText(markerPath), InstallMarkerJsonContext.Default.InstallMarker);
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static void RemoveDirIfEmpty(string dir)
    {
        try
        {
            if (Directory.Exists(dir)
                && !Directory.EnumerateFileSystemEntries(dir).Any())
            {
                Directory.Delete(dir);
            }
        }
        catch (Exception)
        {
            // Best-effort.
        }
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception)
        {
            // Best-effort.
        }
    }

    private static void TryDeleteDir(string dir)
    {
        try
        {
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, recursive: true);
            }
        }
        catch (Exception)
        {
            // Best-effort.
        }
    }

    /// <summary>Result of assembling a payload into staging: the placed relative paths + the
    /// verified sha256s, or an error (nothing committed).</summary>
    private sealed class Assembled
    {
        public IReadOnlyList<string> Payload { get; init; } = [];
        public IReadOnlyDictionary<string, string> Sha256s { get; init; } = new Dictionary<string, string>();
        public string? Error { get; init; }

        public static Assembled Fail(string error) => new() { Error = error };
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Installed app '{Id}' version {Version} ({Rid}).")]
    private partial void LogInstalled(string id, string version, string rid);

    [LoggerMessage(Level = LogLevel.Information, Message = "Uninstalled app '{Id}'.")]
    private partial void LogUninstalled(string id);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Install/uninstall of app '{Id}' failed.")]
    private partial void LogInstallFailed(Exception ex, string id);
}
