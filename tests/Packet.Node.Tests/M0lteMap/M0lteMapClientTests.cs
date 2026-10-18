using System.Net;
using Packet.Node.Core.M0lteMap;

namespace Packet.Node.Tests.M0lteMap;

/// <summary>
/// The UK packet map HTTP client (#453): where a report goes, what headers it carries, and how
/// each kind of answer (or no answer) is classified. Never touches the real collector.
/// </summary>
public sealed class M0lteMapClientTests
{
    private sealed class ScriptedHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public HttpRequestMessage? Request { get; private set; }
        public string? Body { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();   // as a real socket handler does
            Request = request;
            Body = request.Content is null ? null : await request.Content.ReadAsStringAsync(ct);
            return respond(request);
        }
    }

    private static readonly Uri Fake = new("https://collector.invalid/");

    private static M0lteMapNodeData Report() => new()
    {
        NodeAlias = "READNG",
        Location = new M0lteMapLocation { Locator = "IO91lk" },
        Software = new M0lteMapSoftware { Name = "pdn", Version = "0.68.0" },
        Ports = [],
    };

    private static (M0lteMapClient Client, ScriptedHandler Handler) Make(HttpStatusCode status, string body = "")
    {
        var handler = new ScriptedHandler(_ => new HttpResponseMessage(status) { Content = new StringContent(body) });
        return (new M0lteMapClient(new HttpClient(handler), "0.68.0", Fake), handler);
    }

    [Fact]
    public void The_default_collector_is_the_one_LinBpq_reports_to()
    {
        new M0lteMapClient(new HttpClient(), "0.68.0").ReportUri("GB7RDG")
            .Should().Be(new Uri("https://packetnodes.spots.radio/api/NodeData/GB7RDG"));
    }

    [Fact]
    public async Task Posts_the_report_as_json_to_NodeData_for_the_node_call_with_a_pdn_user_agent()
    {
        var (client, handler) = Make(HttpStatusCode.OK);

        var result = await client.PostAsync("GB7RDG-1", Report());

        result.Outcome.Should().Be(M0lteMapPostOutcome.Accepted);
        handler.Request!.Method.Should().Be(HttpMethod.Post);
        handler.Request.RequestUri.Should().Be(new Uri("https://collector.invalid/api/NodeData/GB7RDG-1"));
        handler.Request.Content!.Headers.ContentType!.MediaType.Should().Be("application/json");
        handler.Request.Headers.UserAgent.ToString().Should().Be("pdn/0.68.0");
        handler.Body.Should().Be(M0lteMapClient.Serialize(Report()));
    }

    [Theory]
    [InlineData(HttpStatusCode.OK, M0lteMapPostOutcome.Accepted)]
    [InlineData(HttpStatusCode.Accepted, M0lteMapPostOutcome.Accepted)]
    [InlineData(HttpStatusCode.NoContent, M0lteMapPostOutcome.Accepted)]
    [InlineData(HttpStatusCode.BadRequest, M0lteMapPostOutcome.Rejected)]
    [InlineData(HttpStatusCode.NotFound, M0lteMapPostOutcome.Rejected)]
    [InlineData(HttpStatusCode.UnprocessableEntity, M0lteMapPostOutcome.Rejected)]
    [InlineData(HttpStatusCode.TooManyRequests, M0lteMapPostOutcome.TransportError)]
    [InlineData(HttpStatusCode.InternalServerError, M0lteMapPostOutcome.TransportError)]
    [InlineData(HttpStatusCode.BadGateway, M0lteMapPostOutcome.TransportError)]
    [InlineData(HttpStatusCode.ServiceUnavailable, M0lteMapPostOutcome.TransportError)]
    public async Task Classifies_each_answer(HttpStatusCode status, M0lteMapPostOutcome expected)
    {
        var (client, _) = Make(status, "nope");

        var result = await client.PostAsync("GB7RDG", Report());

        result.Outcome.Should().Be(expected);
        result.StatusCode.Should().Be((int)status);
    }

    [Fact]
    public async Task A_refusal_carries_the_collectors_reason_cut_short()
    {
        var (client, _) = Make(HttpStatusCode.BadRequest, new string('x', 5000));

        var result = await client.PostAsync("GB7RDG", Report());

        result.Detail!.Length.Should().Be(300);
    }

    [Fact]
    public async Task No_route_to_the_collector_is_a_transport_error_not_an_exception()
    {
        var handler = new ScriptedHandler(_ => throw new HttpRequestException("Name or service not known"));
        var client = new M0lteMapClient(new HttpClient(handler), "0.68.0", Fake);

        var result = await client.PostAsync("GB7RDG", Report());

        result.Outcome.Should().Be(M0lteMapPostOutcome.TransportError);
        result.StatusCode.Should().BeNull();
        result.Detail.Should().Contain("Name or service not known");
    }

    [Fact]
    public async Task A_timeout_is_a_transport_error()
    {
        var handler = new ScriptedHandler(_ => throw new TaskCanceledException("timed out"));
        var client = new M0lteMapClient(new HttpClient(handler), "0.68.0", Fake);

        (await client.PostAsync("GB7RDG", Report())).Outcome.Should().Be(M0lteMapPostOutcome.TransportError);
    }

    [Fact]
    public async Task Shutdown_cancellation_propagates()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        var (client, _) = Make(HttpStatusCode.OK);

        var act = () => client.PostAsync("GB7RDG", Report(), cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Theory]
    [InlineData("0.68.0+abc123", "pdn/0.68.0+abc123")]
    [InlineData("1.0 (dev)", "pdn/1.0dev")]
    [InlineData("", "pdn/dev")]
    public async Task The_user_agent_survives_an_odd_version_string(string version, string expected)
    {
        var handler = new ScriptedHandler(_ => new HttpResponseMessage(HttpStatusCode.OK));
        var client = new M0lteMapClient(new HttpClient(handler), version, Fake);

        await client.PostAsync("GB7RDG", Report());

        handler.Request!.Headers.UserAgent.ToString().Should().Be(expected);
    }
}
