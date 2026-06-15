using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;

namespace Packet.Node.Core.SelfUpdate;

/// <summary>
/// Reads the self-contained channel's <c>latest.json</c> feed (the manifest <c>publish-node.yml</c>
/// writes: a top-level <c>version</c> + per-arch artifact blocks) to learn the latest published
/// version. Abstracted so the available-version check is unit-testable without a live feed; the
/// production impl is total — offline / non-2xx / malformed JSON → <c>null</c>, never an exception.
/// </summary>
public interface ISelfContainedFeedClient
{
    /// <summary>The <c>version</c> from the configured feed's <c>latest.json</c>, or <c>null</c> if
    /// no feed is configured or it can't be read. Never throws.</summary>
    Task<string?> GetLatestVersionAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// The production <see cref="ISelfContainedFeedClient"/>: GETs <c>{feedUrl}/latest.json</c> over
/// HTTPS with a short-TTL cache. When no feed URL is configured (the common case until a public
/// feed host exists) it short-circuits to <c>null</c> with zero network calls.
/// </summary>
public sealed partial class SelfContainedFeedClient : ISelfContainedFeedClient, IDisposable
{
    private static readonly TimeSpan DefaultCacheTtl = TimeSpan.FromMinutes(10);

    private readonly HttpClient http;
    private readonly TimeProvider clock;
    private readonly ILogger<SelfContainedFeedClient> log;
    private readonly Uri? feedUrl;
    private readonly TimeSpan cacheTtl;
    private readonly SemaphoreSlim gate = new(1, 1);

    private string? cached;
    private DateTimeOffset cachedAt = DateTimeOffset.MinValue;
    private bool haveCached;

    /// <param name="feedUrl">The configured feed base URL (e.g. the public release mirror), or
    /// <c>null</c> when none is configured — then the check is a no-op.</param>
    public SelfContainedFeedClient(
        HttpClient http,
        TimeProvider clock,
        ILoggerFactory loggerFactory,
        Uri? feedUrl,
        TimeSpan? cacheTtl = null)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(loggerFactory);
        this.http = http;
        this.clock = clock;
        log = loggerFactory.CreateLogger<SelfContainedFeedClient>();
        this.feedUrl = feedUrl;
        this.cacheTtl = cacheTtl ?? DefaultCacheTtl;
    }

    /// <inheritdoc/>
    public async Task<string?> GetLatestVersionAsync(CancellationToken cancellationToken = default)
    {
        if (feedUrl is null)
        {
            return null; // no feed configured → nothing to check, no network call.
        }

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

    private async Task<string?> FetchAsync(CancellationToken cancellationToken)
    {
        // feedUrl is non-null here (guarded in the caller).
        var url = new Uri(feedUrl!, "latest.json");
        if (!string.Equals(url.Scheme, Uri.UriSchemeHttps, StringComparison.Ordinal))
        {
            return null; // only https feeds (the same trust posture as the catalog fetcher).
        }

        try
        {
            var dto = await http.GetFromJsonAsync(
                url, LatestJson.Default.LatestJsonDto, cancellationToken).ConfigureAwait(false);
            return string.IsNullOrWhiteSpace(dto?.Version) ? null : dto!.Version;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or InvalidOperationException)
        {
            if (ex is TaskCanceledException && cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            LogFault(ex);
            return null;
        }
    }

    [LoggerMessage(Level = LogLevel.Debug, Message = "Self-contained feed check failed; treating as no-update.")]
    private partial void LogFault(Exception ex);

    /// <summary>Dispose the single-flight gate. The client is an app-lifetime DI singleton.</summary>
    public void Dispose() => gate.Dispose();
}

/// <summary>The slice of <c>latest.json</c> the version check needs (just the version).</summary>
internal sealed record LatestJsonDto(
    [property: JsonPropertyName("version")] string? Version);

[JsonSourceGenerationOptions(PropertyNameCaseInsensitive = true)]
[JsonSerializable(typeof(LatestJsonDto))]
internal sealed partial class LatestJson : JsonSerializerContext;
