using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Packet.Node.Core.Oarc;

namespace Packet.Node.Tests.Oarc;

/// <summary>
/// Tests for the OARC ingest client (#459): URL composition, the exact outbound JSON shape (camelCase
/// wire names, epoch time, no <c>@type</c> on typed routes, null-omission), and HTTP status →
/// <see cref="OarcIngestResult"/> classification incl. the never-throw contract. The wire format is
/// validated against the real collector by the env-gated live test; here we pin it deterministically.
/// </summary>
public sealed class OarcIngestClientTests
{
    /// <summary>A handler that records every request (method, URI, body) and returns a scripted
    /// response — or throws, to exercise the transport-error path.</summary>
    private sealed class CapturingHandler : HttpMessageHandler
    {
        private readonly HttpStatusCode status;
        private readonly string body;
        private readonly Exception? throwThis;

        public CapturingHandler(HttpStatusCode status = HttpStatusCode.Accepted, string body = "", Exception? throwThis = null)
        {
            this.status = status;
            this.body = body;
            this.throwThis = throwThis;
        }

        public HttpRequestMessage? LastRequest { get; private set; }
        public string? LastBody { get; private set; }
        public int Calls { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Calls++;
            LastRequest = request;
            LastBody = request.Content is null ? null : await request.Content.ReadAsStringAsync(ct);
            if (throwThis is not null)
            {
                throw throwThis;
            }
            return new HttpResponseMessage(status) { Content = new StringContent(body) };
        }
    }

    private static OarcIngestClient Client(CapturingHandler handler) => new(new HttpClient(handler));

    private const string Base = "https://node-api.packet.oarc.uk/";

    private static OarcNodeUpEvent NodeUp() => new()
    {
        Time = 1_781_771_414,
        NodeCall = "Q0PDN",
        NodeAlias = "PDNTST",
        Locator = "IO91wm",
        Software = "pdn",
        Version = "0.18.0",
    };

    [Fact]
    public async Task Posts_to_the_typed_route_under_the_base_url()
    {
        var handler = new CapturingHandler();
        var result = await Client(handler).ReportAsync(NodeUp(), Base);

        result.Accepted.Should().BeTrue();
        handler.LastRequest!.Method.Should().Be(HttpMethod.Post);
        handler.LastRequest.RequestUri!.ToString().Should().Be("https://node-api.packet.oarc.uk/api/ingest/node-up");
    }

    [Theory]
    [InlineData("https://node-api.packet.oarc.uk/")]   // trailing slash
    [InlineData("https://node-api.packet.oarc.uk")]    // no trailing slash — must still combine right
    public async Task Composes_the_url_regardless_of_trailing_slash(string baseUrl)
    {
        var handler = new CapturingHandler();
        await Client(handler).ReportAsync(NodeUp(), baseUrl);
        handler.LastRequest!.RequestUri!.ToString().Should().Be("https://node-api.packet.oarc.uk/api/ingest/node-up");
    }

    [Fact]
    public async Task Serialises_camelcase_wire_names_epoch_time_and_no_type_discriminator()
    {
        var handler = new CapturingHandler();
        await Client(handler).ReportAsync(NodeUp(), Base);

        var json = JsonNode.Parse(handler.LastBody!)!.AsObject();
        ((string?)json["nodeCall"]).Should().Be("Q0PDN");
        ((string?)json["nodeAlias"]).Should().Be("PDNTST");
        ((string?)json["locator"]).Should().Be("IO91wm");
        ((string?)json["software"]).Should().Be("pdn");
        ((long?)json["time"]).Should().Be(1_781_771_414, "time is unix epoch seconds, a JSON number");
        json.ContainsKey("@type").Should().BeFalse("typed routes infer the type from the route");
        json.ContainsKey("endpointPath").Should().BeFalse("the route is not part of the body");
    }

    [Fact]
    public async Task Omits_optional_fields_that_are_null()
    {
        // NodeUp has no lat/lon set → those keys must be absent, not null.
        var handler = new CapturingHandler();
        await Client(handler).ReportAsync(NodeUp(), Base);

        var json = JsonNode.Parse(handler.LastBody!)!.AsObject();
        json.ContainsKey("latitude").Should().BeFalse();
        json.ContainsKey("longitude").Should().BeFalse();
    }

    [Fact]
    public async Task Link_status_emits_the_exact_wire_names_and_required_counters()
    {
        var ev = new OarcLinkStatusEvent
        {
            Time = 1_781_771_595, Node = "Q0PDN", Id = 1, Direction = "outgoing", Port = "1",
            Remote = "GB7RDG", Local = "Q0PDN-1",
            FramesSent = 10, FramesReceived = 12, FramesResent = 1, FramesQueued = 0,
            BytesSent = 800, BytesReceived = 950, L2RttMs = 340,
        };
        var handler = new CapturingHandler();
        await Client(handler).ReportAsync(ev, Base);

        handler.LastRequest!.RequestUri!.AbsolutePath.Should().Be("/api/ingest/link-status");
        var json = JsonNode.Parse(handler.LastBody!)!.AsObject();
        ((string?)json["direction"]).Should().Be("outgoing");
        ((long?)json["frmsSent"]).Should().Be(10);
        ((long?)json["frmsRcvd"]).Should().Be(12);
        ((long?)json["frmsResent"]).Should().Be(1);
        ((long?)json["l2rttMs"]).Should().Be(340);
        json.ContainsKey("frmsQdPeak").Should().BeFalse("an unset optional counter is omitted");
        json.ContainsKey("bpsTxMean").Should().BeFalse();
    }

    [Fact]
    public async Task L2trace_uses_the_trace_dialect_field_names()
    {
        var ev = new OarcL2TraceEvent
        {
            ReportFrom = "Q0PDN", Time = 1, Port = "1", Direction = "sent", IsRf = true,
            Source = "Q0PDN", Destination = "GB7RDG", Control = 0, L2Type = "I", CommandResponse = "C",
            Modulo = 8, ReceiveSequence = 2, TransmitSequence = 3, Pid = 240, IFieldLength = 5,
        };
        var handler = new CapturingHandler();
        await Client(handler).ReportAsync(ev, Base);

        handler.LastRequest!.RequestUri!.AbsolutePath.Should().Be("/api/ingest/l2trace");
        var json = JsonNode.Parse(handler.LastBody!)!.AsObject();
        ((string?)json["dirn"]).Should().Be("sent", "traces use dirn=sent/rcvd, not incoming/outgoing");
        ((bool?)json["isRF"]).Should().BeTrue();
        ((string?)json["l2Type"]).Should().Be("I");
        ((string?)json["cr"]).Should().Be("C");
        ((int?)json["ilen"]).Should().Be(5);
        ((int?)json["tseq"]).Should().Be(3);
    }

    [Theory]
    [InlineData(HttpStatusCode.Accepted, OarcIngestOutcome.Accepted, false)]
    [InlineData(HttpStatusCode.OK, OarcIngestOutcome.Accepted, false)]
    [InlineData(HttpStatusCode.BadRequest, OarcIngestOutcome.Rejected, false)]
    [InlineData(HttpStatusCode.UnprocessableEntity, OarcIngestOutcome.Rejected, false)]
    [InlineData(HttpStatusCode.TooManyRequests, OarcIngestOutcome.TransportError, true)]
    [InlineData(HttpStatusCode.InternalServerError, OarcIngestOutcome.TransportError, true)]
    [InlineData(HttpStatusCode.ServiceUnavailable, OarcIngestOutcome.TransportError, true)]
    public async Task Classifies_http_status_into_outcome(HttpStatusCode status, OarcIngestOutcome expected, bool retryable)
    {
        var handler = new CapturingHandler(status);
        var result = await Client(handler).ReportAsync(NodeUp(), Base);
        result.Outcome.Should().Be(expected);
        result.ShouldRetry.Should().Be(retryable);
        result.StatusCode.Should().Be((int)status);
    }

    [Fact]
    public async Task A_400_captures_the_server_error_detail_and_is_not_retryable()
    {
        var handler = new CapturingHandler(HttpStatusCode.BadRequest,
            "{\"errors\":{\"Locator\":[\"must be a valid Maidenhead locator\"]}}");
        var result = await Client(handler).ReportAsync(NodeUp(), Base);

        result.Outcome.Should().Be(OarcIngestOutcome.Rejected);
        result.ShouldRetry.Should().BeFalse("a malformed payload never succeeds on retry");
        result.Detail.Should().Contain("Maidenhead");
    }

    [Fact]
    public async Task A_transport_exception_becomes_a_retryable_result_and_never_throws()
    {
        var handler = new CapturingHandler(throwThis: new HttpRequestException("connection refused"));
        var result = await Client(handler).ReportAsync(NodeUp(), Base);

        result.Outcome.Should().Be(OarcIngestOutcome.TransportError);
        result.ShouldRetry.Should().BeTrue();
        result.Detail.Should().Contain("connection refused");
    }

    [Fact]
    public async Task A_genuine_shutdown_cancellation_propagates()
    {
        // A cancelled token (node stopping) must NOT be swallowed as a transport error — it ends the loop.
        var handler = new CapturingHandler(throwThis: new OperationCanceledException());
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        var act = async () => await Client(handler).ReportAsync(NodeUp(), Base, cts.Token);
        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task A_request_timeout_is_a_retryable_transport_error_not_a_shutdown()
    {
        // A per-request timeout surfaces as TaskCanceledException WITHOUT our token being cancelled —
        // it must classify as a (retryable) transport error, not propagate as shutdown.
        var handler = new CapturingHandler(throwThis: new TaskCanceledException("timed out"));
        var result = await Client(handler).ReportAsync(NodeUp(), Base, CancellationToken.None);
        result.Outcome.Should().Be(OarcIngestOutcome.TransportError);
        result.ShouldRetry.Should().BeTrue();
    }

    [Fact]
    public async Task A_bad_base_url_is_a_non_retryable_rejection_not_a_throw()
    {
        var handler = new CapturingHandler();
        var result = await Client(handler).ReportAsync(NodeUp(), "not-a-url");
        result.Outcome.Should().Be(OarcIngestOutcome.Rejected);
        result.ShouldRetry.Should().BeFalse();
        handler.Calls.Should().Be(0, "we never dispatch with an unusable base URL");
    }
}
