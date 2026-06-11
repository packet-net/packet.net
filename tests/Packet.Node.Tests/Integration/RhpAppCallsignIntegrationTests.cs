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
/// The R-3 keystone, end-to-end at the node-host layer over the in-memory radio: an app
/// callsign registered via <see cref="PortSupervisor.RegisterAppCallsign"/> (what the RHPv2
/// server's <c>bind</c>+<c>listen</c> drives) makes the node ANSWER for that callsign — an
/// over-the-air connect to it lands at the registration's handler as an
/// <see cref="INodeConnection"/> (never at the node console), data flows both ways, and the
/// node's own callsign keeps serving the ordinary console untouched.
/// </summary>
[Trait("Category", "Node")]
public sealed class RhpAppCallsignIntegrationTests
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

    [Fact]
    public async Task An_over_the_air_connect_to_a_registered_app_callsign_reaches_the_app_not_the_console()
    {
        var bus = new SharedRadioBus();
        var nodeModem = bus.Attach();
        var remoteModem = bus.Attach();

        var provider = new TestConfigProvider(Config());
        var factory = new FakeTransportFactory().Provide("kiss-tcp:mem:1", nodeModem);
        await using var supervisor = new PortSupervisor(provider, factory, TimeProvider.System, NullLoggerFactory.Instance);
        await supervisor.StartAsync();

        // The RHP server's bind: register the app callsign with a handler that greets the
        // caller and records what it receives (standing in for the accept→child-handle pump).
        var handled = new TaskCompletionSource<(INodeConnection conn, string portId)>(TaskCreationOptions.RunContinuationsAsynchronously);
        var fromCaller = new ConcurrentQueue<string>();
        using var registration = supervisor.RegisterAppCallsign(AppCall, portId: null, async (conn, portId) =>
        {
            handled.TrySetResult((conn, portId));
            await conn.WriteAsync("DAPPSv1>\r"u8.ToArray());
            _ = Task.Run(async () =>
            {
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
        });

        await using var remote = new RemoteStation(remoteModem, RemoteCall);
        await remote.StartAsync();
        await remote.ConnectAsync(AppCall);

        // The handler got the session (with the arrival port), the caller got the APP's
        // greeting — and never the node console banner.
        var (conn, portId) = await handled.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal("p1", portId);
        Assert.Equal(RemoteCall.ToString(), conn.PeerId);
        await Wait.ForAsync(() => remote.Saw("DAPPSv1>"), "the app's greeting reached the caller");
        Assert.DoesNotContain("Welcome to", remote.ReceivedText, StringComparison.Ordinal);

        // Caller → app data flows.
        remote.SendLine("HELLO APP");
        await Wait.ForAsync(() => fromCaller.Any(s => s.Contains("HELLO APP", StringComparison.Ordinal)),
            "the caller's line reached the app handler");
    }

    [Fact]
    public async Task The_node_console_still_serves_the_nodes_own_callsign()
    {
        var bus = new SharedRadioBus();
        var nodeModem = bus.Attach();
        var remoteModem = bus.Attach();

        var provider = new TestConfigProvider(Config());
        var factory = new FakeTransportFactory().Provide("kiss-tcp:mem:1", nodeModem);
        await using var supervisor = new PortSupervisor(provider, factory, TimeProvider.System, NullLoggerFactory.Instance);
        await supervisor.StartAsync();

        using var registration = supervisor.RegisterAppCallsign(AppCall, null, (_, _) => Task.CompletedTask);

        await using var remote = new RemoteStation(remoteModem, RemoteCall);
        await remote.StartAsync();
        await remote.ConnectAsync(NodeCall);

        await Wait.ForAsync(() => remote.Saw("Welcome to"), "the node console banner still serves the node callsign");
    }

    [Fact]
    public async Task Registering_the_nodes_own_callsign_is_refused()
    {
        var bus = new SharedRadioBus();
        var provider = new TestConfigProvider(Config());
        var factory = new FakeTransportFactory().Provide("kiss-tcp:mem:1", bus.Attach());
        await using var supervisor = new PortSupervisor(provider, factory, TimeProvider.System, NullLoggerFactory.Instance);
        await supervisor.StartAsync();

        Assert.Throws<InvalidOperationException>(
            () => supervisor.RegisterAppCallsign(NodeCall, null, (_, _) => Task.CompletedTask));

        // And a duplicate app registration is refused too (one listener per callsign).
        using var first = supervisor.RegisterAppCallsign(AppCall, null, (_, _) => Task.CompletedTask);
        Assert.Throws<InvalidOperationException>(
            () => supervisor.RegisterAppCallsign(AppCall, null, (_, _) => Task.CompletedTask));
    }
}
