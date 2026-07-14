using System.Net;
using System.Text;
using Packet.Mcp;
using Packet.Node.Mcp;

namespace Packet.Node.Tests.Mcp;

public class RestNodeMcpBackendTests
{
    // A stub handler that answers a fixed map of "VERB path" → (status, json body),
    // and records the requests it saw.
    private sealed class StubHandler(Dictionary<string, (HttpStatusCode, string)> responses)
        : HttpMessageHandler
    {
        public List<string> Seen { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            string key = $"{request.Method.Method} {request.RequestUri!.PathAndQuery}";
            Seen.Add(key);
            var (status, body) = responses.TryGetValue(key, out var r) ? r : (HttpStatusCode.NotFound, "");
            return Task.FromResult(new HttpResponseMessage(status)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            });
        }
    }

    private static RestNodeMcpBackend Backend(StubHandler handler)
        => new(new HttpClient(handler) { BaseAddress = new Uri("http://127.0.0.1:8080") });

    [Fact]
    public async Task List_ports_maps_the_rest_json()
    {
        var handler = new StubHandler(new()
        {
            ["GET /api/v1/ports"] = (HttpStatusCode.OK,
                """[{"id":"vhf","enabled":true,"state":"up","sessionCount":2,"lastError":null,"framesIn":10,"framesOut":7}]"""),
        });

        var ports = await Backend(handler).ListPortsAsync();

        var p = ports.Should().ContainSingle().Subject;
        p.Id.Should().Be("vhf");
        p.State.Should().Be("up");
        p.SessionCount.Should().Be(2);
        p.FramesIn.Should().Be(10);
        p.FramesOut.Should().Be(7);
    }

    [Fact]
    public async Task Recent_frames_filters_client_side_by_kind()
    {
        var handler = new StubHandler(new()
        {
            ["GET /api/v1/monitor/recent?limit=250"] = (HttpStatusCode.OK,
                """
                [{"seq":1,"timestamp":"2026-06-13T00:00:00Z","portId":"vhf","direction":"in","source":"A","dest":"B","type":"UI","length":20},
                 {"seq":2,"timestamp":"2026-06-13T00:00:01Z","portId":"vhf","direction":"out","source":"B","dest":"A","type":"RR","length":15}]
                """),
        });

        var frames = await Backend(handler).RecentFramesAsync(new FrameFilter(Kind: "UI"));

        frames.Should().ContainSingle().Which.Kind.Should().Be("UI");
    }

    [Fact]
    public async Task Reset_port_maps_status_codes()
    {
        var handler = new StubHandler(new()
        {
            ["POST /api/v1/ports/vhf/lifecycle"] = (HttpStatusCode.OK, ""),
            ["POST /api/v1/ports/ghost/lifecycle"] = (HttpStatusCode.NotFound, ""),
        });
        var backend = Backend(handler);

        (await backend.ResetPortAsync("vhf", McpCaller.LocalStdio)).Accepted.Should().BeTrue();
        (await backend.ResetPortAsync("ghost", McpCaller.LocalStdio)).Accepted.Should().BeFalse();
    }

    [Fact]
    public async Task Disconnect_maps_204_and_404()
    {
        var handler = new StubHandler(new()
        {
            ["DELETE /api/v1/sessions/vhf%3AM0LTE"] = (HttpStatusCode.NoContent, ""),
        });
        var backend = Backend(handler);

        (await backend.DisconnectSessionAsync("vhf:M0LTE", McpCaller.LocalStdio)).Accepted.Should().BeTrue();
        (await backend.DisconnectSessionAsync("vhf:NOBODY", McpCaller.LocalStdio)).Accepted.Should().BeFalse();
    }

    [Fact]
    public async Task Rig_status_maps_the_rest_json_and_flattens_the_meters()
    {
        var handler = new StubHandler(new()
        {
            ["GET /api/v1/rigs"] = (HttpStatusCode.OK,
                """
                [{"portId":"hf","attached":true,"kind":"hamlib","endpoint":"127.0.0.1:4532",
                  "backend":"Hamlib rigctld","manufacturer":"Yaesu","model":"FT-991A",
                  "capabilities":["frequencyGet","frequencySet","swrMeter"],"connectionState":"healthy",
                  "frequencyHz":14074000,"mode":"PKTUSB","passbandHz":3000,"transmitting":true,
                  "meters":{"swr":1.2,"rfPowerWatts":42.5,"rfPowerRelative":0.42,"sampleAt":"2026-06-13T00:00:00Z"},
                  "sampledAt":"2026-06-13T00:00:00Z"}]
                """),
        });

        var rigs = await Backend(handler).RigStatusAsync();

        var rig = rigs.Should().ContainSingle().Subject;
        rig.PortId.Should().Be("hf");
        rig.Attached.Should().BeTrue();
        rig.Capabilities.Should().Contain("swrMeter");
        rig.FrequencyHz.Should().Be(14_074_000);
        rig.Mode.Should().Be("PKTUSB");
        rig.Swr.Should().Be(1.2);
        rig.RfPowerWatts.Should().Be(42.5);
        rig.RfPowerRelative.Should().Be(0.42);
    }

    [Fact]
    public async Task Rig_status_for_one_port_maps_404_to_the_empty_result()
    {
        var handler = new StubHandler(new()
        {
            ["GET /api/v1/ports/hf/rig"] = (HttpStatusCode.OK,
                """{"portId":"hf","attached":false,"kind":"hamlib","endpoint":"127.0.0.1:4532","capabilities":[],"connectionState":"unknown"}"""),
        });
        var backend = Backend(handler);

        (await backend.RigStatusAsync("hf")).Should().ContainSingle()
            .Which.Attached.Should().BeFalse();
        (await backend.RigStatusAsync("ghost")).Should().BeEmpty();
    }

    [Fact]
    public async Task Set_rig_frequency_returns_the_read_back_and_maps_the_error_body()
    {
        var handler = new StubHandler(new()
        {
            ["POST /api/v1/ports/hf/rig/frequency"] = (HttpStatusCode.OK, """{"frequencyHz":14074010}"""),
            ["POST /api/v1/ports/vhf/rig/frequency"] = (HttpStatusCode.Conflict,
                """{"error":"port 'vhf' has no rig: block configured."}"""),
        });
        var backend = Backend(handler);

        var ok = await backend.SetRigFrequencyAsync(new SetRigFrequencyRequest("hf", 14_074_000), McpCaller.LocalStdio);
        ok.Accepted.Should().BeTrue();
        ok.FrequencyHz.Should().Be(14_074_010, "the node reads the dial back after the set");

        var conflict = await backend.SetRigFrequencyAsync(new SetRigFrequencyRequest("vhf", 14_074_000), McpCaller.LocalStdio);
        conflict.Accepted.Should().BeFalse();
        conflict.FrequencyHz.Should().BeNull();
        conflict.Message.Should().Be("port 'vhf' has no rig: block configured.");

        var missing = await backend.SetRigFrequencyAsync(new SetRigFrequencyRequest("ghost", 14_074_000), McpCaller.LocalStdio);
        missing.Accepted.Should().BeFalse();
        missing.Message.Should().Be("no such port 'ghost'.");
    }

    [Fact]
    public async Task Set_rig_mode_returns_the_read_back_and_maps_the_error_body()
    {
        var handler = new StubHandler(new()
        {
            ["POST /api/v1/ports/hf/rig/mode"] = (HttpStatusCode.OK, """{"mode":"PKTUSB","passbandHz":3000}"""),
            ["POST /api/v1/ports/vhf/rig/mode"] = (HttpStatusCode.BadRequest,
                """{"error":"mode is required (a hamlib token like USB/PKTUSB, or the rig's native name)."}"""),
        });
        var backend = Backend(handler);

        var ok = await backend.SetRigModeAsync(new SetRigModeRequest("hf", "PKTUSB"), McpCaller.LocalStdio);
        ok.Accepted.Should().BeTrue();
        ok.Mode.Should().Be("PKTUSB");
        ok.PassbandHz.Should().Be(3000);

        var bad = await backend.SetRigModeAsync(new SetRigModeRequest("vhf", " "), McpCaller.LocalStdio);
        bad.Accepted.Should().BeFalse();
        bad.Message.Should().StartWith("mode is required");
    }

    [Fact]
    public async Task Send_ui_frame_and_set_kiss_param_are_sse_only_over_the_bridge()
    {
        var backend = Backend(new StubHandler(new()));

        var send = await backend.SendUiFrameAsync(new SendUiRequest("vhf", "APRS", "hi"), McpCaller.LocalStdio);
        var kiss = await backend.SetKissParamAsync(new SetKissParamRequest("vhf", "txdelay", 40), McpCaller.LocalStdio);

        send.Accepted.Should().BeFalse();
        kiss.Accepted.Should().BeFalse();
    }
}
