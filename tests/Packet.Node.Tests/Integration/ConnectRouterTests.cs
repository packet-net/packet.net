using System.Collections.Concurrent;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Packet.Core;
using Packet.Node.Core.Configuration;
using Packet.Node.Core.Console;
using Packet.Node.Core.Hosting;
using Packet.Node.Tests.Support;

namespace Packet.Node.Tests.Integration;

/// <summary>
/// The connect router (<see cref="PortSupervisor.CreateConnectRouter"/>) the node console uses
/// for <c>C[onnect] [port] &lt;call&gt;</c>: a locally-registered app SSID bridges to the app over
/// an in-memory loopback (no RF), an explicit 1-indexed port dials that port directly, a plain
/// <c>C &lt;call&gt;</c> falls back to the session's default connector. Plus the keystone
/// end-to-end: a caller at the node prompt issuing <c>C &lt;app-ssid&gt;</c> lands inside the app
/// — the same place an over-the-air connect straight to that SSID would (NET/ROM out of scope).
/// </summary>
[Trait("Category", "Node")]
public sealed class ConnectRouterTests
{
    private static readonly Callsign NodeCall = new("NODE", 1);
    private static readonly Callsign AppCall = new("DAPPS", 7);
    private static readonly Callsign RemoteCall = new("REMOTE", 1);

    private static NodeConfig Config() => new()
    {
        Identity = new Identity { Callsign = NodeCall.ToString(), Alias = "TESTNODE" },
        Ports =
        [
            new PortConfig { Id = "p1", Enabled = true, Transport = new KissTcpTransport { Host = "mem", Port = 1 } },
        ],
    };

    private static async Task<PortSupervisor> StartedSupervisorAsync(SharedRadioBus bus)
    {
        var provider = new TestConfigProvider(Config());
        var factory = new FakeTransportFactory().Provide("kiss-tcp:mem:1", bus.Attach());
        var supervisor = new PortSupervisor(provider, factory, TimeProvider.System, NullLoggerFactory.Instance);
        await supervisor.StartAsync();
        return supervisor;
    }

    [Fact]
    public async Task A_registered_app_callsign_resolves_to_a_local_crossconnect()
    {
        await using var supervisor = await StartedSupervisorAsync(new SharedRadioBus());
        using var registration = supervisor.RegisterAppCallsign(AppCall, portId: null, (_, _) => Task.CompletedTask);
        var router = supervisor.CreateConnectRouter(defaultConnector: null);
        var inbound = new DriveableConnection(RemoteCall.ToString(), NodeTransportKind.Ax25);

        var resolution = router.Resolve(port: null, AppCall, inbound);

        Assert.False(resolution.Failed);
        Assert.True(resolution.IsLocalApp);
        Assert.NotNull(resolution.Connector);
    }

    [Fact]
    public async Task An_explicit_port_resolves_to_a_direct_dial_on_that_port()
    {
        await using var supervisor = await StartedSupervisorAsync(new SharedRadioBus());
        var router = supervisor.CreateConnectRouter(defaultConnector: null);
        var inbound = new DriveableConnection(RemoteCall.ToString(), NodeTransportKind.Ax25);

        var resolution = router.Resolve(port: 1, RemoteCall, inbound);

        Assert.False(resolution.Failed);
        Assert.False(resolution.IsLocalApp);
        Assert.Equal("p1", resolution.Connector!.PortId);
    }

    [Fact]
    public async Task An_out_of_range_port_fails_with_the_valid_range()
    {
        await using var supervisor = await StartedSupervisorAsync(new SharedRadioBus());
        var router = supervisor.CreateConnectRouter(defaultConnector: null);
        var inbound = new DriveableConnection(RemoteCall.ToString(), NodeTransportKind.Ax25);

        var resolution = router.Resolve(port: 2, RemoteCall, inbound);

        Assert.True(resolution.Failed);
        Assert.Contains("No such port 2 (1..1)", resolution.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_plain_connect_falls_through_to_the_default_connector()
    {
        await using var supervisor = await StartedSupervisorAsync(new SharedRadioBus());
        var fallback = new RecordingConnector("p1");
        var router = supervisor.CreateConnectRouter(fallback);
        var inbound = new DriveableConnection(RemoteCall.ToString(), NodeTransportKind.Ax25);

        // RemoteCall isn't a registered app and no port was given → the session's default dial.
        var resolution = router.Resolve(port: null, RemoteCall, inbound);

        Assert.False(resolution.Failed);
        Assert.False(resolution.IsLocalApp);
        Assert.Same(fallback, resolution.Connector);
    }

    [Fact]
    public async Task A_plain_connect_with_no_default_connector_is_unavailable()
    {
        await using var supervisor = await StartedSupervisorAsync(new SharedRadioBus());
        var router = supervisor.CreateConnectRouter(defaultConnector: null);
        var inbound = new DriveableConnection(RemoteCall.ToString(), NodeTransportKind.Ax25);

        var resolution = router.Resolve(port: null, RemoteCall, inbound);

        Assert.True(resolution.Failed);
        Assert.Contains("not available", resolution.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task From_the_node_prompt_connecting_to_an_app_ssid_bridges_into_the_app()
    {
        var bus = new SharedRadioBus();
        var nodeModem = bus.Attach();
        var remoteModem = bus.Attach();

        var provider = new TestConfigProvider(Config());
        var factory = new FakeTransportFactory().Provide("kiss-tcp:mem:1", nodeModem);
        await using var supervisor = new PortSupervisor(provider, factory, TimeProvider.System, NullLoggerFactory.Instance);
        await supervisor.StartAsync();

        // The chat app binds its SSID and answers with a greeting, echoing what the caller sends.
        var handled = new TaskCompletionSource<(INodeConnection conn, string portId)>(TaskCreationOptions.RunContinuationsAsynchronously);
        var fromCaller = new ConcurrentQueue<string>();
        using var registration = supervisor.RegisterAppCallsign(AppCall, portId: null, async (conn, portId) =>
        {
            handled.TrySetResult((conn, portId));
            await conn.WriteAsync("DAPPSv1>\r"u8.ToArray());
            while (true)
            {
                var chunk = await conn.ReadAsync();
                if (chunk.IsEmpty)
                {
                    break;
                }
                fromCaller.Enqueue(Encoding.UTF8.GetString(chunk.Span));
            }
        });

        await using var remote = new RemoteStation(remoteModem, RemoteCall);
        await remote.StartAsync();

        // Connect to the NODE's own callsign → the console; wait for its banner.
        await remote.ConnectAsync(NodeCall);
        await Wait.ForAsync(() => remote.Saw("Welcome to"), "the node console banner");

        // Now crossconnect to the app SSID from the prompt.
        remote.SendLine("C DAPPS-7");

        var (conn, portId) = await handled.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal("local", portId);                       // the crossconnect label, not an RF port
        Assert.Equal(RemoteCall.ToString(), conn.PeerId);    // the app sees the real caller
        await Wait.ForAsync(() => remote.Saw("DAPPSv1>"), "the app's greeting reached the caller via the crossconnect");

        // Caller → app data flows over the loopback.
        remote.SendLine("HELLO APP");
        await Wait.ForAsync(() => fromCaller.Any(s => s.Contains("HELLO APP", StringComparison.Ordinal)),
            "the caller's line reached the app through the prompt crossconnect");
    }

    // A connector that records nothing but its label — used as a stand-in default.
    private sealed class RecordingConnector(string portId) : IOutboundConnector
    {
        public string PortId => portId;
        public Task<INodeConnection> ConnectAsync(Callsign target, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("not dialled in this test");
    }
}
