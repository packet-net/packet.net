using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Packet.Node.Core.Oarc;

/// <summary>The outcome of one ingest POST.</summary>
public enum OarcIngestOutcome
{
    /// <summary>The collector accepted the datagram (HTTP 200/202). Note: acceptance is a queue
    /// admission, not a guarantee it passes the collector's async validation — but it is the most
    /// we can observe synchronously, and a well-formed datagram (our contract) is applied.</summary>
    Accepted,

    /// <summary>The collector rejected the datagram synchronously (HTTP 400/422) — our payload is
    /// malformed. This will never succeed on retry; the reporter drops it (and we log the server's
    /// reason, which is a bug on our side).</summary>
    Rejected,

    /// <summary>A transport-level failure (network/DNS/TLS/timeout, HTTP 429, or any 5xx). Transient
    /// — the reporter retries with backoff.</summary>
    TransportError,
}

/// <summary>The result of one ingest POST: the <see cref="Outcome"/>, the HTTP status (when there was
/// a response), and an optional detail (the server's error text on a rejection).</summary>
public readonly record struct OarcIngestResult(OarcIngestOutcome Outcome, int? StatusCode, string? Detail)
{
    /// <summary>The collector accepted the datagram.</summary>
    public bool Accepted => Outcome == OarcIngestOutcome.Accepted;

    /// <summary>Whether a retry could plausibly succeed: a transport error (incl. 429/5xx) is
    /// retryable; a synchronous rejection (400/422 — our payload is wrong) is not.</summary>
    public bool ShouldRetry => Outcome == OarcIngestOutcome.TransportError;

    /// <summary>Classify an HTTP status into an outcome. 200/202 → accepted; 400/422 → rejected
    /// (our bug); everything else (429, 5xx, redirects, …) → a retryable transport error.</summary>
    public static OarcIngestResult FromStatus(int status, string? detail) => status switch
    {
        200 or 202 => new OarcIngestResult(OarcIngestOutcome.Accepted, status, null),
        400 or 422 => new OarcIngestResult(OarcIngestOutcome.Rejected, status, detail),
        _ => new OarcIngestResult(OarcIngestOutcome.TransportError, status, detail),
    };
}

/// <summary>The thin HTTP layer to the OARC collector's typed ingest routes — the <i>how</i> of
/// reporting (URL composition, JSON shaping, status classification), with no <i>when</i>/<i>what</i>
/// policy (that is the <c>OarcReporter</c>'s). Open ingest (no auth), so no credential here.</summary>
public interface IOarcIngestClient
{
    /// <summary>POST one event to its typed route under <paramref name="baseUrl"/>. Never throws on a
    /// transport/HTTP failure — those become an <see cref="OarcIngestResult"/> — except a genuine
    /// shutdown cancellation (a cancelled <paramref name="cancellationToken"/>), which propagates.
    /// The base URL is taken per-call so a hot config change to <c>oarc.baseUrl</c> applies at once.</summary>
    Task<OarcIngestResult> ReportAsync(OarcEvent ev, string baseUrl, CancellationToken cancellationToken = default);
}

/// <inheritdoc cref="IOarcIngestClient"/>
public sealed partial class OarcIngestClient : IOarcIngestClient
{
    // Canonical outbound shape: camelCase Web defaults (belt-and-suspenders — every member also
    // pins its name) and omit-when-null so an event carries only the fields we actually set.
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    // The server's error text can be large; cap what we log so a misbehaving collector can't blow
    // up the log line.
    private const int MaxErrorDetailChars = 2000;

    private readonly HttpClient http;
    private readonly ILogger<OarcIngestClient> logger;

    public OarcIngestClient(HttpClient http, ILogger<OarcIngestClient>? logger = null)
    {
        this.http = http ?? throw new ArgumentNullException(nameof(http));
        this.logger = logger ?? NullLogger<OarcIngestClient>.Instance;
    }

    public async Task<OarcIngestResult> ReportAsync(OarcEvent ev, string baseUrl, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(ev);
        ArgumentException.ThrowIfNullOrWhiteSpace(baseUrl);

        Uri uri;
        try
        {
            // EndpointPath is relative + slash-free ("api/ingest/node-up"); a trailing slash on the
            // base keeps the combine from dropping a path segment.
            var root = new Uri(baseUrl.EndsWith('/') ? baseUrl : baseUrl + "/", UriKind.Absolute);
            uri = new Uri(root, ev.EndpointPath);
        }
        catch (UriFormatException ex)
        {
            // A bad base URL is a config error, not transient — treat as a (non-retryable) rejection.
            LogBadBaseUrl(baseUrl, ex.Message);
            return new OarcIngestResult(OarcIngestOutcome.Rejected, null, $"bad base URL: {ex.Message}");
        }

        try
        {
            var json = JsonSerializer.Serialize(ev, ev.GetType(), JsonOptions);
            using var content = new StringContent(json, Encoding.UTF8, "application/json");
            using var response = await http.PostAsync(uri, content, cancellationToken).ConfigureAwait(false);

            string? detail = null;
            if (response.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.UnprocessableEntity)
            {
                // A synchronous reject means our payload violates the collector's contract — capture
                // and log the reason; it is a defect on our side, not a transient blip.
                detail = await SafeReadBodyAsync(response, cancellationToken).ConfigureAwait(false);
                LogRejected(ev.EndpointPath, (int)response.StatusCode, detail ?? "(no body)");
            }

            return OarcIngestResult.FromStatus((int)response.StatusCode, detail);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;   // genuine shutdown — propagate so the reporter's loop unwinds cleanly
        }
        catch (Exception ex)   // network/DNS/TLS down, or a per-request timeout (TaskCanceled w/o our token)
        {
            LogTransportFault(ev.EndpointPath, ex.Message);
            return new OarcIngestResult(OarcIngestOutcome.TransportError, null, ex.Message);
        }
    }

    private static async Task<string?> SafeReadBodyAsync(HttpResponseMessage response, CancellationToken ct)
    {
        try
        {
            var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            return body.Length > MaxErrorDetailChars ? body[..MaxErrorDetailChars] : body;
        }
        catch
        {
            return null;   // reading the error body must never itself throw
        }
    }

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "OARC ingest: base URL '{BaseUrl}' is not usable ({Reason}); event dropped.")]
    private partial void LogBadBaseUrl(string baseUrl, string reason);

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "OARC ingest: collector rejected {Endpoint} ({Status}): {Detail}. This is a payload bug on our side; not retried.")]
    private partial void LogRejected(string endpoint, int status, string detail);

    [LoggerMessage(Level = LogLevel.Debug,
        Message = "OARC ingest: transport error posting {Endpoint} ({Reason}); will retry with backoff.")]
    private partial void LogTransportFault(string endpoint, string reason);
}
