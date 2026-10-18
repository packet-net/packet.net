using Microsoft.Extensions.Logging.Abstractions;
using Packet.Ax25.Session;
using Packet.Core;
using Packet.Node.Core.Configuration;
using Packet.Node.Core.Hosting;
using Packet.Node.Rhp;
using Packet.Node.Tests.Support;
using Packet.Rhp2;
using Packet.Rhp2.Server;

namespace Packet.Node.Tests.Integration;

/// <summary>
/// An RHP <c>open</c> that fails says why (packet-net/packet.net#849): a station that answers
/// DM is <c>errCode 19</c> Refused, a station that never answers is <c>errCode 20</c> No answer.
/// Through the node's real gateway, connector and port, over the shared in-memory channel.
/// </summary>
[Trait("Category", "Node")]
public sealed class RhpOpenFailureReasonTests
{
    private static readonly Callsign NodeCall = new("N0BBB", 1);
    private static readonly Callsign StationCall = new("N0AAA", 3);

    // Short T1 and N2 so the silent case gives up in a second or two, not a minute.
    private static NodeConfig Config() => new()
    {
        Identity = new Identity { Callsign = NodeCall.ToString(), Alias = "TESTNODE" },
        Ports =
        [
            new PortConfig
            {
                Id = "p1",
                Enabled = true,
                Transport = new KissTcpTransport { Host = "mem", Port = 1 },
                Ax25 = new Ax25PortParams { N2 = 1, T1Ms = 300 },
            },
        ],
    };

    private static async Task<(NodeHostedService host, SupervisorRhpGateway gateway)> StartAsync(SharedRadioBus bus)
    {
        var config = new TestConfigProvider(Config());
        var factory = new FakeTransportFactory().Provide("kiss-tcp:mem:1", bus.Attach());
        var host = new NodeHostedService(config, factory, TimeProvider.System, NullLoggerFactory.Instance);
        await host.StartAsync(CancellationToken.None);
        await Wait.ForAsync(() => host.Supervisor?.RunningPortIds.Contains("p1") == true, "port p1 comes up");
        return (host, new SupervisorRhpGateway(host, config));
    }

    [Fact]
    public async Task A_station_that_answers_DM_is_Refused()
    {
        var bus = new SharedRadioBus();
        // A station that is on the air and refuses every call: a listener not accepting answers
        // a SABM(E) with DM.
        await using var station = new Ax25Listener(bus.Attach(), new Ax25ListenerOptions { MyCall = StationCall }, TimeProvider.System);
        await station.StartAsync();
        station.AcceptIncoming = false;

        var (host, gateway) = await StartAsync(bus);
        using var _ = host;

        var open = () => gateway.OpenAx25StreamAsync("p1", local: null, remote: StationCall.ToString());
        var ex = (await open.Should().ThrowAsync<RhpGatewayException>()).Which;

        ex.ErrCode.Should().Be(RhpErrorCode.Refused);
        ex.Message.Should().Contain("refused");
    }

    [Fact]
    public async Task A_station_that_never_answers_is_No_answer()
    {
        var (host, gateway) = await StartAsync(new SharedRadioBus());
        using var _ = host;

        var open = () => gateway.OpenAx25StreamAsync("p1", local: null, remote: StationCall.ToString());
        var ex = (await open.Should().ThrowAsync<RhpGatewayException>()).Which;

        ex.ErrCode.Should().Be(RhpErrorCode.NoAnswer);
        ex.Message.Should().Contain("no answer");
    }
}
