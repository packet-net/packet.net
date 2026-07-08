using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Packet.Node.Core.HeadEnd;

/// <summary>
/// A typed client for one head-end daemon's HTTP control plane (<c>headend/api.go</c>): the
/// machine surface PDN drives in the split-station topology (see
/// <c>docs/research/split-station-rf-headend.md</c>). Exposes the inventory (what the head-end
/// bridges), the line-control verb (<c>setBaud</c> for the Tait CCDI clock + the future baud sweep),
/// and a health probe. The raw byte pipe itself is a separate TCP dial (per
/// <see cref="HeadEndPortInfo.TcpPort"/>), not part of this control plane.
/// </summary>
/// <remarks>
/// The base address is injectable and the <see cref="System.Net.Http.HttpClient"/> is reusable /
/// substitutable (tests point it at a stub server / message handler). Absolute request URIs are
/// built off <see cref="BaseAddress"/> rather than <c>HttpClient.BaseAddress</c>, so a single shared
/// <c>HttpClient</c> can serve a whole fleet of head-ends (each client instance carries its own base).
/// </remarks>
public sealed class HeadEndClient
{
    // One process-wide HttpClient (the guidance for long-lived hosts): pooled sockets, no
    // per-head-end disposal concern. Each HeadEndClient carries its own BaseAddress, so the
    // shared instance serves every head-end. A 10 s timeout fails fast on an unreachable box.
    private static readonly HttpClient SharedHttp = new() { Timeout = TimeSpan.FromSeconds(10) };

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        // Omit null dataBits/parity/stopBits on the line request so an omitted param reads as
        // "leave unchanged" on the head-end (its lineRequest uses nil-means-unchanged pointers).
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly HttpClient http;

    /// <summary>The head-end HTTP base (<c>http://host:port/</c>) every verb hangs off. Its
    /// <see cref="Uri.Host"/> is also the host PDN dials for the raw byte pipes.</summary>
    public Uri BaseAddress { get; }

    /// <summary>Construct a client for the head-end at <paramref name="baseAddress"/> (built via
    /// <see cref="HeadEndAddress.ToBaseUri"/> from the manual <c>host:port</c>). A null
    /// <paramref name="httpClient"/> uses the shared process-wide instance.</summary>
    public HeadEndClient(Uri baseAddress, HttpClient? httpClient = null)
    {
        ArgumentNullException.ThrowIfNull(baseAddress);
        BaseAddress = EnsureTrailingSlash(baseAddress);
        http = httpClient ?? SharedHttp;
    }

    /// <summary>Build the <c>http://host:port/</c> base for a manual <c>host:port</c> address.</summary>
    public static Uri BaseAddressFor(string address) => HeadEndAddress.ToBaseUri(address);

    /// <summary><c>GET /inventory</c> — the instance id + every bridged device.</summary>
    public async Task<HeadEndInventory> GetInventoryAsync(CancellationToken cancellationToken = default)
    {
        using var response = await http.GetAsync(new Uri(BaseAddress, "inventory"), cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<HeadEndInventory>(Json, cancellationToken).ConfigureAwait(false)
               ?? throw new HttpRequestException("head-end returned an empty inventory body.");
    }

    /// <summary>
    /// <c>POST /ports/{deviceId}/line</c> — re-clock a bridged UART. This is exactly what the
    /// Stage-1 <c>setBaud</c> seam routes to (the data socket is a pure binary pipe and cannot carry
    /// line-rate changes). <paramref name="baud"/> is required; the other params are optional
    /// (omitted ⇒ unchanged). Returns the head-end's effective params.
    /// </summary>
    public async Task<HeadEndLineParams> SetLineAsync(
        string deviceId,
        int baud,
        int? dataBits = null,
        string? parity = null,
        int? stopBits = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(deviceId);
        var body = new LineRequest { Baud = baud, DataBits = dataBits, Parity = parity, StopBits = stopBits };
        var url = new Uri(BaseAddress, $"ports/{Uri.EscapeDataString(deviceId)}/line");
        using var response = await http.PostAsJsonAsync(url, body, Json, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<HeadEndLineParams>(Json, cancellationToken).ConfigureAwait(false)
               ?? throw new HttpRequestException("head-end returned an empty line-params body.");
    }

    /// <summary><c>GET /statusz</c> — the richer self-observability surface (#583): instance id,
    /// live bridge count, per-bridge client-connection state. Throws on any failure; in particular a
    /// pre-0.1.4 daemon answers 404 (<see cref="HttpRequestException.StatusCode"/> is
    /// <c>NotFound</c>), which callers treat as "fall back to <see cref="HealthAsync"/>".</summary>
    public async Task<HeadEndStatus> GetStatusAsync(CancellationToken cancellationToken = default)
    {
        using var response = await http.GetAsync(new Uri(BaseAddress, "statusz"), cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<HeadEndStatus>(Json, cancellationToken).ConfigureAwait(false)
               ?? throw new HttpRequestException("head-end returned an empty statusz body.");
    }

    /// <summary><c>GET /healthz</c> — true iff the head-end answers 2xx. Never throws (an
    /// unreachable / erroring head-end is simply "not healthy").</summary>
    public async Task<bool> HealthAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            using var response = await http.GetAsync(new Uri(BaseAddress, "healthz"), cancellationToken).ConfigureAwait(false);
            return response.IsSuccessStatusCode;
        }
        catch (HttpRequestException)
        {
            return false;
        }
    }

    private static Uri EnsureTrailingSlash(Uri uri) =>
        uri.AbsoluteUri.EndsWith('/') ? uri : new Uri(uri.AbsoluteUri + "/", UriKind.Absolute);

    // The POST /ports/{id}/line body. Nullable optional params are dropped on the wire (see Json).
    private sealed record LineRequest
    {
        public int Baud { get; init; }
        public int? DataBits { get; init; }
        public string? Parity { get; init; }
        public int? StopBits { get; init; }
    }
}
