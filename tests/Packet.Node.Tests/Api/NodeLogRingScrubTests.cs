using Packet.Node.Api;
using Packet.Node.Core.Api;

namespace Packet.Node.Tests.Api;

/// <summary>
/// <c>GET /api/v1/log</c> must not hand a read-scoped caller a bearer token (#727 item 9).
/// </summary>
/// <remarks>
/// The ring tees every MEL line the configured filters admit. The SSE feeds carry their JWT as
/// a <c>?access_token=</c> query parameter (an <c>EventSource</c> cannot set headers), and
/// ASP.NET's request-start/finish lines print the full query string. <c>appsettings.json</c>
/// pins <c>Microsoft.AspNetCore</c> to <c>Warning</c>, so this is closed by default - but
/// <c>PUT /api/v1/system/loglevel</c> (admin) raises that category live, and from then on a
/// read-scoped account could read an admin bearer straight out of the log tail.
/// </remarks>
[Trait("Category", "Node")]
public sealed class NodeLogRingScrubTests
{
    private const string Jwt =
        "eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9.eyJzdWIiOiJtMGx0ZSIsInNjb3BlIjoiYWRtaW4ifQ.c2lnbmF0dXJl";

    [Fact]
    public void An_sse_request_line_loses_its_query_token_before_it_reaches_the_ring()
    {
        var ring = new NodeLogRing();

        ring.Add(new LogLine("12:00:00", "info",
            $"Request starting GET http://pdn.lan:8080/api/v1/monitor/events?access_token={Jwt} - -"));

        var line = ring.Recent(1).Should().ContainSingle().Subject;
        line.Msg.Should().NotContain(Jwt);
        line.Msg.Should().Contain("access_token=***");
        line.Msg.Should().Contain("/api/v1/monitor/events", "only the credential is removed, not the diagnostic");
    }

    [Fact]
    public void A_token_in_the_middle_of_a_query_string_loses_only_its_own_value()
    {
        var ring = new NodeLogRing();

        ring.Add(new LogLine("12:00:00", "info",
            $"Request finished GET /api/v1/log?access_token={Jwt}&limit=50 - 200"));

        var msg = ring.Recent(1).Single().Msg;
        msg.Should().NotContain(Jwt);
        msg.Should().Contain("access_token=***&limit=50", "the scrub stops at the parameter separator");
    }

    [Fact]
    public void A_logged_authorization_header_loses_its_bearer_value()
    {
        var ring = new NodeLogRing();

        ring.Add(new LogLine("12:00:00", "info", $"upstream call with Authorization: Bearer {Jwt}"));

        var msg = ring.Recent(1).Single().Msg;
        msg.Should().NotContain(Jwt);
        msg.Should().Contain("Bearer ***");
    }

    [Fact]
    public void The_panels_cookie_credential_is_scrubbed_too()
    {
        var ring = new NodeLogRing();

        ring.Add(new LogLine("12:00:00", "info", $"Cookie: pdn_at={Jwt}; theme=dark"));

        var msg = ring.Recent(1).Single().Msg;
        msg.Should().NotContain(Jwt);
        msg.Should().Contain("pdn_at=***");
        msg.Should().Contain("theme=dark");
    }

    [Fact]
    public void An_ordinary_line_is_stored_unchanged()
    {
        var ring = new NodeLogRing();
        const string ordinary = "PortSupervisor: port 'vhf' is up (kiss-tcp 127.0.0.1:8101)";

        ring.Add(new LogLine("12:00:00", "info", ordinary));

        ring.Recent(1).Single().Msg.Should().Be(ordinary);
    }
}
