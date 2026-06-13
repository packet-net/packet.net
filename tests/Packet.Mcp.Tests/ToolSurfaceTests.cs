using Packet.Mcp;
using Packet.Mcp.Tools;

namespace Packet.Mcp.Tests;

public class ToolSurfaceTests
{
    [Fact]
    public async Task Read_tools_pass_through_to_the_backend()
    {
        var backend = new FakeNodeMcpBackend
        {
            Ports = [new McpPortStatus("vhf", true, "up", 2, 10, 7)],
        };
        var tools = new ReadTools(backend);

        var ports = await tools.ListPorts();

        ports.Should().ContainSingle().Which.Id.Should().Be("vhf");
    }

    [Fact]
    public async Task Recent_frames_forwards_the_filter()
    {
        var backend = new FakeNodeMcpBackend();
        var tools = new ReadTools(backend);

        await tools.RecentFrames(port: "vhf", peer: "M0LTE", kind: "UI", sinceSeconds: 30, limit: 50);

        backend.LastFilter.Should().NotBeNull();
        backend.LastFilter!.Port.Should().Be("vhf");
        backend.LastFilter.Peer.Should().Be("M0LTE");
        backend.LastFilter.Kind.Should().Be("UI");
        backend.LastFilter.SinceSeconds.Should().Be(30);
        backend.LastFilter.Limit.Should().Be(50);
    }

    [Fact]
    public async Task Write_tools_attribute_the_caller_from_the_accessor()
    {
        var backend = new FakeNodeMcpBackend();
        var accessor = new LocalStdioCallerAccessor();
        var tools = new WriteTools(backend, accessor);

        await tools.SendUiFrame("vhf", "APRS", "hi");

        backend.LastCaller.Should().Be(McpCaller.LocalStdio);
        backend.LastSend!.Dest.Should().Be("APRS");
        backend.LastSend.Payload.Should().Be("hi");
    }

    [Fact]
    public async Task Set_kiss_param_round_trips_the_request()
    {
        var backend = new FakeNodeMcpBackend();
        var tools = new WriteTools(backend, new LocalStdioCallerAccessor());

        var result = await tools.SetKissParam("vhf", "txdelay", 40);

        result.Accepted.Should().BeTrue();
        backend.LastKissParam!.Param.Should().Be("txdelay");
        backend.LastKissParam.Value.Should().Be(40);
    }

    private sealed class FakeNodeMcpBackend : INodeMcpBackend
    {
        public IReadOnlyList<McpPortStatus> Ports { get; init; } = [];
        public FrameFilter? LastFilter { get; private set; }
        public McpCaller? LastCaller { get; private set; }
        public SendUiRequest? LastSend { get; private set; }
        public SetKissParamRequest? LastKissParam { get; private set; }

        public Task<IReadOnlyList<McpPortStatus>> ListPortsAsync(CancellationToken ct = default)
            => Task.FromResult(Ports);

        public Task<IReadOnlyList<McpSessionInfo>> ListSessionsAsync(CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<McpSessionInfo>>([]);

        public Task<IReadOnlyList<McpMonitorFrame>> RecentFramesAsync(FrameFilter filter, CancellationToken ct = default)
        {
            LastFilter = filter;
            return Task.FromResult<IReadOnlyList<McpMonitorFrame>>([]);
        }

        public Task<McpLinkQuality> LinkQualityAsync(string remote, string? portId = null, CancellationToken ct = default)
            => Task.FromResult(new McpLinkQuality(portId ?? "?", remote, 0, 0, 0, 0, 0, 0, Unknown: true));

        public Task<McpNetworkTopology> NetworkTopologyAsync(CancellationToken ct = default)
            => Task.FromResult(new McpNetworkTopology(DateTimeOffset.UnixEpoch, [], []));

        public Task<SendResult> SendUiFrameAsync(SendUiRequest req, McpCaller caller, CancellationToken ct = default)
        {
            LastSend = req;
            LastCaller = caller;
            return Task.FromResult(new SendResult(true, "queued"));
        }

        public Task<PortActionResult> ResetPortAsync(string portId, McpCaller caller, CancellationToken ct = default)
        {
            LastCaller = caller;
            return Task.FromResult(new PortActionResult(true, portId, "restarting"));
        }

        public Task<SessionResult> DisconnectSessionAsync(string sessionId, McpCaller caller, CancellationToken ct = default)
        {
            LastCaller = caller;
            return Task.FromResult(new SessionResult(true, sessionId, "disconnecting"));
        }

        public Task<KissParamResult> SetKissParamAsync(SetKissParamRequest req, McpCaller caller, CancellationToken ct = default)
        {
            LastKissParam = req;
            LastCaller = caller;
            return Task.FromResult(new KissParamResult(true, RequiresRestart: false, "applied"));
        }
    }
}
