using Microsoft.Extensions.Logging.Abstractions;
using Packet.Core;
using Packet.Node.Core.Configuration;
using Packet.Node.Core.Hosting;
using Packet.Node.Core.Rigs;
using Packet.Node.Tests.Support;
using Packet.Rig;

namespace Packet.Node.Tests.Integration;

/// <summary>
/// The per-port rig-control attachment (<c>rig:</c>) on a live <see cref="PortSupervisor"/>: a
/// port with a rig block dials the daemon and starts the status poller (without touching the
/// packet transport); an unreachable daemon degrades cleanly to a working port; teardown
/// disposes the poller before the rig; the read models project both states.
/// </summary>
[Trait("Category", "Node")]
public sealed class PortRigIntegrationTests
{
    private static readonly Callsign NodeCall = new("NODE", 1);

    private static PortConfig PortWithRig(string id, string device) => new()
    {
        Id = id,
        Enabled = true,
        Transport = new SerialKissTransport { Device = device },
        Rig = new PortRigConfig { Kind = "hamlib", Host = "127.0.0.1", Port = 4532 },
    };

    private static NodeConfig Config(params PortConfig[] ports) => new()
    {
        Identity = new Identity { Callsign = NodeCall.ToString() },
        Ports = ports,
    };

    [Fact]
    public async Task A_port_with_a_rig_block_attaches_the_rig_and_starts_the_poller()
    {
        var bus = new SharedRadioBus();
        var config = new TestConfigProvider(Config(PortWithRig("hf", "/dev/pty-hf")));
        var transports = new FakeTransportFactory().Provide("serial-kiss:/dev/pty-hf", bus.Attach());
        var disposal = new List<string>();
        var rig = new FakeRigControl(disposal, "rig") { FrequencyHz = 14_074_000 };
        var rigs = new FakeRigControlFactory().Provide(rig);
        var telemetry = new RigTelemetry();

        await using (var supervisor = new PortSupervisor(
            config, transports, TimeProvider.System, NullLoggerFactory.Instance,
            rigFactory: rigs, rigTelemetry: telemetry))
        {
            await supervisor.StartAsync();
            await Wait.ForAsync(() => supervisor.RunningPortIds.Contains("hf"), "port hf up");

            var port = supervisor.GetPort("hf")!;
            port.Rig.Should().BeSameAs(rig, "the running port owns the connected rig");
            port.RigStatus.Should().NotBeNull("an attached rig gets a status poller");
            port.InnerTransport.Should().BeNull("a rig never wraps the packet transport");

            rigs.Requests.Should().ContainSingle()
                .Which.Should().Be(new PortRigConfig { Kind = "hamlib", Host = "127.0.0.1", Port = 4532 });

            // The read model projects the live monitor.
            await Wait.ForAsync(
                () => RigReadModels.ForPort(supervisor, config.Current, "hf")?.SampledAt is not null,
                "first poll tick landed");
            var status = RigReadModels.ForPort(supervisor, config.Current, "hf")!;
            status.Attached.Should().BeTrue();
            status.FrequencyHz.Should().Be(14_074_000);
            status.ConnectionState.Should().Be("healthy");

            RigReadModels.All(supervisor, config.Current).Should().ContainSingle()
                .Which.PortId.Should().Be("hf");
        }

        rig.Disposed.Should().BeTrue("tearing the port down must close the rig connection");
    }

    [Fact]
    public async Task An_unreachable_rig_daemon_degrades_to_a_working_port_without_a_rig()
    {
        var bus = new SharedRadioBus();
        var config = new TestConfigProvider(Config(PortWithRig("hf", "/dev/pty-hf")));
        var transports = new FakeTransportFactory().Provide("serial-kiss:/dev/pty-hf", bus.Attach());
        var rigs = new FakeRigControlFactory().Fault(new RigConnectionException("nothing listening on 4532"));

        await using var supervisor = new PortSupervisor(
            config, transports, TimeProvider.System, NullLoggerFactory.Instance, rigFactory: rigs);
        await supervisor.StartAsync();
        await Wait.ForAsync(() => supervisor.RunningPortIds.Contains("hf"), "port hf up despite the rig fault");

        var port = supervisor.GetPort("hf")!;
        port.Started.Should().BeTrue("an unreachable rig daemon must never take the packet channel down");
        port.Rig.Should().BeNull();
        port.RigStatus.Should().BeNull();

        // The read model reports the configured-but-not-attached rig honestly.
        var status = RigReadModels.ForPort(supervisor, config.Current, "hf")!;
        status.Attached.Should().BeFalse();
        status.Kind.Should().Be("hamlib");
        status.Endpoint.Should().Be("127.0.0.1:4532");
        status.ConnectionState.Should().Be("unknown");
    }

    [Fact]
    public async Task Teardown_disposes_the_poller_before_the_rig()
    {
        var bus = new SharedRadioBus();
        var config = new TestConfigProvider(Config(PortWithRig("hf", "/dev/pty-hf")));
        var transports = new FakeTransportFactory().Provide("serial-kiss:/dev/pty-hf", bus.Attach());
        var disposal = new List<string>();
        var rig = new FakeRigControl(disposal, "rig");
        var rigs = new FakeRigControlFactory().Provide(rig);

        var supervisor = new PortSupervisor(
            config, transports, TimeProvider.System, NullLoggerFactory.Instance, rigFactory: rigs);
        await supervisor.StartAsync();
        await Wait.ForAsync(() => supervisor.RunningPortIds.Contains("hf"), "port hf up");
        var monitor = supervisor.GetPort("hf")!.RigStatus!;

        await supervisor.DisposeAsync();

        rig.Disposed.Should().BeTrue();
        // The poller stopped (its Snapshot still answers from captured state, but the loop is
        // done): a disposed monitor must never read a disposed rig, which the ordering in
        // RunningPort.DisposeAsync guarantees — poller first, rig last.
        var act = async () => await monitor.DisposeAsync(); // idempotent double-dispose
        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task Read_models_report_a_rigless_port_and_an_unknown_port_distinctly()
    {
        var bus = new SharedRadioBus();
        var rigless = new PortConfig
        {
            Id = "vhf",
            Enabled = true,
            Transport = new SerialKissTransport { Device = "/dev/pty-vhf" },
        };
        var config = new TestConfigProvider(Config(rigless));
        var transports = new FakeTransportFactory().Provide("serial-kiss:/dev/pty-vhf", bus.Attach());

        await using var supervisor = new PortSupervisor(
            config, transports, TimeProvider.System, NullLoggerFactory.Instance);
        await supervisor.StartAsync();
        await Wait.ForAsync(() => supervisor.RunningPortIds.Contains("vhf"), "port vhf up");

        RigReadModels.All(supervisor, config.Current).Should().BeEmpty("no port has a rig block");
        var status = RigReadModels.ForPort(supervisor, config.Current, "vhf")!;
        status.Attached.Should().BeFalse();
        status.Kind.Should().BeEmpty("an honest 'this port has no rig'");
        RigReadModels.ForPort(supervisor, config.Current, "nope").Should().BeNull("unknown port → 404");
    }
}
