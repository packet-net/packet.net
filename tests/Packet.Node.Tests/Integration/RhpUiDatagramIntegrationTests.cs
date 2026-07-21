using Microsoft.Extensions.Logging.Abstractions;
using Packet.Ax25;
using Packet.Ax25.Session;
using Packet.Ax25.Transport;
using Packet.Core;
using Packet.Node.Core.Configuration;
using Packet.Node.Core.Hosting;
using Packet.Node.Rhp;
using Packet.Node.Tests.Support;
using Packet.Rhp2.Server;
using Xunit;

namespace Packet.Node.Tests.Integration;

/// <summary>
/// The RHPv2 dgram (UI) path end-to-end at the node-host layer over the in-memory radio
/// (R-6): <see cref="SupervisorRhpGateway.SendUiAsync"/> emits a connectionless UI frame from
/// an explicit source that a station on the channel hears, and
/// <see cref="SupervisorRhpGateway.RegisterUiListener"/> promiscuously taps inbound UI —
/// surfacing the frame's true source / destination / PID / info with the arrival port label.
/// </summary>
[Trait("Category", "Node")]
public sealed class RhpUiDatagramIntegrationTests
{
    private static readonly Callsign NodeCall = new("NODE", 1);
    private static readonly Callsign RemoteCall = new("2E0XYZ", 7);
    private static readonly Callsign AppCall = new("M0LTE", 1);

    private static NodeConfig Config() => new()
    {
        Identity = new Identity { Callsign = NodeCall.ToString(), Alias = "TESTNODE" },
        Ports = [new PortConfig { Id = "p1", Enabled = true, Transport = new KissTcpTransport { Host = "mem", Port = 1 } }],
    };

    private static async Task<(NodeHostedService host, TestConfigProvider config)> StartedHostAsync(SharedRadioBus bus)
    {
        var config = new TestConfigProvider(Config());
        var factory = new FakeTransportFactory().Provide("kiss-tcp:mem:1", bus.Attach());
        var host = new NodeHostedService(config, factory, TimeProvider.System, NullLoggerFactory.Instance);
        await host.StartAsync(CancellationToken.None);
        await Wait.ForAsync(() => host.Supervisor?.RunningPortIds.Contains("p1") == true, "port p1 comes up");
        return (host, config);
    }

    [Fact]
    public async Task SendUiAsync_emits_a_ui_frame_from_the_bound_source_that_the_channel_hears()
    {
        var bus = new SharedRadioBus();
        var (host, config) = await StartedHostAsync(bus);
        using var _ = host;

        // A bare station on the same channel that captures the UI frame the node emits.
        await using var remote = new BareStation(bus.Attach(), RemoteCall);
        await remote.StartAsync();

        var gateway = new SupervisorRhpGateway(host, config);
        var info = "hello"u8.ToArray();
        await gateway.SendUiAsync(portLabel: "p1", local: AppCall.ToString(), remote: "APRS", info, pid: 0xCC);

        var frame = await remote.WaitForUiAsync(TimeSpan.FromSeconds(10));
        Assert.True(frame.IsUi);
        Assert.Equal(AppCall, frame.Source.Callsign);           // the explicit source, not the node callsign
        Assert.Equal(new Callsign("APRS", 0), frame.Destination.Callsign);
        Assert.Equal((byte)0xCC, frame.Pid);
        Assert.Equal(info, frame.Info.ToArray());
    }

    [Fact]
    public async Task RegisterUiListener_taps_inbound_ui_promiscuously_with_source_dest_pid_and_port()
    {
        var bus = new SharedRadioBus();
        var (host, config) = await StartedHostAsync(bus);
        using var _ = host;

        await using var remote = new BareStation(bus.Attach(), RemoteCall);
        await remote.StartAsync();

        var gateway = new SupervisorRhpGateway(host, config);
        var heard = new TaskCompletionSource<UiDatagram>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var subscription = gateway.RegisterUiListener(portLabel: "p1", dg =>
        {
            heard.TrySetResult(dg);
            return Task.CompletedTask;
        });

        // The remote sends a broadcast UI frame (dest APRS — NOT the node) — the tap is promiscuous.
        await remote.SendUiAsync(new Callsign("APRS", 0), "!beacon"u8.ToArray(), pid: 0xF0);

        var dg = await heard.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(RemoteCall.ToString(), dg.Source);   // the frame's true source → recv.remote
        Assert.Equal("APRS", dg.Dest);                    // the frame's destination → recv.local
        Assert.Equal((byte)0xF0, dg.Pid);
        Assert.Equal("p1", dg.PortLabel);                 // the arrival port id
        Assert.Equal("!beacon"u8.ToArray(), dg.Info.ToArray());
    }

    // A bare Ax25Listener endpoint on the bus: sends UI frames and captures received ones, for
    // the opposite end of a UI exchange with the node under test. Received UI frames are buffered
    // in a channel so a caller that waits after the frame arrives still observes it (no race).
    private sealed class BareStation(IAx25Transport transport, Callsign myCall) : IAsyncDisposable
    {
        private readonly Ax25Listener listener = new(transport, new Ax25ListenerOptions { MyCall = myCall });
        private readonly System.Threading.Channels.Channel<Ax25Frame> receivedUi =
            System.Threading.Channels.Channel.CreateUnbounded<Ax25Frame>();

        public async Task StartAsync()
        {
            listener.FrameTraced += OnFrame;
            await listener.StartAsync();
            listener.AcceptIncoming = false;
        }

        public Task SendUiAsync(Callsign dest, ReadOnlyMemory<byte> info, byte pid)
            => listener.SendUiAsync(dest, info, pid);

        public async Task<Ax25Frame> WaitForUiAsync(TimeSpan budget)
        {
            using var cts = new CancellationTokenSource(budget);
            return await receivedUi.Reader.ReadAsync(cts.Token);
        }

        private void OnFrame(object? sender, Ax25FrameEventArgs e)
        {
            if (e.Direction == FrameDirection.Received && e.Frame.IsUi)
            {
                receivedUi.Writer.TryWrite(e.Frame);
            }
        }

        public async ValueTask DisposeAsync()
        {
            listener.FrameTraced -= OnFrame;
            await listener.DisposeAsync();
        }
    }
}
