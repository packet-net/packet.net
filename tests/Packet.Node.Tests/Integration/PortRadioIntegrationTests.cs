using Microsoft.Extensions.Logging.Abstractions;
using Packet.Ax25.Transport;
using Packet.Core;
using Packet.Node.Core.Configuration;
using Packet.Node.Core.Hosting;
using Packet.Node.Tests.Support;
using Packet.Radio;

namespace Packet.Node.Tests.Integration;

/// <summary>
/// The per-port radio-control attachment (<c>radio:</c>) on a live
/// <see cref="PortSupervisor"/>: a port with a radio block gets its transport wrapped
/// in <see cref="RssiTaggingTransport"/> (and still carries traffic through it); a
/// radio that fails to open degrades cleanly to a working un-tagged port; teardown
/// disposes the pieces in order (tagging wrapper, then the modem chain it didn't own,
/// then the radio last).
/// </summary>
[Trait("Category", "Node")]
public sealed class PortRadioIntegrationTests
{
    private static readonly Callsign NodeCall = new("NODE", 1);
    private static readonly Callsign RemoteCall = new("REMOTE", 1);

    private static PortConfig SerialPortWithRadio(string id, string device) => new()
    {
        Id = id,
        Enabled = true,
        Transport = new SerialKissTransport { Device = device },
        Radio = new PortRadioConfig { Kind = "tait-ccdi", Port = "/dev/ttyUSB0", Baud = 28800 },
        // Bounded connect budget — the in-memory channel is instant (see #47).
        Ax25 = new Ax25PortParams { N2 = TestAx25Timing.NodeN2 },
    };

    private static NodeConfig Config(params PortConfig[] ports) => new()
    {
        Identity = new Identity { Callsign = NodeCall.ToString() },
        Ports = ports,
    };

    [Fact]
    public async Task A_port_with_a_radio_block_runs_over_the_rssi_tagging_wrapper_and_still_carries_traffic()
    {
        var bus = new SharedRadioBus();
        var config = new TestConfigProvider(Config(SerialPortWithRadio("a", "/dev/pty-a")));
        var transports = new FakeTransportFactory().Provide("serial-kiss:/dev/pty-a", bus.Attach());
        var radio = new FakeRadioControl();
        var radios = new FakeRadioControlFactory().Provide(radio);

        await using (var supervisor = new PortSupervisor(
            config, transports, TimeProvider.System, NullLoggerFactory.Instance, radioFactory: radios))
        {
            await supervisor.StartAsync();
            await Wait.ForAsync(() => supervisor.RunningPortIds.Contains("a"), "port a up");

            var port = supervisor.GetPort("a")!;
            port.Transport.Should().BeOfType<RssiTaggingTransport>(
                "a port with a radio block runs over the tagging wrapper");
            port.Radio.Should().BeSameAs(radio, "the running port owns the opened radio");
            port.InnerTransport.Should().NotBeNull("the wrapper doesn't own the modem — the port tracks it");
            port.ModemTransport.Should().BeSameAs(port.InnerTransport,
                "KISS-param application must target the modem beneath the wrapper");

            // The supervisor asked the factory for exactly the configured control channel.
            radios.Requests.Should().ContainSingle()
                .Which.Should().Be(new PortRadioConfig { Kind = "tait-ccdi", Port = "/dev/ttyUSB0", Baud = 28800 });

            // Traffic still flows through the wrapped transport: a remote connects
            // through the shared bus and reaches the node prompt.
            await using var remote = new RemoteStation(bus.Attach(), RemoteCall);
            await remote.StartAsync();
            await remote.ConnectAsync(NodeCall);
            await Wait.ForAsync(() => remote.Saw("Welcome"), "the session reached the prompt over the tagged transport");
        }

        radio.Disposed.Should().BeTrue("tearing the port down must close the radio control channel");
    }

    [Fact]
    public async Task Teardown_disposes_the_modem_chain_before_the_radio()
    {
        // The RSSI-tagging wrapper samples the radio until the wrapper is disposed, so
        // the radio must be the LAST thing closed: wrapper → modem chain → radio.
        var bus = new SharedRadioBus();
        var log = new List<string>();
        var config = new TestConfigProvider(Config(SerialPortWithRadio("a", "/dev/pty-a")));
        var transports = new FakeTransportFactory()
            .Provide("serial-kiss:/dev/pty-a", new DisposalRecordingTransport(bus.Attach(), log, "modem"));
        var radios = new FakeRadioControlFactory().Provide(new FakeRadioControl(log, "radio"));

        await using (var supervisor = new PortSupervisor(
            config, transports, TimeProvider.System, NullLoggerFactory.Instance, radioFactory: radios))
        {
            await supervisor.StartAsync();
            await Wait.ForAsync(() => supervisor.RunningPortIds.Contains("a"), "port a up");
        }

        log.Should().Equal("modem", "radio");
    }

    [Fact]
    public async Task A_radio_that_fails_to_open_degrades_cleanly_to_a_working_untagged_port()
    {
        var bus = new SharedRadioBus();
        var config = new TestConfigProvider(Config(SerialPortWithRadio("a", "/dev/pty-a")));
        var transports = new FakeTransportFactory().Provide("serial-kiss:/dev/pty-a", bus.Attach());
        var radios = new FakeRadioControlFactory().Fault();

        await using var supervisor = new PortSupervisor(
            config, transports, TimeProvider.System, NullLoggerFactory.Instance, radioFactory: radios);
        await supervisor.StartAsync();
        await Wait.ForAsync(() => supervisor.RunningPortIds.Contains("a"), "port a up despite the radio fault");

        var port = supervisor.GetPort("a")!;
        port.Transport.Should().NotBeOfType<RssiTaggingTransport>("the radio failed — no tagging wrapper");
        port.Radio.Should().BeNull();
        port.InnerTransport.Should().BeNull();

        // And the degraded port still carries traffic.
        await using var remote = new RemoteStation(bus.Attach(), RemoteCall);
        await remote.StartAsync();
        await remote.ConnectAsync(NodeCall);
        await Wait.ForAsync(() => remote.Saw("Welcome"), "the session reached the prompt without radio metadata");
    }

    /// <summary>Delegating transport that records its disposal into a shared ordering log.</summary>
    private sealed class DisposalRecordingTransport(IAx25Transport inner, List<string> log, string name) : IAx25Transport
    {
        public Task SendAsync(ReadOnlyMemory<byte> ax25, CancellationToken cancellationToken = default) =>
            inner.SendAsync(ax25, cancellationToken);

        public IAsyncEnumerable<Ax25InboundFrame> ReceiveAsync(CancellationToken cancellationToken = default) =>
            inner.ReceiveAsync(cancellationToken);

        public async ValueTask DisposeAsync()
        {
            log.Add(name);
            await inner.DisposeAsync().ConfigureAwait(false);
        }
    }
}
