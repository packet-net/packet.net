using System.Formats.Tar;
using System.IO.Compression;
using Packet.Node.Core.Applications.Catalog;

namespace Packet.Node.Tests.Applications.Catalog;

/// <summary>
/// A fake <see cref="IArtifactFetcher"/> that serves local fixture bytes (no live network).
/// <see cref="Add"/> registers an https url → on-disk fixture file mapping; the installer
/// fetches by copying the fixture to a fresh temp file, exactly as the real fetcher would
/// hand back a downloaded temp file. An unregistered url is an error (a missing fixture is a
/// test bug). Non-https urls are refused, mirroring the production fetcher's contract.
/// </summary>
internal sealed class FakeArtifactFetcher : IArtifactFetcher
{
    private readonly Dictionary<string, string> byUrl = new(StringComparer.Ordinal);

    /// <summary>Map <paramref name="url"/> to the bytes of <paramref name="fixturePath"/>.</summary>
    public FakeArtifactFetcher Add(string url, string fixturePath)
    {
        byUrl[url] = fixturePath;
        return this;
    }

    public Task<string> FetchToTempAsync(Uri url, long maxBytes, CancellationToken cancellationToken)
    {
        if (!string.Equals(url.Scheme, Uri.UriSchemeHttps, StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"refusing to fetch '{url}': only https artifacts are allowed.");
        }
        if (!byUrl.TryGetValue(url.ToString(), out var fixture))
        {
            throw new InvalidOperationException($"no fixture registered for '{url}'.");
        }

        var bytes = File.ReadAllBytes(fixture);
        if (bytes.LongLength > maxBytes)
        {
            throw new InvalidOperationException($"fixture for '{url}' exceeds the {maxBytes}-byte cap.");
        }
        var temp = Path.Combine(Path.GetTempPath(), $"pdn-fake-fetch-{Guid.NewGuid():N}.tmp");
        File.WriteAllBytes(temp, bytes);
        return Task.FromResult(temp);
    }
}

/// <summary>A temp directory the test owns; recursively deleted on dispose.</summary>
internal sealed class TempDir : IDisposable
{
    public TempDir(string label)
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"pdn-{label}-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public string Combine(params string[] parts) => System.IO.Path.Combine([Path, .. parts]);

    public void Dispose()
    {
        try
        {
            Directory.Delete(Path, recursive: true);
        }
        catch (Exception)
        {
            // Best-effort temp cleanup only.
        }
    }
}

internal static class CatalogTestSupport
{
    /// <summary>Walk up from the test assembly to the repo root (the directory that has
    /// <c>catalog/apps.yaml</c>) — same approach as the package tests' repo-root walker.</summary>
    public static string RepoRoot()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir, "catalog", "apps.yaml")))
            {
                return dir;
            }
            dir = Path.GetDirectoryName(dir);
        }
        throw new InvalidOperationException(
            "Could not locate the repo root (no catalog/apps.yaml above the test assembly).");
    }

    /// <summary>The real shipped catalog file text.</summary>
    public static string RealCatalogYaml() =>
        File.ReadAllText(Path.Combine(RepoRoot(), "catalog", "apps.yaml"));

    /// <summary>A minimal valid <c>pdn-app.yaml</c> for <paramref name="id"/> with a service
    /// block (so it is a valid package manifest), at the given version.</summary>
    public static string ManifestYaml(string id, string version, string command = "/bin/true") => $"""
        manifest: 1
        id: {id}
        name: {id.ToUpperInvariant()}
        version: "{version}"
        service:
          command: {command}
        """;

    /// <summary>Write <paramref name="content"/> to a fixture file under <paramref name="dir"/>
    /// and return its path.</summary>
    public static string WriteFixture(TempDir dir, string name, string content)
    {
        var path = dir.Combine("fixtures", name);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return path;
    }

    public static string WriteFixtureBytes(TempDir dir, string name, byte[] content)
    {
        var path = dir.Combine("fixtures", name);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, content);
        return path;
    }

    /// <summary>The sha256 of a file as 64-char lowercase hex (the catalog pin spelling).</summary>
    public static string Sha256Hex(string path) => Sha256.OfFileHex(path);

    /// <summary>Build a <c>.pdnapp</c> (tar.gz) fixture from a map of relative-path → content,
    /// returning its path. Optionally inject a raw extra tar entry (e.g. a traversal name).</summary>
    public static string BuildPdnapp(
        TempDir dir,
        string name,
        IReadOnlyDictionary<string, string> files,
        string? traversalEntryName = null)
    {
        var path = dir.Combine("fixtures", name);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        using var fs = File.Create(path);
        using var gz = new GZipStream(fs, CompressionMode.Compress);
        using var tar = new TarWriter(gz, TarEntryFormat.Pax);

        foreach (var (rel, content) in files)
        {
            WriteTarFile(tar, rel, content);
        }
        if (traversalEntryName is not null)
        {
            WriteTarFile(tar, traversalEntryName, "pwned");
        }

        return path;
    }

    private static void WriteTarFile(TarWriter tar, string name, string content)
    {
        var entry = new PaxTarEntry(TarEntryType.RegularFile, name)
        {
            DataStream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(content)),
        };
        tar.WriteEntry(entry);
    }
}
