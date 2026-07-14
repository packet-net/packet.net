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

    [Fact]
    public async Task Write_tools_reject_a_caller_without_the_operate_scope()
    {
        var backend = new FakeNodeMcpBackend();
        var readOnly = new FixedCallerAccessor(new McpCaller(
            "viewer", "sse", new HashSet<string>(StringComparer.Ordinal) { McpScopes.Read }));
        var tools = new WriteTools(backend, readOnly);

        var act = async () => await tools.ResetPort("vhf");

        await act.Should().ThrowAsync<UnauthorizedAccessException>();
        backend.LastCaller.Should().BeNull(); // never reached the backend
    }

    [Fact]
    public async Task Operate_scope_is_enough_to_invoke_a_write_tool()
    {
        var backend = new FakeNodeMcpBackend();
        var operate = new FixedCallerAccessor(new McpCaller(
            "op", "sse", new HashSet<string>(StringComparer.Ordinal) { McpScopes.Read, McpScopes.Operate }));
        var tools = new WriteTools(backend, operate);

        var result = await tools.ResetPort("vhf");

        result.Accepted.Should().BeTrue();
        backend.LastCaller!.Actor.Should().Be("op");
    }

    [Fact]
    public async Task Get_rig_status_passes_the_port_filter_to_the_backend()
    {
        var backend = new FakeNodeMcpBackend
        {
            Rigs = [new McpRigStatus(
                "hf", true, "hamlib", "127.0.0.1:4532", "Hamlib rigctld", "Yaesu", "FT-991A",
                ["frequencyGet", "frequencySet"], "healthy", 14_074_000, "PKTUSB", 3000,
                false, null, null, null, DateTimeOffset.UnixEpoch)],
        };
        var tools = new ReadTools(backend);

        var rigs = await tools.GetRigStatus("hf");

        backend.LastRigStatusPort.Should().Be("hf");
        rigs.Should().ContainSingle().Which.FrequencyHz.Should().Be(14_074_000);
    }

    [Fact]
    public async Task Get_rig_status_defaults_to_all_rig_configured_ports()
    {
        var backend = new FakeNodeMcpBackend();
        var tools = new ReadTools(backend);

        await tools.GetRigStatus();

        backend.LastRigStatusPort.Should().BeNull();
    }

    [Fact]
    public async Task Set_rig_frequency_attributes_the_caller_and_round_trips_the_request()
    {
        var backend = new FakeNodeMcpBackend();
        var tools = new WriteTools(backend, new LocalStdioCallerAccessor());

        var result = await tools.SetRigFrequency("hf", 14_074_000);

        result.Accepted.Should().BeTrue();
        result.FrequencyHz.Should().Be(14_074_000);
        backend.LastCaller.Should().Be(McpCaller.LocalStdio);
        backend.LastRigFrequency!.Port.Should().Be("hf");
        backend.LastRigFrequency.FrequencyHz.Should().Be(14_074_000);
    }

    [Fact]
    public async Task Set_rig_mode_round_trips_mode_and_passband()
    {
        var backend = new FakeNodeMcpBackend();
        var tools = new WriteTools(backend, new LocalStdioCallerAccessor());

        var result = await tools.SetRigMode("hf", "PKTUSB", 3000);

        result.Accepted.Should().BeTrue();
        result.Mode.Should().Be("PKTUSB");
        result.PassbandHz.Should().Be(3000);
        backend.LastCaller.Should().Be(McpCaller.LocalStdio);
        backend.LastRigMode!.Port.Should().Be("hf");
        backend.LastRigMode.Mode.Should().Be("PKTUSB");
        backend.LastRigMode.PassbandHz.Should().Be(3000);
    }

    [Fact]
    public async Task Set_rig_mode_defaults_the_passband_to_the_rigs_default_width()
    {
        var backend = new FakeNodeMcpBackend();
        var tools = new WriteTools(backend, new LocalStdioCallerAccessor());

        await tools.SetRigMode("hf", "USB");

        backend.LastRigMode!.PassbandHz.Should().BeNull();
    }

    [Fact]
    public async Task Rig_write_tools_reject_a_caller_without_the_operate_scope()
    {
        var backend = new FakeNodeMcpBackend();
        var readOnly = new FixedCallerAccessor(new McpCaller(
            "viewer", "sse", new HashSet<string>(StringComparer.Ordinal) { McpScopes.Read }));
        var tools = new WriteTools(backend, readOnly);

        var qsy = async () => await tools.SetRigFrequency("hf", 14_074_000);
        var mode = async () => await tools.SetRigMode("hf", "PKTUSB");

        await qsy.Should().ThrowAsync<UnauthorizedAccessException>();
        await mode.Should().ThrowAsync<UnauthorizedAccessException>();
        backend.LastCaller.Should().BeNull(); // never reached the backend
        backend.LastRigFrequency.Should().BeNull();
        backend.LastRigMode.Should().BeNull();
    }

    [Fact]
    public async Task Operate_scope_is_enough_for_the_rig_write_tools()
    {
        var backend = new FakeNodeMcpBackend();
        var operate = new FixedCallerAccessor(new McpCaller(
            "op", "sse", new HashSet<string>(StringComparer.Ordinal) { McpScopes.Read, McpScopes.Operate }));
        var tools = new WriteTools(backend, operate);

        var qsy = await tools.SetRigFrequency("hf", 7_040_000);
        var mode = await tools.SetRigMode("hf", "PKTLSB");

        qsy.Accepted.Should().BeTrue();
        mode.Accepted.Should().BeTrue();
        backend.LastCaller!.Actor.Should().Be("op");
    }

    private sealed class FixedCallerAccessor(McpCaller caller) : IMcpCallerAccessor
    {
        public McpCaller Current { get; } = caller;
    }

    private sealed class FakeNodeMcpBackend : INodeMcpBackend
    {
        public IReadOnlyList<McpPortStatus> Ports { get; init; } = [];
        public IReadOnlyList<McpRigStatus> Rigs { get; init; } = [];
        public FrameFilter? LastFilter { get; private set; }
        public McpCaller? LastCaller { get; private set; }
        public SendUiRequest? LastSend { get; private set; }
        public SetKissParamRequest? LastKissParam { get; private set; }
        public string? LastRigStatusPort { get; private set; }
        public SetRigFrequencyRequest? LastRigFrequency { get; private set; }
        public SetRigModeRequest? LastRigMode { get; private set; }

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

        public Task<IReadOnlyList<McpRigStatus>> RigStatusAsync(string? portId = null, CancellationToken ct = default)
        {
            LastRigStatusPort = portId;
            return Task.FromResult(Rigs);
        }

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

        public Task<RigFrequencyResult> SetRigFrequencyAsync(SetRigFrequencyRequest req, McpCaller caller, CancellationToken ct = default)
        {
            LastRigFrequency = req;
            LastCaller = caller;
            return Task.FromResult(new RigFrequencyResult(true, req.Port, req.FrequencyHz, "tuned"));
        }

        public Task<RigModeResult> SetRigModeAsync(SetRigModeRequest req, McpCaller caller, CancellationToken ct = default)
        {
            LastRigMode = req;
            LastCaller = caller;
            return Task.FromResult(new RigModeResult(true, req.Port, req.Mode, req.PassbandHz, "set"));
        }
    }
}
