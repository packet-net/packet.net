using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Packet.Kiss.NinoTnc.Firmware;

/// <summary>
/// <see cref="INinoTncFirmwareCatalogue"/> backed by the GitHub
/// contents API of <c>ninocarrillo/flashtnc</c>. Reads only the tip of
/// the repository's default branch: whatever <c>N9600A-v{major}-{minor}.hex</c>
/// files are sitting there *now* are taken to be the current releases.
/// </summary>
/// <remarks>
/// <para>
/// Nino removes superseded firmware images from master when a new
/// release lands, so this catalogue intentionally surfaces only the
/// current state. Old versions are still reachable via git history
/// but aren't useful to surface in a routine update-check flow.
/// </para>
/// <para>
/// The class does no caching beyond what <see cref="HttpClient"/>'s
/// configured cache does. For a typical "user pressed TX-Test, are we
/// up to date?" workflow, one round-trip per check is fine; if you
/// poll, wrap with your own cache.
/// </para>
/// </remarks>
public sealed partial class GitHubNinoTncFirmwareCatalogue : INinoTncFirmwareCatalogue
{
    /// <summary>The default repository slug.</summary>
    public const string DefaultOwner = "ninocarrillo";

    /// <summary>The default repository name.</summary>
    public const string DefaultRepo = "flashtnc";

    /// <summary>The default branch this catalogue reads.</summary>
    public const string DefaultBranch = "master";

    /// <summary>
    /// Filename pattern: <c>N9600A-v{major}-{minor}.hex</c>. Major and
    /// minor are both decimal. Case-sensitive — GitHub URLs are.
    /// </summary>
    [GeneratedRegex(@"^N9600A-v(\d+)-(\d+)\.hex$")]
    private static partial Regex HexFileNameRegex();

    private readonly HttpClient http;
    private readonly string owner;
    private readonly string repo;
    private readonly string branch;

    /// <summary>
    /// Create a catalogue that reads from
    /// <c>ninocarrillo/flashtnc@master</c>. <paramref name="http"/>
    /// must be supplied — the caller owns its lifetime, and tests can
    /// substitute a <see cref="HttpMessageHandler"/> for offline runs.
    /// The caller is responsible for setting a sensible
    /// <c>User-Agent</c> header (GitHub requires one for unauthenticated
    /// requests).
    /// </summary>
    public GitHubNinoTncFirmwareCatalogue(
        HttpClient http,
        string owner = DefaultOwner,
        string repo = DefaultRepo,
        string branch = DefaultBranch)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentException.ThrowIfNullOrEmpty(owner);
        ArgumentException.ThrowIfNullOrEmpty(repo);
        ArgumentException.ThrowIfNullOrEmpty(branch);
        this.http = http;
        this.owner = owner;
        this.repo = repo;
        this.branch = branch;
    }

    /// <inheritdoc/>
    public async Task<NinoTncFirmwareRelease?> GetLatestForVariantAsync(
        NinoTncChipVariant variant,
        CancellationToken cancellationToken = default)
    {
        if (variant == NinoTncChipVariant.Unknown)
        {
            return null;
        }

        var allReleases = await ListReleasesAsync(cancellationToken).ConfigureAwait(false);
        return allReleases
            .Where(r => r.ChipVariant == variant)
            .OrderByDescending(r => r.Version)
            .FirstOrDefault();
    }

    /// <summary>
    /// Return every release the catalogue currently knows about, across
    /// all chip variants. Exposed primarily for diagnostics.
    /// </summary>
    public async Task<IReadOnlyList<NinoTncFirmwareRelease>> ListReleasesAsync(
        CancellationToken cancellationToken = default)
    {
        var url = $"https://api.github.com/repos/{owner}/{repo}/contents?ref={branch}";
        var contents = await http.GetFromJsonAsync(
            url,
            GitHubContentsContext.Default.IReadOnlyListGitHubContentEntry,
            cancellationToken).ConfigureAwait(false);
        if (contents is null)
        {
            return Array.Empty<NinoTncFirmwareRelease>();
        }

        var byName = contents
            .Where(c => c.Type == "file")
            .ToDictionary(c => c.Name, c => c, StringComparer.Ordinal);

        var releases = new List<NinoTncFirmwareRelease>();
        foreach (var entry in byName.Values)
        {
            var match = HexFileNameRegex().Match(entry.Name);
            if (!match.Success) continue;
            int major = int.Parse(match.Groups[1].ValueSpan, provider: System.Globalization.CultureInfo.InvariantCulture);
            int minor = int.Parse(match.Groups[2].ValueSpan, provider: System.Globalization.CultureInfo.InvariantCulture);
            var version = new NinoTncFirmwareVersion(major, minor);
            var variant = version.ChipVariant;
            if (variant == NinoTncChipVariant.Unknown)
            {
                // Filename parses as a hex-like pattern but the major
                // doesn't map to a known chip — skip rather than guess.
                continue;
            }
            if (entry.DownloadUrl is null)
            {
                continue;
            }

            string checksumName = $"v{major}-{minor}-mplab-checksums.txt";
            Uri? checksumUrl = byName.TryGetValue(checksumName, out var checksumEntry) && checksumEntry.DownloadUrl is not null
                ? new Uri(checksumEntry.DownloadUrl)
                : null;

            releases.Add(new NinoTncFirmwareRelease(version, variant, new Uri(entry.DownloadUrl), checksumUrl));
        }
        return releases;
    }
}

// JSON shape returned by /repos/{owner}/{repo}/contents (only the
// fields we need). source-generated context keeps the package AOT-clean.

internal sealed record GitHubContentEntry(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("download_url")] string? DownloadUrl);

[JsonSerializable(typeof(IReadOnlyList<GitHubContentEntry>))]
[JsonSerializable(typeof(GitHubContentEntry))]
internal sealed partial class GitHubContentsContext : JsonSerializerContext;
