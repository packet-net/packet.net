using Microsoft.Extensions.Logging.Abstractions;
using Packet.Core;
using Packet.Node.Core.Configuration;
using Packet.Node.Core.Hosting;
using Packet.Node.Core.Radios;
using Packet.Node.Tests.Support;

namespace Packet.Node.Tests.Radios;

/// <summary>
/// The per-port radio status read model (<see cref="RadioReadModels"/>) over a live
/// <see cref="PortSupervisor"/>: an attached radio reports <c>attached</c> with its carrier-sense; a
/// radio that failed to open reports <c>attached: false</c> (degraded) without breaking the read; a
/// port with no radio block reports <c>attached: false</c> with an empty kind.
/// </summary>
[Trait("Category", "Node")]
public sealed class RadioStatusTests
{
    private static readonly Callsign NodeCall = new("NODE", 1);

    private static PortConfig RadioPort(string id) => new()
    {
        Id = id,
        Enabled = true,
        Transport = new SerialKissTransport { Device = "/dev/pty-" + id },
        Radio = new PortRadioConfig { Kind = "tait-ccdi", Port = "/dev/ttyUSB0", Baud = 28800 },
        Ax25 = new Ax25PortParams { N2 = TestAx25Timing.NodeN2 },
    };

    private static NodeConfig Config(params PortConfig[] ports) => new()
    {
        Identity = new Identity { Callsign = NodeCall.ToString() },
        Ports = ports,
    };

    [Fact]
    public async Task An_attached_radio_reports_attached_with_carrier_sense()
    {
        var bus = new SharedRadioBus();
        var config = new TestConfigProvider(Config(RadioPort("a")));
        var transports = new FakeTransportFactory().Provide("serial-kiss:/dev/pty-a", bus.Attach());
        var radio = new FakeRadioControl();
        var radios = new FakeRadioControlFactory().Provide(radio);

        await using var supervisor = new PortSupervisor(
            config, transports, TimeProvider.System, NullLoggerFactory.Instance, radioFactory: radios);
        await supervisor.StartAsync();
        await Wait.ForAsync(() => supervisor.RunningPortIds.Contains("a"), "port a up");

        radio.RaiseCarrierSense(busy: true, DateTimeOffset.UtcNow);

        var status = RadioReadModels.ForPort(supervisor, config.Current, "a")!;
        status.Attached.Should().BeTrue();
        status.Kind.Should().Be("tait-ccdi");
        status.ControlPort.Should().Be("/dev/ttyUSB0");
        status.ChannelBusy.Should().BeTrue();

        RadioReadModels.All(supervisor, config.Current).Should().ContainSingle()
            .Which.PortId.Should().Be("a");
    }

    [Fact]
    public async Task A_radio_that_fails_to_open_reports_not_attached_without_breaking_the_read()
    {
        var bus = new SharedRadioBus();
        var config = new TestConfigProvider(Config(RadioPort("a")));
        var transports = new FakeTransportFactory().Provide("serial-kiss:/dev/pty-a", bus.Attach());
        var radios = new FakeRadioControlFactory().Fault();

        await using var supervisor = new PortSupervisor(
            config, transports, TimeProvider.System, NullLoggerFactory.Instance, radioFactory: radios);
        await supervisor.StartAsync();
        await Wait.ForAsync(() => supervisor.RunningPortIds.Contains("a"), "port a up (degraded)");

        var status = RadioReadModels.ForPort(supervisor, config.Current, "a")!;
        status.Attached.Should().BeFalse("the radio failed to open — the port degraded");
        status.Kind.Should().Be("tait-ccdi", "the configured kind is still reported");

        // An unknown port id is a null (the endpoint maps that to 404).
        RadioReadModels.ForPort(supervisor, config.Current, "nope").Should().BeNull();
    }

    [Fact]
    public void A_port_with_no_radio_block_reports_not_attached_with_an_empty_kind()
    {
        var config = Config(new PortConfig
        {
            Id = "a",
            Enabled = false,
            Transport = new KissTcpTransport { Host = "127.0.0.1", Port = 8123 },
        });

        var status = RadioReadModels.ForPort(supervisor: null, config, "a")!;
        status.Attached.Should().BeFalse();
        status.Kind.Should().BeEmpty();

        // /radios lists only ports that have a radio block — none here.
        RadioReadModels.All(supervisor: null, config).Should().BeEmpty();
    }
}
