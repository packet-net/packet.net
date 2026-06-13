using Microsoft.Extensions.Logging;

namespace Packet.Node.Core.Applications.Catalog;

/// <summary>
/// The download seam: fetch a remote artifact to a local temp file. Abstracted so the
/// installer tests can inject a fake that serves local fixture bytes (no live network) while
/// production streams over HTTPS.
/// </summary>
public interface IArtifactFetcher
{
    /// <summary>Fetch <paramref name="url"/> to a fresh temp file and return its path; the
    /// caller owns deleting it. Enforces <paramref name="maxBytes"/> (a too-large body is an
    /// error, not a truncated file). The default implementation REFUSES non-https urls.</summary>
    Task<string> FetchToTempAsync(Uri url, long maxBytes, CancellationToken cancellationToken);
}

/// <summary>
/// The production <see cref="IArtifactFetcher"/>: streams an https artifact to a temp file,
/// enforcing a byte cap and refusing any non-https scheme (the catalog only ever pins https
/// urls — a plain-http or file url is a sign of a tampered catalog).
/// </summary>
public sealed partial class HttpArtifactFetcher : IArtifactFetcher
{
    /// <summary>The default body cap — 512 MB. DAPPS binaries are ~100 MB; this leaves
    /// headroom while still bounding a runaway download.</summary>
    public const long DefaultMaxBytes = 512L * 1024 * 1024;

    private readonly HttpClient http;
    private readonly ILogger<HttpArtifactFetcher> log;

    /// <summary>Create a fetcher over <paramref name="http"/> (the caller owns its lifetime;
    /// in DI it comes from <c>IHttpClientFactory</c>).</summary>
    public HttpArtifactFetcher(HttpClient http, ILoggerFactory loggerFactory)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(loggerFactory);
        this.http = http;
        log = loggerFactory.CreateLogger<HttpArtifactFetcher>();
    }

    /// <inheritdoc/>
    public async Task<string> FetchToTempAsync(Uri url, long maxBytes, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(url);
        if (!string.Equals(url.Scheme, Uri.UriSchemeHttps, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"refusing to fetch '{url}': only https artifacts are allowed.");
        }

        var tempPath = Path.Combine(Path.GetTempPath(), $"pdn-fetch-{Guid.NewGuid():N}.tmp");
        LogFetching(url, tempPath);

        try
        {
            using var response = await http.GetAsync(
                url, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();

            // A Content-Length over the cap fails fast, before streaming a single byte.
            if (response.Content.Headers.ContentLength is { } declared && declared > maxBytes)
            {
                throw new InvalidOperationException(
                    $"artifact '{url}' declares {declared} bytes, over the {maxBytes}-byte cap.");
            }

            await using var source = await response.Content
                .ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            await using var dest = new FileStream(
                tempPath, FileMode.Create, FileAccess.Write, FileShare.None,
                bufferSize: 1 << 16, useAsync: true);

            var buffer = new byte[1 << 16];
            long total = 0;
            int read;
            while ((read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
            {
                total += read;
                if (total > maxBytes)
                {
                    throw new InvalidOperationException(
                        $"artifact '{url}' exceeded the {maxBytes}-byte cap mid-stream.");
                }
                await dest.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            }

            return tempPath;
        }
        catch
        {
            TryDelete(tempPath);
            throw;
        }
    }

    private static void TryDelete(string path)
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
            // Best-effort temp cleanup only.
        }
    }

    [LoggerMessage(Level = LogLevel.Debug, Message = "Fetching {Url} to {TempPath}.")]
    private partial void LogFetching(Uri url, string tempPath);
}
