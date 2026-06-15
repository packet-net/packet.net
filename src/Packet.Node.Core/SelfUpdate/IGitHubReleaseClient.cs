using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;

namespace Packet.Node.Core.SelfUpdate;

/// <summary>
/// The latest published node release, as the github-channel available-version check needs it:
/// the <c>node-v*</c> tag and the per-arch <c>.deb</c> asset download URLs.
/// </summary>
/// <param name="TagName">The release tag (e.g. <c>node-v0.9.0</c>).</param>
/// <param name="Assets">The release's downloadable assets (name → browser download URL).</param>
public sealed record GitHubRelease(string TagName, IReadOnlyDictionary<string, Uri> Assets);

/// <summary>
/// Fetches the latest node release from the GitHub Releases API. Abstracted so the
/// available-version check + the Apply request-file build are unit-testable without hitting the
/// real API (a fake returns a canned release / null). The production impl is rate-limited and
/// total: a network error, a non-2xx, or a malformed body yields <c>null</c>, never an exception.
/// </summary>
public interface IGitHubReleaseClient
{
    /// <summary>The latest <c>node-v*</c> release, or <c>null</c> if it can't be determined
    /// (offline / rate-limited / API error / no releases). Never throws.</summary>
    Task<GitHubRelease?> GetLatestNodeReleaseAsync(CancellationToken cancellationToken = default);

    /// <summary>Download a small text asset (the release <c>SHA256SUMS</c>) from an HTTPS URL and
    /// return its body, or <c>null</c> on any fault / non-https / over-cap. Never throws. Used to
    /// look up the expected sha256 of the per-arch <c>.deb</c> for the github Apply request.</summary>
    Task<string?> GetTextAssetAsync(Uri url, CancellationToken cancellationToken = default);
}

/// <summary>
/// The production <see cref="IGitHubReleaseClient"/>: GETs <c>/repos/{owner}/{repo}/releases/latest</c>
/// over HTTPS, with a short-TTL in-process cache so repeated <c>/info</c> calls don't hammer the
/// API (unauthenticated GitHub allows ~60 req/h/IP — the cache keeps us comfortably under). Every
/// fault — offline, non-2xx (incl. 403 rate-limit / 404 no-release), malformed JSON, timeout — is
/// swallowed to <c>null</c>.
/// </summary>
public sealed partial class GitHubReleaseClient : IGitHubReleaseClient, IDisposable
{
    /// <summary>The packet.net node-host repo whose Releases carry the <c>node-v*</c> tags + <c>.deb</c> assets.
    /// The repos were transferred from <c>m0lte/*</c> to the <c>packet-net</c> org (2026-06); the old
    /// owner still 301-redirects, but the API client treats a non-200 (the redirect) as "no update", so the
    /// canonical owner must be used directly.</summary>
    public const string DefaultOwner = "packet-net";

    /// <summary>The repo name.</summary>
    public const string DefaultRepo = "packet.net";

    private static readonly TimeSpan DefaultCacheTtl = TimeSpan.FromMinutes(10);

    private readonly HttpClient http;
    private readonly TimeProvider clock;
    private readonly ILogger<GitHubReleaseClient> log;
    private readonly string owner;
    private readonly string repo;
    private readonly TimeSpan cacheTtl;
    private readonly SemaphoreSlim gate = new(1, 1);

    private GitHubRelease? cached;
    private DateTimeOffset cachedAt = DateTimeOffset.MinValue;
    private bool haveCached;

    /// <param name="http">The HTTPS client (in DI from <c>IHttpClientFactory</c>). The
    /// <c>User-Agent</c> header is set per request (GitHub requires one).</param>
    public GitHubReleaseClient(
        HttpClient http,
        TimeProvider clock,
        ILoggerFactory loggerFactory,
        string owner = DefaultOwner,
        string repo = DefaultRepo,
        TimeSpan? cacheTtl = null)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(loggerFactory);
        this.http = http;
        this.clock = clock;
        log = loggerFactory.CreateLogger<GitHubReleaseClient>();
        this.owner = owner;
        this.repo = repo;
        this.cacheTtl = cacheTtl ?? DefaultCacheTtl;
    }

    /// <inheritdoc/>
    public async Task<GitHubRelease?> GetLatestNodeReleaseAsync(CancellationToken cancellationToken = default)
    {
        var now = clock.GetUtcNow();
        if (haveCached && now - cachedAt < cacheTtl)
        {
            return cached;
        }

        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            now = clock.GetUtcNow();
            if (haveCached && now - cachedAt < cacheTtl)
            {
                return cached;
            }

            var result = await FetchAsync(cancellationToken).ConfigureAwait(false);
            // Cache even a null result — a 403 rate-limit shouldn't trigger a retry storm.
            cached = result;
            cachedAt = now;
            haveCached = true;
            return result;
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task<GitHubRelease?> FetchAsync(CancellationToken cancellationToken)
    {
        var url = $"https://api.github.com/repos/{owner}/{repo}/releases/latest";
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            // GitHub rejects requests without a User-Agent; the recommended Accept pins the API version.
            request.Headers.UserAgent.ParseAdd("packet.net-node-selfupdate");
            request.Headers.Accept.ParseAdd("application/vnd.github+json");

            using var response = await http.SendAsync(
                request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                LogNotOk((int)response.StatusCode);
                return null;
            }

            var dto = await response.Content.ReadFromJsonAsync(
                GitHubReleaseJson.Default.GitHubReleaseDto, cancellationToken).ConfigureAwait(false);
            if (dto?.TagName is null || !dto.TagName.StartsWith("node-v", StringComparison.OrdinalIgnoreCase))
            {
                // releases/latest returned something that isn't a node-v release (e.g. a lib-v
                // tag was the most recent full release) → treat as "no node release known".
                return null;
            }

            var assets = new Dictionary<string, Uri>(StringComparer.Ordinal);
            foreach (var a in dto.Assets ?? [])
            {
                if (!string.IsNullOrEmpty(a.Name) && Uri.TryCreate(a.BrowserDownloadUrl, UriKind.Absolute, out var u))
                {
                    assets[a.Name] = u;
                }
            }

            return new GitHubRelease(dto.TagName, assets);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or InvalidOperationException)
        {
            // Offline / timeout / malformed body — the safe default. (TaskCanceledException covers
            // the HttpClient timeout; a genuine caller cancellation rethrows below.)
            if (ex is TaskCanceledException && cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            LogFault(ex);
            return null;
        }
    }

    /// <inheritdoc/>
    public async Task<string?> GetTextAssetAsync(Uri url, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(url);
        if (!string.Equals(url.Scheme, Uri.UriSchemeHttps, StringComparison.Ordinal))
        {
            return null; // only https assets (the same trust posture as the catalog fetcher).
        }

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.UserAgent.ParseAdd("packet.net-node-selfupdate");

            using var response = await http.SendAsync(
                request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                LogNotOk((int)response.StatusCode);
                return null;
            }
            // SHA256SUMS is tiny (a handful of lines); cap defensively at 1 MB.
            if (response.Content.Headers.ContentLength is > 1L * 1024 * 1024)
            {
                return null;
            }
            return await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or InvalidOperationException)
        {
            if (ex is TaskCanceledException && cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            LogFault(ex);
            return null;
        }
    }

    [LoggerMessage(Level = LogLevel.Debug, Message = "GitHub releases/latest returned HTTP {Status}; treating as no-update.")]
    private partial void LogNotOk(int status);

    [LoggerMessage(Level = LogLevel.Debug, Message = "GitHub release check failed; treating as no-update.")]
    private partial void LogFault(Exception ex);

    /// <summary>Dispose the single-flight gate. The client is an app-lifetime DI singleton.</summary>
    public void Dispose() => gate.Dispose();
}

/// <summary>The slice of the GitHub release JSON the version check + Apply request-file need.</summary>
internal sealed record GitHubReleaseDto(
    [property: JsonPropertyName("tag_name")] string? TagName,
    [property: JsonPropertyName("assets")] IReadOnlyList<GitHubAssetDto>? Assets);

internal sealed record GitHubAssetDto(
    [property: JsonPropertyName("name")] string? Name,
    [property: JsonPropertyName("browser_download_url")] string? BrowserDownloadUrl);

[JsonSourceGenerationOptions(PropertyNameCaseInsensitive = true)]
[JsonSerializable(typeof(GitHubReleaseDto))]
internal sealed partial class GitHubReleaseJson : JsonSerializerContext;
