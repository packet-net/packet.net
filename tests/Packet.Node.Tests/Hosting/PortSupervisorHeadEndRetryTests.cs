using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Packet.Node.Core.Configuration;
using Packet.Node.Core.Hosting;
using Packet.Node.Tests.Support;

namespace Packet.Node.Tests.Hosting;

/// <summary>
/// The head-end bring-up retry loop (#576): a head-end-bound port whose bring-up fails (the Pi
/// boots slower than the node's LXC, or the head-end is mid-restart) must come back on its own —
/// reconcile only runs on config change, so without the retry the port stays down until an
/// operator edits config.
/// </summary>
[Trait("Category", "Node")]
public sealed class PortSupervisorHeadEndRetryTests
{
    private const string Endpoint = "nino-tnc-tcp:pi-shack/nino0";

    private static NodeConfig Config(bool enabled = true) => new()
    {
        Identity = new Identity { Callsign = "NODE-1" },
        Ports =
        [
            new PortConfig
            {
                Id = "a",
                Enabled = enabled,
                Transport = new NinoTncTcpTransport { HeadEndId = "pi-shack", DeviceId = "nino0", Mode = 6 },
                Ax25 = new Ax25PortParams { N2 = TestAx25Timing.NodeN2 },
            },
        ],
    };

    [Fact]
    public async Task A_head_end_port_that_fails_at_boot_comes_up_on_the_retry_once_the_head_end_appears()
    {
        var clock = new FakeTimeProvider();
        var bus = new SharedRadioBus();
        var config = new TestConfigProvider(Config());
        var transports = new FakeTransportFactory().Fault(Endpoint);

        await using var supervisor = new PortSupervisor(
            config, transports, clock, NullLoggerFactory.Instance);
        await supervisor.StartAsync();

        supervisor.RunningPortIds.Should().BeEmpty("the head-end is unreachable at boot — the port faults");

        // The head-end appears (Pi finished booting): the transport now opens.
        transports.ClearFault(Endpoint).Provide(Endpoint, bus.Attach());

        // Nothing happens until the retry interval elapses — then the loop brings the port up
        // with NO config change. Walk the fake clock; the loop's delay + gate hops run on real
        // threads, so poll briefly between steps.
        for (int i = 0; i < 40 && !supervisor.RunningPortIds.Contains("a"); i++)
        {
            clock.Advance(TimeSpan.FromSeconds(10));
            await Task.Delay(25);
        }

        await Wait.ForAsync(() => supervisor.RunningPortIds.Contains("a"),
            "the retry loop brings the head-end port up once the head-end answers");
    }

    [Fact]
    public async Task The_retry_keeps_trying_across_multiple_failed_attempts()
    {
        var clock = new FakeTimeProvider();
        var bus = new SharedRadioBus();
        var config = new TestConfigProvider(Config());
        var transports = new FakeTransportFactory().Fault(Endpoint);

        await using var supervisor = new PortSupervisor(
            config, transports, clock, NullLoggerFactory.Instance);
        await supervisor.StartAsync();

        // Let several retry attempts fail (the head-end still down).
        for (int i = 0; i < 8; i++)
        {
            clock.Advance(TimeSpan.FromSeconds(30));
            await Task.Delay(25);
        }
        supervisor.RunningPortIds.Should().BeEmpty("every attempt so far failed — the port stays down, nothing crashes");

        transports.ClearFault(Endpoint).Provide(Endpoint, bus.Attach());
        for (int i = 0; i < 40 && !supervisor.RunningPortIds.Contains("a"); i++)
        {
            clock.Advance(TimeSpan.FromSeconds(10));
            await Task.Delay(25);
        }

        await Wait.ForAsync(() => supervisor.RunningPortIds.Contains("a"),
            "the loop retries indefinitely and succeeds when the head-end finally appears");
    }
}
