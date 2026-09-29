using Microsoft.Extensions.Logging.Abstractions;
using Packet.Core;
using Packet.Node.Core.Configuration;
using Packet.Node.Core.Hosting;
using Packet.Node.Rhp;
using Packet.Node.Tests.Support;
using Packet.Rhp2;
using Packet.Rhp2.Server;

namespace Packet.Node.Tests.Integration;

/// <summary>
/// How the RHPv2 gateway reads the wire's <c>port</c> (#841). RHPv2 names a port by its
/// number, counted from 1 (PWP-0222's own OPEN example sends <c>"port": 2</c>), and every
/// XRouter-convention client, DAPPS among them, sends it that way. #668 made the gateway match
/// only a port's id, for pdn-bbs, which left DAPPS unable to open a single connection through
/// pdn. Both now work: the id first, then the port's position in configuration order.
/// </summary>
/// <remarks>
/// Two ports on two separate in-memory buses, an <see cref="EchoStation"/> answering on each,
/// so the station that saw the connect identifies the port the dial left on (the pattern of
/// <see cref="ConnectPortSelectionTests"/>).
/// </remarks>
[Trait("Category", "Node")]
public sealed class RhpGatewayPortLabelTests
{
    private static readonly Callsign NodeCall = new("NODE", 1);
    private static readonly Callsign AppCall = new("N0AAA", 3);
    private static readonly Callsign Target = new("GB7RDG", 1);

    private static NodeConfig Config(string firstId, string secondId) => new()
    {
        Identity = new Identity { Callsign = NodeCall.ToString(), Alias = "TESTNODE" },
        Ports =
        [
            new PortConfig { Id = firstId, Enabled = true, Transport = new KissTcpTransport { Host = "mem", Port = 1 } },
            new PortConfig { Id = secondId, Enabled = true, Transport = new KissTcpTransport { Host = "mem", Port = 2 } },
        ],
    };

    private sealed class TwoPortNode : IAsyncDisposable
    {
        public required NodeHostedService Host { get; init; }
        public required SupervisorRhpGateway Gateway { get; init; }
        public required EchoStation OnFirst { get; init; }
        public required EchoStation OnSecond { get; init; }

        public async ValueTask DisposeAsync()
        {
            await OnFirst.DisposeAsync();
            await OnSecond.DisposeAsync();
            Host.Dispose();
        }
    }

    private static async Task<TwoPortNode> StartAsync(string firstId, string secondId)
    {
        var first = new SharedRadioBus();
        var second = new SharedRadioBus();
        var config = new TestConfigProvider(Config(firstId, secondId));
        var factory = new FakeTransportFactory()
            .Provide("kiss-tcp:mem:1", first.Attach())
            .Provide("kiss-tcp:mem:2", second.Attach());
        var host = new NodeHostedService(config, factory, TimeProvider.System, NullLoggerFactory.Instance);
        await host.StartAsync(CancellationToken.None);
        await Wait.ForAsync(
            () => host.Supervisor?.RunningPortIds.Contains(firstId) == true && host.Supervisor.RunningPortIds.Contains(secondId),
            "both ports come up");

        var onFirst = new EchoStation(first.Attach(), Target, "first");
        var onSecond = new EchoStation(second.Attach(), Target, "second");
        await onFirst.StartAsync();
        await onSecond.StartAsync();

        return new TwoPortNode
        {
            Host = host,
            Gateway = new SupervisorRhpGateway(host, config),
            OnFirst = onFirst,
            OnSecond = onSecond,
        };
    }

    [Fact]
    public async Task A_port_number_dials_the_port_in_that_position()
    {
        // DAPPS's form of open: a port number, from an app callsign.
        await using var node = await StartAsync("alpha", "bravo");

        await using var conn = await node.Gateway.OpenAx25StreamAsync(portLabel: "2", local: AppCall.ToString(), remote: Target.ToString());

        node.OnSecond.SawConnect.Should().BeTrue("\"2\" is the second port in configuration order");
        node.OnFirst.SawConnect.Should().BeFalse();
    }

    [Fact]
    public async Task A_port_id_still_dials_that_port()
    {
        // #668's form, what pdn-bbs sends, is unchanged.
        await using var node = await StartAsync("alpha", "bravo");

        await using var conn = await node.Gateway.OpenAx25StreamAsync(portLabel: "BRAVO", local: AppCall.ToString(), remote: Target.ToString());

        node.OnSecond.SawConnect.Should().BeTrue();
        node.OnFirst.SawConnect.Should().BeFalse();
    }

    [Fact]
    public async Task A_port_whose_id_is_a_number_is_that_port_not_the_port_in_that_position()
    {
        // The workaround DAPPS's docs give today is to name a port "1". An id match wins over a
        // position, so a node configured [id "2", id "1"] sends "1" to the SECOND entry.
        await using var node = await StartAsync("2", "1");

        await using var conn = await node.Gateway.OpenAx25StreamAsync(portLabel: "1", local: AppCall.ToString(), remote: Target.ToString());

        node.OnSecond.SawConnect.Should().BeTrue("the port whose id is \"1\" is the second entry");
        node.OnFirst.SawConnect.Should().BeFalse();
    }

    [Theory]
    [InlineData("0")]
    [InlineData("3")]
    [InlineData("-1")]
    [InlineData("charlie")]
    public async Task A_label_that_is_neither_an_id_nor_a_port_number_is_NoSuchPort(string label)
    {
        await using var node = await StartAsync("alpha", "bravo");

        var open = () => node.Gateway.OpenAx25StreamAsync(portLabel: label, local: AppCall.ToString(), remote: Target.ToString());
        var ex = (await open.Should().ThrowAsync<RhpGatewayException>()).Which;

        ex.ErrCode.Should().Be(RhpErrorCode.NoSuchPort);
        ex.Message.Should().Contain($"No such port '{label}'").And.Contain("a number from 1 to 2");
        node.OnFirst.SawConnect.Should().BeFalse();
        node.OnSecond.SawConnect.Should().BeFalse();
    }

    [Fact]
    public async Task A_bind_takes_the_port_number()
    {
        // The passive half: bind + listen with DAPPS's numeric port registers on that port, and
        // a number past the end is refused the same way an open is.
        await using var node = await StartAsync("alpha", "bravo");

        using var registration = node.Gateway.RegisterListener("2", AppCall.ToString(), (_, _) => Task.CompletedTask);
        registration.Should().NotBeNull();

        var bindPastTheEnd = () => node.Gateway.RegisterListener("3", new Callsign("N0AAA", 4).ToString(), (_, _) => Task.CompletedTask);
        bindPastTheEnd.Should().Throw<RhpGatewayException>().Which.ErrCode.Should().Be(RhpErrorCode.NoSuchPort);
    }
}
