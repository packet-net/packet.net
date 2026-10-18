using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Packet.Node.Core.M0lteMap;

/// <summary>How one report POST went.</summary>
public enum M0lteMapPostOutcome
{
    /// <summary>The collector took the report (any 2xx; LinBPQ looks for <c>200 OK</c>).</summary>
    Accepted,

    /// <summary>The collector refused it (a 4xx other than 429). Retrying the same report will
    /// not help; the reporter waits for the next scheduled report.</summary>
    Rejected,

    /// <summary>The collector could not be reached, timed out, or answered 429 or 5xx. Worth
    /// retrying sooner than the next scheduled report.</summary>
    TransportError,
}

/// <summary>The result of one report POST: the outcome, the HTTP status when there was a
/// response, and a short reason for the log.</summary>
public readonly record struct M0lteMapPostResult(M0lteMapPostOutcome Outcome, int? StatusCode, string? Detail)
{
    /// <summary>Classify an HTTP status.</summary>
    public static M0lteMapPostResult FromStatus(int status, string? detail) => status switch
    {
        >= 200 and < 300 => new(M0lteMapPostOutcome.Accepted, status, null),
        429 or >= 500 => new(M0lteMapPostOutcome.TransportError, status, detail),
        >= 400 => new(M0lteMapPostOutcome.Rejected, status, detail),
        _ => new(M0lteMapPostOutcome.TransportError, status, detail),
    };
}

/// <summary>The HTTP layer to the UK packet map's collector: where and how a report is sent.
/// When and what is the <see cref="M0lteMapReporter"/>'s business.</summary>
public interface IM0lteMapClient
{
    /// <summary>POST one report for <paramref name="nodeCall"/>. Never throws for a network or
    /// HTTP failure (those become the result); a cancelled <paramref name="cancellationToken"/>
    /// propagates.</summary>
    Task<M0lteMapPostResult> PostAsync(string nodeCall, M0lteMapNodeData report, CancellationToken cancellationToken = default);
}

/// <inheritdoc cref="IM0lteMapClient"/>
public sealed class M0lteMapClient : IM0lteMapClient
{
    /// <summary>The collector LinBPQ reports to (it sends plain HTTP to this host; pdn uses
    /// HTTPS, which the same service answers). The report goes to
    /// <c>api/NodeData/{nodeCall}</c> under it.</summary>
    public static readonly Uri DefaultBaseUri = new("https://packetnodes.spots.radio/");

    /// <summary>The serialiser settings for the report: the DTOs pin every wire name, null
    /// members are left out, and text is escaped only where JSON requires it, so an operator's
    /// <c>IL2P+CRC</c> arrives as written rather than as <c>IL2P+CRC</c> (this body is never
    /// embedded in HTML, which is what the default encoder guards against).</summary>
    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private const int MaxDetailChars = 300;

    private readonly HttpClient http;
    private readonly Uri baseUri;
    private readonly ProductInfoHeaderValue userAgent;

    /// <param name="http">The HTTP client (its timeout bounds each POST).</param>
    /// <param name="version">The node's version, sent in the User-Agent as <c>pdn/&lt;version&gt;</c>
    /// (LinBPQ sends <c>linbpq/&lt;version&gt;</c>).</param>
    /// <param name="baseUri">The collector root. Null = <see cref="DefaultBaseUri"/>; tests point
    /// it at a fake. Not an operator setting.</param>
    public M0lteMapClient(HttpClient http, string version, Uri? baseUri = null)
    {
        this.http = http ?? throw new ArgumentNullException(nameof(http));
        this.baseUri = baseUri ?? DefaultBaseUri;
        userAgent = new ProductInfoHeaderValue(M0lteMapReportBuilder.SoftwareName, SafeVersion(version));
    }

    /// <summary>The URL a report for <paramref name="nodeCall"/> is sent to.</summary>
    public Uri ReportUri(string nodeCall) => new(baseUri, "api/NodeData/" + Uri.EscapeDataString(nodeCall));

    /// <summary>Serialise a report exactly as it goes on the wire.</summary>
    public static string Serialize(M0lteMapNodeData report) => JsonSerializer.Serialize(report, JsonOptions);

    /// <inheritdoc/>
    public async Task<M0lteMapPostResult> PostAsync(string nodeCall, M0lteMapNodeData report, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(nodeCall);
        ArgumentNullException.ThrowIfNull(report);

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, ReportUri(nodeCall))
            {
                Content = new StringContent(Serialize(report), Encoding.UTF8, "application/json"),
            };
            request.Headers.UserAgent.Add(userAgent);
            using var response = await http.SendAsync(request, cancellationToken).ConfigureAwait(false);
            var status = (int)response.StatusCode;
            string? detail = null;
            if (status is < 200 or >= 300)
            {
                detail = await ReadDetailAsync(response, cancellationToken).ConfigureAwait(false);
            }
            return M0lteMapPostResult.FromStatus(status, detail);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)   // DNS, connection, TLS, or the client's own timeout
        {
            return new M0lteMapPostResult(M0lteMapPostOutcome.TransportError, null, ex.Message);
        }
    }

    private static async Task<string?> ReadDetailAsync(HttpResponseMessage response, CancellationToken ct)
    {
        try
        {
            var body = (await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false)).Trim();
            if (body.Length == 0)
            {
                return response.ReasonPhrase;
            }
            return body.Length > MaxDetailChars ? body[..MaxDetailChars] : body;
        }
        catch
        {
            return response.ReasonPhrase;
        }
    }

    // A product version token may not carry spaces or separators; a dev build's version string
    // could. Keep what is legal and fall back to "dev".
    private static string SafeVersion(string? version)
    {
        const string separators = "()<>@,;:\\\"/[]?={}";
        var kept = new string((version ?? "").Where(c => c is > ' ' and < (char)127 && !separators.Contains(c)).ToArray());
        return kept.Length == 0 ? "dev" : kept;
    }
}
