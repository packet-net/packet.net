using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Packet.Node.Core.Configuration;
using Packet.Node.Core.Hosting;
using Packet.Node.Tests.Support;

namespace Packet.Node.Tests.Hosting;

/// <summary>
/// The bring-up retry loop: a port whose bring-up fails (the Pi boots slower than the node's
/// LXC, a head-end is mid-restart, a USB TNC enumerates late) must come back on its own -
/// reconcile only runs on config change, so without the retry the port stays down until an
/// operator edits config (#576, generalised to every transport kind in #722).
///
/// <para>The gate discipline is the other half: the old loop held the supervisor's mutation gate
/// across a whole 30 s attempt, so a blackholing head-end made every reconcile and every web
/// action queue behind a port that was not even up. The attempt now opens the transport OUTSIDE
/// the gate and takes it only for the port-set mutation.</para>
/// </summary>
[Trait("Category", "Node")]
public sealed class PortBringUpRetryTests
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

        supervisor.RunningPortIds.Should().BeEmpty("the head-end is unreachable at boot - the port faults");

        // The head-end appears (Pi finished booting): the transport now opens.
        transports.ClearFault(Endpoint).Provide(Endpoint, bus.Attach());

        // Nothing happens until the retry interval elapses - then the loop brings the port up
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
        supervisor.RunningPortIds.Should().BeEmpty("every attempt so far failed - the port stays down, nothing crashes");

        transports.ClearFault(Endpoint).Provide(Endpoint, bus.Attach());
        for (int i = 0; i < 40 && !supervisor.RunningPortIds.Contains("a"); i++)
        {
            clock.Advance(TimeSpan.FromSeconds(10));
            await Task.Delay(25);
        }

        await Wait.ForAsync(() => supervisor.RunningPortIds.Contains("a"),
            "the loop retries indefinitely and succeeds when the head-end finally appears");
    }

    [Fact]
    public async Task A_retry_stalled_in_a_blackholing_dial_does_not_block_an_exclusive_operation_on_another_port()
    {
        // The reported symptom (#722): a head-end that neither answers nor refuses (DROP
        // firewall, dead Pi) made the control API appear hung for ~30 s at a time, repeatedly,
        // on operations touching entirely different ports - because the retry loop held the
        // supervisor's mutation gate for the whole attempt, and every reconcile / port restart /
        // session action queues behind it.
        var clock = new FakeTimeProvider();
        var bus = new SharedRadioBus();
        var config = new TestConfigProvider(new NodeConfig
        {
            Identity = new Identity { Callsign = "NODE-1" },
            Ports =
            [
                new PortConfig
                {
                    Id = "a",
                    Enabled = true,
                    Transport = new NinoTncTcpTransport { HeadEndId = "pi-shack", DeviceId = "nino0", Mode = 6 },
                    Ax25 = new Ax25PortParams { N2 = TestAx25Timing.NodeN2 },
                },
                new PortConfig
                {
                    Id = "healthy",
                    Enabled = true,
                    Transport = new KissTcpTransport { Host = "mem", Port = 9 },
                    Ax25 = new Ax25PortParams { N2 = TestAx25Timing.NodeN2 },
                },
            ],
        });
        var transports = new FakeTransportFactory()
            .Fault(Endpoint)
            .Provide("kiss-tcp:mem:9", bus.Attach(), bus.Attach());

        await using var supervisor = new PortSupervisor(
            config, transports, clock, NullLoggerFactory.Instance);
        await supervisor.StartAsync();
        await Wait.ForAsync(() => supervisor.RunningPortIds.Contains("healthy"), "the healthy port is up");

        // The head-end starts blackholing: the next retry attempt parks inside the dial.
        transports.ClearFault(Endpoint).Stall(Endpoint);
        for (int i = 0; i < 40 && !transports.IsStalling(Endpoint); i++)
        {
            clock.Advance(PortSupervisor.RetryInitialDelay);
            await Task.Delay(25);
        }
        await Wait.ForAsync(() => transports.IsStalling(Endpoint), "a retry attempt is parked in the dial");

        // The exclusive operation a POST /sessions or a port restart runs (the host takes its own
        // gate and then the supervisor's) must still complete promptly.
        var restart = supervisor.RestartPortAsync("healthy");
        var finished = await Task.WhenAny(restart, Task.Delay(TimeSpan.FromSeconds(10)));
        finished.Should().BeSameAs(restart,
            "a stalled bring-up retry must not hold the mutation gate across its dial");
        (await restart).Should().BeTrue();
        transports.IsStalling(Endpoint).Should().BeTrue("the retry is still parked - that is the point");

        transports.Release(Endpoint);
    }
}
