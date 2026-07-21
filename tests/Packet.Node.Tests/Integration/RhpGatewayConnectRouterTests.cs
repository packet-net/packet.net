using System.Collections.Concurrent;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Packet.Core;
using Packet.Node.Core.Configuration;
using Packet.Node.Core.Console;
using Packet.Node.Core.Hosting;
using Packet.Node.Rhp;
using Packet.Node.Tests.Support;
using Packet.Rhp2;
using Packet.Rhp2.Server;

namespace Packet.Node.Tests.Integration;

/// <summary>
/// The RHPv2 gateway (<see cref="SupervisorRhpGateway.OpenAx25StreamAsync"/>) now shares the
/// console's connect routing: an app's outbound <c>open</c> to a callsign the node is locally
/// registered for — with no explicit port — bridges in-process (loopback crossconnect) instead
/// of dialling RF, so one local app can open another and the target sees the originating
/// callsign. An explicit port stays a direct dial (and validates). NET/ROM out of scope.
/// </summary>
[Trait("Category", "Node")]
public sealed class RhpGatewayConnectRouterTests
{
    private static readonly Callsign NodeCall = new("NODE", 1);
    private static readonly Callsign AppCall = new("DAPPS", 7);
    private static readonly Callsign OriginApp = new("CHAT", 1);

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
    public async Task An_app_open_to_a_local_app_ssid_bridges_in_process_no_rf()
    {
        var (host, config) = await StartedHostAsync(new SharedRadioBus());
        using var _ = host;

        var handled = new TaskCompletionSource<(INodeConnection conn, string portId)>(TaskCreationOptions.RunContinuationsAsynchronously);
        var fromCaller = new ConcurrentQueue<string>();
        using var registration = host.Supervisor!.RegisterAppCallsign(AppCall, portId: null, async (conn, portId) =>
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

        var gateway = new SupervisorRhpGateway(host, config);

        // The originating app (CHAT-1) opens DAPPS-7 with NO port → in-process crossconnect.
        await using var userConn = await gateway.OpenAx25StreamAsync(portLabel: null, local: OriginApp.ToString(), remote: AppCall.ToString());

        var (conn, portId) = await handled.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal("local", portId);                        // crossconnect, not an RF port
        Assert.Equal(OriginApp.ToString(), conn.PeerId);      // target app sees the originating app

        // App → caller (the greeting comes back over the returned connection)…
        var greeting = await ReadTextAsync(userConn, TimeSpan.FromSeconds(10));
        Assert.Contains("DAPPSv1>", greeting, StringComparison.Ordinal);

        // …and caller → app.
        await userConn.WriteAsync("HELLO APP\r"u8.ToArray());
        await Wait.ForAsync(() => fromCaller.Any(s => s.Contains("HELLO APP", StringComparison.Ordinal)),
            "the originating app's line reached the target app over the loopback");
    }

    [Fact]
    public async Task A_local_app_whose_handler_returns_immediately_stays_bridged()
    {
        // The regression this guards (the deterministic one-node repro): the REAL RHPv2 server's
        // accept handler pushes ACCEPT then pumps the connection in the BACKGROUND and RETURNS
        // immediately. LocalAppConnector used to dispose the app end of the loopback on that
        // (immediate) return — tearing the bridge down before any data crossed (Connected ->
        // immediately Disconnected, app got the accept but its banner never reached the caller).
        // The earlier crossconnect tests missed it because their handlers await a read loop
        // (return only when the link ends). This one mirrors the real server: own + return.
        var (host, config) = await StartedHostAsync(new SharedRadioBus());
        using var hostLifetime = host;

        var fromCaller = new ConcurrentQueue<string>();
        using var registration = host.Supervisor!.RegisterAppCallsign(AppCall, portId: null, (conn, portId) =>
        {
            _ = Task.Run(async () =>
            {
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
            return Task.CompletedTask;   // ownership taken; pump runs on — return immediately
        });

        var gateway = new SupervisorRhpGateway(host, config);
        await using var userConn = await gateway.OpenAx25StreamAsync(portLabel: null, local: OriginApp.ToString(), remote: AppCall.ToString());

        // The bridge must outlive the handler's return: the app's banner reaches the caller…
        var greeting = await ReadTextAsync(userConn, TimeSpan.FromSeconds(10));
        Assert.Contains("DAPPSv1>", greeting, StringComparison.Ordinal);

        // …and caller → app still flows.
        await userConn.WriteAsync("PING\r"u8.ToArray());
        await Wait.ForAsync(() => fromCaller.Any(s => s.Contains("PING", StringComparison.Ordinal)),
            "caller data reaches the app after the handler returned — the bridge was not torn down");
    }

    [Fact]
    public async Task An_explicit_port_skips_the_local_app_and_validates_the_port()
    {
        var (host, config) = await StartedHostAsync(new SharedRadioBus());
        using var _ = host;

        // DAPPS-7 is registered as a local app, but an EXPLICIT port is "go to RF": the gateway
        // dials, and port 2 doesn't exist (only p1) → NoSuchPort, never a crossconnect.
        using var registration = host.Supervisor!.RegisterAppCallsign(AppCall, portId: null, (_, _) => Task.CompletedTask);
        var gateway = new SupervisorRhpGateway(host, config);

        var ex = await Assert.ThrowsAsync<RhpGatewayException>(() =>
            gateway.OpenAx25StreamAsync(portLabel: "2", local: null, remote: AppCall.ToString()));

        Assert.Equal(RhpErrorCode.NoSuchPort, ex.ErrCode);
        Assert.Contains("No such port '2' (1..1)", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_null_port_with_no_local_app_throws_NoSuchPort_not_a_silent_dial()
    {
        // #665: a null/blank port must NOT silently default to ports[0] for an RF dial.
        var (host, config) = await StartedHostAsync(new SharedRadioBus());
        using var _ = host;

        var gateway = new SupervisorRhpGateway(host, config);

        var ex = await Assert.ThrowsAsync<RhpGatewayException>(() =>
            gateway.OpenAx25StreamAsync(portLabel: null, local: null, remote: "GB7RDG"));

        Assert.Equal(RhpErrorCode.NoSuchPort, ex.ErrCode);
        Assert.Contains("explicit port", ex.Message, StringComparison.Ordinal);
    }

    private static async Task<string> ReadTextAsync(INodeConnection conn, TimeSpan budget)
    {
        using var cts = new CancellationTokenSource(budget);
        var chunk = await conn.ReadAsync(cts.Token);
        return Encoding.UTF8.GetString(chunk.Span);
    }
}
