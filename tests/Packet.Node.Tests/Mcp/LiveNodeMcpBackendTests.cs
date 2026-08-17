using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Packet.Ax25;
using Packet.Ax25.Transport;
using Packet.Core;
using Packet.Mcp;
using Packet.Node.Core.Audit;
using Packet.Node.Core.Configuration;
using Packet.Node.Core.Hosting;
using Packet.Node.Mcp;
using Packet.Node.Tests.Support;

namespace Packet.Node.Tests.Mcp;

/// <summary>
/// The in-process MCP backend's <b>write</b> paths (RM-6), over a real
/// <see cref="NodeHostedService"/> with a fake transport - so each tool is pinned by
/// what it did to the live node (a frame on the air, a rebuilt port, a KISS parameter
/// on the modem) AND by the audit row it left behind. Auditing is at invocation, not
/// outcome: a refused write is recorded too, which is the point of an audit trail.
/// </summary>
[Trait("Category", "Node")]
public sealed class LiveNodeMcpBackendTests
{
    private static readonly Callsign NodeCall = new("NODE", 1);
    private static readonly Callsign RemoteCall = new("REMOTE", 1);
    private static readonly DateTimeOffset Now = new(2026, 8, 17, 9, 30, 0, TimeSpan.Zero);

    private static readonly McpCaller SseCaller = new(
        "tom", "mcp:sse", new HashSet<string>(StringComparer.Ordinal) { McpScopes.Read, McpScopes.Operate },
        "192.0.2.17");

    private static PortConfig Port(string id, int memPort) => new()
    {
        Id = id,
        Enabled = true,
        Transport = new KissTcpTransport { Host = "mem", Port = memPort },
        Ax25 = new Ax25PortParams { N2 = TestAx25Timing.NodeN2 },
    };

    private static string Endpoint(int memPort) => $"kiss-tcp:mem:{memPort}";

    private static NodeConfig Config(params PortConfig[] ports) => new()
    {
        Identity = new Identity { Callsign = NodeCall.ToString() },
        // No telnet listener: this suite is about the MCP write paths, and a bind would
        // only add a socket to nothing.
        Management = new ManagementConfig { Telnet = new TelnetConfig { Enabled = false } },
        Ports = ports,
    };

    [Fact]
    public async Task Send_ui_frame_puts_the_frame_on_the_air_and_audits_who_asked_for_it()
    {
        var bus = new SharedRadioBus();
        var config = new TestConfigProvider(Config(Port("a", 1)));
        var transports = new FakeTransportFactory().Provide(Endpoint(1), bus.Attach());
        var audit = new RecordingAuditLog();
        var clock = new FakeTimeProvider(Now);

        using var host = new NodeHostedService(config, transports, TimeProvider.System, NullLoggerFactory.Instance);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await host.StartAsync(cts.Token);
        await Wait.ForAsync(() => host.Supervisor?.RunningPortIds.Contains("a") == true, "port a up");

        await using var sniffer = new BusSniffer(bus.Attach());
        var backend = new LiveNodeMcpBackend(host, config, clock, audit);

        var result = await backend.SendUiFrameAsync(
            new SendUiRequest("a", RemoteCall.ToString(), "CQ CQ"), SseCaller, cts.Token);

        result.Accepted.Should().BeTrue(result.Message);
        await Wait.ForAsync(() => sniffer.Frames.Length > 0, "the UI frame reached the shared channel");
        var frame = sniffer.Frames[0];
        frame.Destination.Callsign.Should().Be(RemoteCall);
        frame.Source.Callsign.Should().Be(NodeCall, "the node transmits under the port's own call");
        Encoding.UTF8.GetString(frame.Info.Span).Should().Be("CQ CQ");

        var entry = audit.Entries.Should().ContainSingle().Subject;
        entry.Action.Should().Be("send_ui_frame");
        entry.Actor.Should().Be("tom");
        entry.Source.Should().Be("mcp:sse", "the SSE transport names itself in the audit trail");
        entry.Target.Should().Be("a");
        entry.Detail.Should().Be($"dest={RemoteCall} len=5");
        entry.ClientIp.Should().Be("192.0.2.17");
        entry.TimestampUtc.Should().Be(Now, "the row is stamped from the injected clock");

        await host.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Send_ui_frame_on_a_port_that_is_not_up_is_refused_and_still_audited()
    {
        var bus = new SharedRadioBus();
        var config = new TestConfigProvider(Config(Port("a", 1)));
        var transports = new FakeTransportFactory().Provide(Endpoint(1), bus.Attach());
        var audit = new RecordingAuditLog();

        using var host = new NodeHostedService(config, transports, TimeProvider.System, NullLoggerFactory.Instance);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await host.StartAsync(cts.Token);
        await Wait.ForAsync(() => host.Supervisor?.RunningPortIds.Contains("a") == true, "port a up");

        var backend = new LiveNodeMcpBackend(host, config, new FakeTimeProvider(Now), audit);

        var result = await backend.SendUiFrameAsync(
            new SendUiRequest("nope", RemoteCall.ToString(), "hi"), McpCaller.LocalStdio, cts.Token);

        result.Accepted.Should().BeFalse();
        result.Message.Should().Contain("'nope' is not up");
        var entry = audit.Entries.Should().ContainSingle().Subject;
        entry.Action.Should().Be("send_ui_frame");
        entry.Outcome.Should().Be("requested",
            "the row records the privileged INVOCATION - a refusal is exactly what an audit trail is for");
        entry.Actor.Should().Be("local-stdio");
        entry.Source.Should().Be("mcp:stdio", "the stdio bridge's transport name is normalised for the trail");
        entry.ClientIp.Should().BeNull("a local process has no client ip");

        await host.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Reset_port_rebuilds_the_port_under_the_hosts_exclusive_gate_and_audits_it()
    {
        var bus = new SharedRadioBus();
        var config = new TestConfigProvider(Config(Port("a", 1)));
        // Two transports for the one endpoint: the restart re-opens it.
        var transports = new FakeTransportFactory().Provide(Endpoint(1), bus.Attach(), bus.Attach());
        var audit = new RecordingAuditLog();

        using var host = new NodeHostedService(config, transports, TimeProvider.System, NullLoggerFactory.Instance);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await host.StartAsync(cts.Token);
        await Wait.ForAsync(() => host.Supervisor?.RunningPortIds.Contains("a") == true, "port a up");
        var listenerBefore = host.Supervisor!.GetPort("a")!.Listener;

        var backend = new LiveNodeMcpBackend(host, config, new FakeTimeProvider(Now), audit);
        var result = await backend.ResetPortAsync("a", SseCaller, cts.Token);

        result.Accepted.Should().BeTrue(result.Message);
        result.PortId.Should().Be("a");
        var rebuilt = host.Supervisor!.GetPort("a")!;
        rebuilt.Listener.Should().NotBeSameAs(listenerBefore, "a reset tears the port down and brings it back");
        rebuilt.Listener.MyCall.Should().Be(NodeCall);
        listenerBefore.IsRunning.Should().BeFalse("the old listener was disposed with the old port");

        // The rebuilt port carries traffic again.
        await using var remote = new RemoteStation(bus.Attach(), RemoteCall);
        await remote.StartAsync();
        await remote.ConnectAsync(NodeCall);
        await Wait.ForAsync(() => remote.Saw("Welcome"), "the reset port answers");

        var entry = audit.Entries.Should().ContainSingle().Subject;
        entry.Action.Should().Be("reset_port");
        entry.Target.Should().Be("a");
        entry.Detail.Should().Be("restart");

        await host.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Set_kiss_param_reaches_the_live_modem_and_audits_the_value()
    {
        // An in-memory endpoint records what was pushed to it, so "did the write reach the
        // modem" is an assertion rather than an inference.
        var (nodeModem, _) = InMemoryRadio.CreatePair();
        var config = new TestConfigProvider(Config(Port("a", 1)));
        var transports = new FakeTransportFactory().Provide(Endpoint(1), nodeModem);
        var audit = new RecordingAuditLog();

        using var host = new NodeHostedService(config, transports, TimeProvider.System, NullLoggerFactory.Instance);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await host.StartAsync(cts.Token);
        await Wait.ForAsync(() => host.Supervisor?.RunningPortIds.Contains("a") == true, "port a up");

        var backend = new LiveNodeMcpBackend(host, config, new FakeTimeProvider(Now), audit);
        var result = await backend.SetKissParamAsync(new SetKissParamRequest("a", "txdelay", 45), SseCaller, cts.Token);

        result.Accepted.Should().BeTrue(result.Message);
        result.RequiresRestart.Should().BeFalse("the CSMA params apply on the next transmission");
        nodeModem.Applied.TxDelay.Should().Be((byte)45, "the value reached the live modem, not just the config");

        var entry = audit.Entries.Should().ContainSingle().Subject;
        entry.Action.Should().Be("set_kiss_param");
        entry.Target.Should().Be("a");
        entry.Detail.Should().Be("txdelay=45");

        // A rejected value is audited too, and changes nothing on the modem.
        var rejected = await backend.SetKissParamAsync(new SetKissParamRequest("a", "txdelay", 999), SseCaller, cts.Token);
        rejected.Accepted.Should().BeFalse();
        nodeModem.Applied.TxDelay.Should().Be((byte)45, "an out-of-range write must not disturb the live value");
        audit.Entries.Should().HaveCount(2);
        audit.Entries[1].Detail.Should().Be("txdelay=999");

        await host.StopAsync(CancellationToken.None);
    }

    /// <summary>An in-memory <see cref="IAuditLog"/> - the write paths' other half.</summary>
    private sealed class RecordingAuditLog : IAuditLog
    {
        private readonly List<AuditEntry> entries = [];
        private readonly object gate = new();

        public AuditEntry[] Entries { get { lock (gate) { return entries.ToArray(); } } }

        public void Record(AuditEntry entry)
        {
            lock (gate)
            {
                entries.Add(entry);
            }
        }

        public IReadOnlyList<AuditEntry> Recent(int limit) => Entries.TakeLast(limit).ToList();
    }

    /// <summary>
    /// A third station on the shared channel that just parses and keeps what it hears -
    /// the "was it really on the air" witness for the connectionless send path.
    /// </summary>
    private sealed class BusSniffer : IAsyncDisposable
    {
        private readonly CancellationTokenSource lifetime = new();
        private readonly List<Ax25Frame> frames = [];
        private readonly object gate = new();
        private readonly Task pump;

        public BusSniffer(IAx25Transport transport)
        {
            pump = Task.Run(async () =>
            {
                await foreach (var inbound in transport.ReceiveAsync(lifetime.Token).ConfigureAwait(false))
                {
                    if (Ax25Frame.TryParse(inbound.Ax25.Span, out var frame))
                    {
                        lock (gate)
                        {
                            frames.Add(frame);
                        }
                    }
                }
            }, CancellationToken.None);
        }

        public Ax25Frame[] Frames { get { lock (gate) { return frames.ToArray(); } } }

        public async ValueTask DisposeAsync()
        {
            await lifetime.CancelAsync().ConfigureAwait(false);
            try
            {
                await pump.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // The pump is cancelled by design on teardown.
            }
            lifetime.Dispose();
        }
    }
}
