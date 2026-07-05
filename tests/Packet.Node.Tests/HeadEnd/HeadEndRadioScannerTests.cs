using System.Text.Json;
using Packet.Node.Core.Api;
using Packet.Node.Core.Configuration;
using Packet.Node.Core.HeadEnd;
using Packet.Node.Core.Radios;
using Packet.Node.Tests.Support;

namespace Packet.Node.Tests.HeadEnd;

/// <summary>
/// The remote fleet scanner (<see cref="HeadEndRadioScanner"/>): reach-through identification of free
/// head-end devices over loopback raw pipes that answer GETVER (NinoTNC) and MODEL (Tait), the Tait
/// CCDI baud sweep, skipping devices already bound to a configured port, the matched-pair proposals
/// (unambiguous auto-suggest vs ambiguous manual choice), and the duplicate-instance-id conflict.
/// </summary>
[Trait("Category", "Node")]
public sealed class HeadEndRadioScannerTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    private static NodeConfig ConfigWith(
        IEnumerable<HeadEndConfig>? headEnds = null, IEnumerable<PortConfig>? ports = null) => new()
    {
        Identity = new Identity { Callsign = "M0LTE-1" },
        HeadEnds = (headEnds ?? []).ToArray(),
        Ports = (ports ?? []).ToArray(),
    };

    private static HeadEndRadioScanner ScannerOver(
        StubHeadEndHandler handler, FakeHeadEndDiscovery discovery, TimeSpan? identify = null) => new(
        discovery,
        clientFactory: uri => new HeadEndClient(uri, new HttpClient(handler)),
        loggerFactory: null,
        discoveryTimeout: TimeSpan.FromMilliseconds(200),
        identifyTimeout: identify ?? TimeSpan.FromSeconds(2),
        connectTimeout: TimeSpan.FromSeconds(2));

    private static int BaudOf(StubHeadEndHandler.LineCall call) =>
        JsonDocument.Parse(call.RawBody).RootElement.GetProperty("baud").GetInt32();

    [Fact]
    public async Task A_free_nino_device_is_identified_via_getver()
    {
        using var pipe = new LoopbackRawPipe();
        var responder = pipe.RespondGetVerAsync("3.41");
        var handler = new StubHeadEndHandler(new HeadEndInventory
        {
            InstanceId = "pi-shack",
            Ports = [new HeadEndPortInfo { Id = "nino0", TcpPort = pipe.Port, Baud = 57600, UsbVid = "04d8" }],
        });
        var discovery = new FakeHeadEndDiscovery(new DiscoveredHeadEnd("pi-shack", "127.0.0.1", 7300));

        var scan = await ScannerOver(handler, discovery).ScanAsync(ConfigWith()).WaitAsync(Timeout);

        var instance = scan.Instances.Should().ContainSingle().Subject;
        instance.InstanceId.Should().Be("pi-shack");
        instance.Source.Should().Be("mdns");
        instance.Reachable.Should().BeTrue();
        var device = instance.Devices.Should().ContainSingle().Subject;
        device.Kind.Should().Be(HeadEndDeviceKind.NinoTnc);
        device.Version.Should().Be("3.41");
        device.Free.Should().BeTrue();
        // #567: a NinoTNC's KISS baud is a fixed 57600 — the identify clocks the head-end line to it
        // (once, no sweep) before GETVER.
        handler.LineCalls.Select(BaudOf).Should().Equal(57600);
        await responder.WaitAsync(Timeout);
    }

    [Fact]
    public async Task A_free_tait_device_is_identified_via_model_without_a_sweep()
    {
        using var pipe = new LoopbackRawPipe();
        var responder = pipe.RespondTaitIdentityAsync(ccdiVersion: "03.02", serial: "1G000123");
        var handler = new StubHeadEndHandler(new HeadEndInventory
        {
            InstanceId = "pi-shack",
            Ports = [new HeadEndPortInfo { Id = "tait0", TcpPort = pipe.Port, Baud = 28800, UsbVid = "10c4" }],
        });
        var discovery = new FakeHeadEndDiscovery(new DiscoveredHeadEnd("pi-shack", "127.0.0.1", 7300));

        var scan = await ScannerOver(handler, discovery).ScanAsync(ConfigWith()).WaitAsync(Timeout);

        var device = scan.Instances.Single().Devices.Single();
        device.Kind.Should().Be(HeadEndDeviceKind.TaitCcdi);
        device.Model.Should().Be("Tait TM8110");
        device.Serial.Should().Be("1G000123");
        device.Version.Should().Be("03.02");
        device.Baud.Should().Be(28800);
        // The band split is read off the product code (record [00]) — 2m for the default rig.
        device.BandCode.Should().Be("B1");
        device.AmateurBand.Should().Be("2m");
        // It answered at the inventory clock — the only line call is the open-time clock, no sweep.
        handler.LineCalls.Select(BaudOf).Should().Equal(28800);
        await responder.WaitAsync(Timeout);
    }

    [Fact]
    public async Task A_tait_devices_amateur_band_is_read_from_its_product_code()
    {
        using var pipe = new LoopbackRawPipe();
        // A 70cm radio's product code (H5 designator after the first '-').
        var responder = pipe.RespondTaitIdentityAsync(productCode: "TMAB12-H500_0201");
        var handler = new StubHeadEndHandler(new HeadEndInventory
        {
            InstanceId = "pi-shack",
            Ports = [new HeadEndPortInfo { Id = "tait0", TcpPort = pipe.Port, Baud = 28800, UsbVid = "10c4" }],
        });
        var discovery = new FakeHeadEndDiscovery(new DiscoveredHeadEnd("pi-shack", "127.0.0.1", 7300));

        var scan = await ScannerOver(handler, discovery).ScanAsync(ConfigWith()).WaitAsync(Timeout);

        var device = scan.Instances.Single().Devices.Single();
        device.Kind.Should().Be(HeadEndDeviceKind.TaitCcdi);
        device.BandCode.Should().Be("H5");
        device.AmateurBand.Should().Be("70cm");
        await responder.WaitAsync(Timeout);
    }

    [Fact]
    public async Task A_tait_device_is_swept_to_the_right_baud_before_it_identifies()
    {
        using var pipe = new LoopbackRawPipe();
        var handler = new StubHeadEndHandler(new HeadEndInventory
        {
            InstanceId = "pi-shack",
            Ports = [new HeadEndPortInfo { Id = "tait0", TcpPort = pipe.Port, Baud = 28800, UsbVid = "10c4" }],
        });
        // The loopback answers MODEL only once the head-end has been clocked to 19200 — so the scan
        // has to sweep off the (wrong) 28800 inventory clock before it identifies.
        var responder = pipe.RespondTaitIdentityAsync(shouldAnswer: () => handler.LastBaud == 19200);
        var discovery = new FakeHeadEndDiscovery(new DiscoveredHeadEnd("pi-shack", "127.0.0.1", 7300));

        var scan = await ScannerOver(handler, discovery, identify: TimeSpan.FromSeconds(1))
            .ScanAsync(ConfigWith()).WaitAsync(Timeout);

        var device = scan.Instances.Single().Devices.Single();
        device.Kind.Should().Be(HeadEndDeviceKind.TaitCcdi);
        device.Baud.Should().Be(19200, "the sweep clocked and identified in one step");
        // Open clocked 28800; the sweep then set 19200 (the first swept rate after the start baud).
        handler.LineCalls.Select(BaudOf).Should().ContainInOrder(28800, 19200);
        await responder.WaitAsync(Timeout);
    }

    [Fact]
    public async Task A_device_already_bound_to_a_configured_port_is_listed_but_not_probed()
    {
        using var freePipe = new LoopbackRawPipe();
        var responder = freePipe.RespondGetVerAsync("3.41");
        var handler = new StubHeadEndHandler(new HeadEndInventory
        {
            InstanceId = "pi-shack",
            Ports =
            [
                // A discard TCP port the scan must NEVER dial (it is bound to a running port).
                new HeadEndPortInfo { Id = "nino-bound", TcpPort = 9, Baud = 57600, UsbVid = "04d8" },
                new HeadEndPortInfo { Id = "nino-free", TcpPort = freePipe.Port, Baud = 57600, UsbVid = "04d8" },
            ],
        });
        var discovery = new FakeHeadEndDiscovery(new DiscoveredHeadEnd("pi-shack", "127.0.0.1", 7300));
        var config = ConfigWith(ports:
        [
            new PortConfig
            {
                Id = "p1",
                Transport = new NinoTncTcpTransport { HeadEndId = "pi-shack", DeviceId = "nino-bound" },
            },
        ]);

        var scan = await ScannerOver(handler, discovery).ScanAsync(config).WaitAsync(Timeout);

        var devices = scan.Instances.Single().Devices;
        devices.Should().HaveCount(2);
        var bound = devices.Single(d => d.DeviceId == "nino-bound");
        bound.Free.Should().BeFalse();
        bound.Kind.Should().Be(HeadEndDeviceKind.NinoTnc, "a bound device's role is known from the binding");
        devices.Single(d => d.DeviceId == "nino-free").Free.Should().BeTrue();
        await responder.WaitAsync(Timeout);
    }

    [Fact]
    public async Task Exactly_one_free_tnc_and_radio_yields_an_auto_pair()
    {
        using var ninoPipe = new LoopbackRawPipe();
        using var taitPipe = new LoopbackRawPipe();
        var ninoResp = ninoPipe.RespondGetVerAsync("3.41");
        var taitResp = taitPipe.RespondTaitIdentityAsync();
        var handler = new StubHeadEndHandler(new HeadEndInventory
        {
            InstanceId = "pi-shack",
            Ports =
            [
                new HeadEndPortInfo { Id = "nino0", TcpPort = ninoPipe.Port, Baud = 57600, UsbVid = "04d8" },
                new HeadEndPortInfo { Id = "tait0", TcpPort = taitPipe.Port, Baud = 28800, UsbVid = "10c4" },
            ],
        });
        var discovery = new FakeHeadEndDiscovery(new DiscoveredHeadEnd("pi-shack", "127.0.0.1", 7300));

        var scan = await ScannerOver(handler, discovery).ScanAsync(ConfigWith()).WaitAsync(Timeout);

        var instance = scan.Instances.Single();
        instance.PairingAmbiguous.Should().BeFalse();
        var pair = instance.ProposedPairs.Should().ContainSingle().Subject;
        pair.TncDeviceId.Should().Be("nino0");
        pair.RadioDeviceId.Should().Be("tait0");
        pair.Auto.Should().BeTrue();
        await ninoResp.WaitAsync(Timeout);
        await taitResp.WaitAsync(Timeout);
    }

    [Fact]
    public async Task More_than_one_free_tnc_makes_the_pairing_ambiguous()
    {
        using var ninoA = new LoopbackRawPipe();
        using var ninoB = new LoopbackRawPipe();
        using var taitPipe = new LoopbackRawPipe();
        var rA = ninoA.RespondGetVerAsync("3.41");
        var rB = ninoB.RespondGetVerAsync("3.41");
        var rT = taitPipe.RespondTaitIdentityAsync();
        var handler = new StubHeadEndHandler(new HeadEndInventory
        {
            InstanceId = "pi-shack",
            Ports =
            [
                new HeadEndPortInfo { Id = "ninoA", TcpPort = ninoA.Port, Baud = 57600, UsbVid = "04d8" },
                new HeadEndPortInfo { Id = "ninoB", TcpPort = ninoB.Port, Baud = 57600, UsbVid = "04d8" },
                new HeadEndPortInfo { Id = "tait0", TcpPort = taitPipe.Port, Baud = 28800, UsbVid = "10c4" },
            ],
        });
        var discovery = new FakeHeadEndDiscovery(new DiscoveredHeadEnd("pi-shack", "127.0.0.1", 7300));

        var scan = await ScannerOver(handler, discovery).ScanAsync(ConfigWith()).WaitAsync(Timeout);

        var instance = scan.Instances.Single();
        instance.PairingAmbiguous.Should().BeTrue();
        instance.ProposedPairs.Should().HaveCount(2);
        instance.ProposedPairs.Should().OnlyContain(p => !p.Auto && p.RadioDeviceId == "tait0");
        instance.ProposedPairs.Select(p => p.TncDeviceId).Should().BeEquivalentTo(["ninoA", "ninoB"]);
        await rA.WaitAsync(Timeout);
        await rB.WaitAsync(Timeout);
        await rT.WaitAsync(Timeout);
    }

    [Fact]
    public async Task Two_advertisers_with_the_same_instance_id_are_a_conflict_not_a_bind()
    {
        var handler = new StubHeadEndHandler(new HeadEndInventory { InstanceId = "pi-shack", Ports = [] });
        var discovery = new FakeHeadEndDiscovery(
            new DiscoveredHeadEnd("pi-shack", "192.168.1.9", 7300),
            new DiscoveredHeadEnd("pi-shack", "192.168.1.42", 7300));

        var scan = await ScannerOver(handler, discovery).ScanAsync(ConfigWith()).WaitAsync(Timeout);

        scan.Instances.Should().BeEmpty("a duplicate-id instance is not scanned — it is not bound");
        var conflict = scan.Conflicts.Should().ContainSingle().Subject;
        conflict.InstanceId.Should().Be("pi-shack");
        conflict.Addresses.Should().BeEquivalentTo(["192.168.1.9:7300", "192.168.1.42:7300"]);
    }

    [Fact]
    public async Task A_config_pinned_address_wins_over_a_discovered_one()
    {
        var handler = new StubHeadEndHandler(new HeadEndInventory { InstanceId = "pi-shack", Ports = [] });
        var discovery = new FakeHeadEndDiscovery(new DiscoveredHeadEnd("pi-shack", "192.168.1.99", 7300));
        var config = ConfigWith(headEnds: [new HeadEndConfig { Id = "pi-shack", Address = "10.0.0.5:7300" }]);

        var scan = await ScannerOver(handler, discovery).ScanAsync(config).WaitAsync(Timeout);

        var instance = scan.Instances.Should().ContainSingle().Subject;
        instance.Source.Should().Be("config");
        instance.Host.Should().Be("10.0.0.5");
        instance.HttpPort.Should().Be(7300);
    }
}
