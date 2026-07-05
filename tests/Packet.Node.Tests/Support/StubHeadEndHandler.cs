using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Packet.Node.Core.HeadEnd;

namespace Packet.Node.Tests.Support;

/// <summary>
/// An in-process stub of a head-end daemon's HTTP control plane (<c>headend/api.go</c>) as an
/// <see cref="HttpMessageHandler"/>: serves a configured <c>GET /inventory</c>, echoes
/// <c>POST /ports/{id}/line</c> back as effective params (recording each call + its raw body so a
/// test can assert the <c>setBaud</c> seam fired), and answers <c>GET /healthz</c>. Lets
/// <see cref="HeadEndClient"/> and the head-end factory branches be driven with no real HTTP socket.
/// </summary>
public sealed class StubHeadEndHandler : HttpMessageHandler
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly HeadEndInventory inventory;
    private readonly bool healthy;

    public StubHeadEndHandler(HeadEndInventory inventory, bool healthy = true)
    {
        this.inventory = inventory;
        this.healthy = healthy;
    }

    /// <summary>One recorded <c>POST /ports/{id}/line</c>: the device id and the raw JSON body.</summary>
    public sealed record LineCall(string DeviceId, string RawBody);

    /// <summary>Every line-control POST the handler received, in order.</summary>
    public List<LineCall> LineCalls { get; } = [];

    private int lastBaud = -1;

    /// <summary>The baud of the most recent <c>POST /ports/{id}/line</c>, or -1 if none — the shared
    /// clock a baud-sweep loopback gates its MODEL answer on. Read cross-thread (the responder task).</summary>
    public int LastBaud => Volatile.Read(ref lastBaud);

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var path = request.RequestUri!.AbsolutePath;

        if (request.Method == HttpMethod.Get && path == "/inventory")
        {
            return Json200(inventory);
        }

        if (request.Method == HttpMethod.Get && path == "/healthz")
        {
            return new HttpResponseMessage(healthy ? HttpStatusCode.OK : HttpStatusCode.ServiceUnavailable);
        }

        if (request.Method == HttpMethod.Post && path.StartsWith("/ports/", StringComparison.Ordinal)
            && path.EndsWith("/line", StringComparison.Ordinal))
        {
            var id = Uri.UnescapeDataString(path["/ports/".Length..^"/line".Length]);
            var body = request.Content is null
                ? ""
                : await request.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            LineCalls.Add(new LineCall(id, body));

            var node = JsonNode.Parse(body)?.AsObject();
            if (node?["baud"] is null)
            {
                return new HttpResponseMessage(HttpStatusCode.BadRequest);
            }
            // Echo effective params: the requested baud + merged defaults for omitted fields
            // (exactly the head-end's nil-means-unchanged merge).
            Volatile.Write(ref lastBaud, (int)node["baud"]!);
            var effective = new HeadEndLineParams
            {
                Baud = (int)node["baud"]!,
                DataBits = node["dataBits"] is { } db ? (int)db : 8,
                Parity = node["parity"] is { } p ? (string)p! : "none",
                StopBits = node["stopBits"] is { } sb ? (int)sb : 1,
            };
            return Json200(effective);
        }

        return new HttpResponseMessage(HttpStatusCode.NotFound);
    }

    private static HttpResponseMessage Json200<T>(T value) =>
        new(HttpStatusCode.OK) { Content = JsonContent.Create(value, options: Json) };
}
