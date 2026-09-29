using Microsoft.Extensions.Logging.Abstractions;
using Packet.Ax25.Session;
using Packet.Ax25.Transport;
using Packet.Core;
using Packet.Node.Core.Configuration;
using Packet.Node.Core.Hosting;
using Packet.Node.Rhp;
using Packet.Node.Tests.Support;

namespace Packet.Node.Tests.Integration;

/// <summary>
/// #843: a peer that hangs up straight after answering an outbound <c>open</c> (UA, then DISC
/// a millisecond later) must end the connection the gateway handed back, so the RHPv2 server's
/// pump sees it end and pushes <c>close</c> to the client. The DISC lands between the listener's
/// connect confirm and the moment <c>Ax25NodeConnection</c> attaches to the session, and the
/// session's early-signal buffer replayed only data across that gap, so the disconnect went to
/// nobody and the handle stayed "connected" until the client itself gave up.
/// </summary>
[Trait("Category", "Node")]
public sealed class RhpGatewayImmediateHangUpTests
{
    private static readonly Callsign NodeCall = new("N0BBB", 1);
    private static readonly Callsign AppCall = new("N0BBB", 3);
    private static readonly Callsign Target = new("N0AAA", 3);

    private static NodeConfig Config() => new()
    {
        Identity = new Identity { Callsign = NodeCall.ToString(), Alias = "TESTNODE" },
        Ports = [new PortConfig { Id = "p1", Enabled = true, Transport = new KissTcpTransport { Host = "mem", Port = 1 } }],
    };

    [Fact]
    public async Task A_disc_straight_after_the_ua_ends_the_opened_connection()
    {
        var bus = new SharedRadioBus();
        var config = new TestConfigProvider(Config());
        var factory = new FakeTransportFactory().Provide("kiss-tcp:mem:1", bus.Attach());
        using var host = new NodeHostedService(config, factory, TimeProvider.System, NullLoggerFactory.Instance);
        await host.StartAsync(CancellationToken.None);
        await Wait.ForAsync(() => host.Supervisor?.RunningPortIds.Contains("p1") == true, "port p1 comes up");

        // What node A did in the issue: no app registered on the called callsign, so it
        // answered the SABME with UA and disconnected at once.
        await using var station = new HangUpStation(bus.Attach(), Target);
        await station.StartAsync();

        var gateway = new SupervisorRhpGateway(host, config);
        await using var conn = await gateway.OpenAx25StreamAsync(portLabel: "p1", local: AppCall.ToString(), remote: Target.ToString());

        await conn.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        (await conn.ReadAsync()).IsEmpty.Should().BeTrue("a connection whose link has ended reads EOF");
        station.HungUp.Should().BeTrue();
    }

    // A bare station that accepts an inbound connect and hangs up in the same breath: the DISC
    // is posted from the connect indication, so it goes out straight after the UA.
    private sealed class HangUpStation : IAsyncDisposable
    {
        private readonly Ax25Listener listener;
        private volatile bool hungUp;

        public HangUpStation(IAx25Transport transport, Callsign myCall)
        {
            listener = new Ax25Listener(transport, new Ax25ListenerOptions
            {
                MyCall = myCall,
                ConfigureSession = session => session.DataLinkSignalEmitted += (_, sig) =>
                {
                    if (sig is DataLinkConnectIndication)
                    {
                        hungUp = true;
                        session.PostEvent(new DlDisconnectRequest());
                    }
                },
                N2 = TestAx25Timing.StationN2,
            }, TimeProvider.System);
        }

        public bool HungUp => hungUp;

        public async Task StartAsync()
        {
            await listener.StartAsync().ConfigureAwait(false);
            listener.AcceptIncoming = true;
        }

        public async ValueTask DisposeAsync() => await listener.DisposeAsync().ConfigureAwait(false);
    }
}
