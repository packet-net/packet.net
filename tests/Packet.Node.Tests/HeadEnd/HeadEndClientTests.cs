using System.Text.Json;
using Packet.Node.Core.HeadEnd;
using Packet.Node.Tests.Support;

namespace Packet.Node.Tests.HeadEnd;

/// <summary>
/// <see cref="HeadEndClient"/> against a stub head-end HTTP control plane
/// (<see cref="StubHeadEndHandler"/>): inventory parse, the line-control POST (body shape + effective
/// params), and the health probe. The base address is injectable and the HttpClient substitutable, so
/// no real socket is opened.
/// </summary>
[Trait("Category", "Node")]
public sealed class HeadEndClientTests
{
    private static readonly JsonSerializerOptions WebJson = new(JsonSerializerDefaults.Web);

    private static HeadEndClient ClientOver(StubHeadEndHandler handler) =>
        new(new Uri("http://headend.test:7300/"), new HttpClient(handler));

    [Fact]
    public async Task GetInventoryAsync_parses_the_instance_id_and_every_port()
    {
        var inventory = new HeadEndInventory
        {
            InstanceId = "pi-shack",
            Ports =
            [
                new HeadEndPortInfo { Id = "nino0", DevPath = "/dev/ttyACM0", UsbVid = "1209", UsbPid = "7388", TcpPort = 8001, Baud = 57600, DataBits = 8, Parity = "none", StopBits = 1 },
                new HeadEndPortInfo { Id = "tait0", DevPath = "/dev/ttyUSB0", TcpPort = 8002, Baud = 28800, DataBits = 8, Parity = "none", StopBits = 1 },
            ],
        };

        var result = await ClientOver(new StubHeadEndHandler(inventory)).GetInventoryAsync();

        result.InstanceId.Should().Be("pi-shack");
        result.Ports.Should().HaveCount(2);
        result.Ports[0].Id.Should().Be("nino0");
        result.Ports[0].TcpPort.Should().Be(8001);
        result.Ports[1].Id.Should().Be("tait0");
        result.Ports[1].Baud.Should().Be(28800);
    }

    [Fact]
    public async Task GetInventoryAsync_parses_the_id_stability_fields_when_reported()
    {
        var inventory = new HeadEndInventory
        {
            InstanceId = "pi-shack",
            Ports =
            [
                new HeadEndPortInfo { Id = "platform-xhci-usb-0", IdSource = "by-path", IdStable = true, TcpPort = 8001, Baud = 57600 },
                new HeadEndPortInfo { Id = "ttyUSB1", IdSource = "dev", IdStable = false, TcpPort = 8002, Baud = 28800 },
            ],
        };

        var result = await ClientOver(new StubHeadEndHandler(inventory)).GetInventoryAsync();

        result.Ports[0].IdSource.Should().Be("by-path");
        result.Ports[0].IdStable.Should().BeTrue();
        result.Ports[1].IdSource.Should().Be("dev");
        result.Ports[1].IdStable.Should().BeFalse();
    }

    [Fact]
    public void An_inventory_without_the_id_stability_fields_reads_as_unknown_not_stable()
    {
        // A head-end < v0.1.3 doesn't emit idSource/idStable. Absent must deserialize to NULL
        // (unknown) — deliberately NOT assumed stable, so old head-ends neither warn nor reassure.
        const string json = """
            {"instanceId":"old-pi","ports":[{"id":"usb-0","devPath":"/dev/ttyUSB0","tcpPort":8001,"baud":57600,"dataBits":8,"parity":"none","stopBits":1}]}
            """;

        var inventory = JsonSerializer.Deserialize<HeadEndInventory>(json, WebJson)!;

        var port = inventory.Ports.Should().ContainSingle().Subject;
        port.Id.Should().Be("usb-0");
        port.IdSource.Should().BeNull("an old head-end didn't report it — unknown, not defaulted");
        port.IdStable.Should().BeNull("absent is unknown, never assumed stable");
    }

    [Fact]
    public async Task SetLineAsync_posts_the_baud_omits_unset_params_and_parses_effective()
    {
        var handler = new StubHeadEndHandler(new HeadEndInventory());

        var effective = await ClientOver(handler).SetLineAsync("tait0", 19200);

        handler.LineCalls.Should().ContainSingle();
        handler.LineCalls[0].DeviceId.Should().Be("tait0");
        handler.LineCalls[0].RawBody.Should().Contain("\"baud\":19200");
        // Unset optional params are dropped on the wire (head-end reads absent as "unchanged").
        handler.LineCalls[0].RawBody.Should().NotContain("dataBits");
        handler.LineCalls[0].RawBody.Should().NotContain("parity");
        handler.LineCalls[0].RawBody.Should().NotContain("stopBits");

        effective.Baud.Should().Be(19200);
        effective.DataBits.Should().Be(8);
        effective.Parity.Should().Be("none");
        effective.StopBits.Should().Be(1);
    }

    [Fact]
    public async Task SetLineAsync_includes_optional_params_when_supplied()
    {
        var handler = new StubHeadEndHandler(new HeadEndInventory());

        var effective = await ClientOver(handler).SetLineAsync("tait0", 9600, dataBits: 7, parity: "even", stopBits: 2);

        handler.LineCalls[0].RawBody.Should().Contain("\"baud\":9600");
        handler.LineCalls[0].RawBody.Should().Contain("\"dataBits\":7");
        handler.LineCalls[0].RawBody.Should().Contain("\"parity\":\"even\"");
        handler.LineCalls[0].RawBody.Should().Contain("\"stopBits\":2");

        effective.Parity.Should().Be("even");
        effective.DataBits.Should().Be(7);
        effective.StopBits.Should().Be(2);
    }

    [Fact]
    public async Task HealthAsync_is_true_on_200_and_false_on_error()
    {
        (await ClientOver(new StubHeadEndHandler(new HeadEndInventory(), healthy: true)).HealthAsync())
            .Should().BeTrue();
        (await ClientOver(new StubHeadEndHandler(new HeadEndInventory(), healthy: false)).HealthAsync())
            .Should().BeFalse();
    }

    [Fact]
    public async Task GetStatusAsync_parses_the_instance_id_bridge_count_and_per_bridge_state()
    {
        var handler = new StubHeadEndHandler(new HeadEndInventory())
        {
            Status = () => new HeadEndStatus
            {
                InstanceId = "pi-shack",
                BridgeCount = 2,
                Bridges =
                [
                    new HeadEndBridgeStatus { Id = "nino0", TcpPort = 8001, ClientConnected = true },
                    new HeadEndBridgeStatus { Id = "tait0", TcpPort = 8002, ClientConnected = false },
                ],
            },
        };

        var status = await ClientOver(handler).GetStatusAsync();

        status.InstanceId.Should().Be("pi-shack");
        status.BridgeCount.Should().Be(2);
        status.Bridges.Should().HaveCount(2);
        status.Bridges[0].Id.Should().Be("nino0");
        status.Bridges[0].ClientConnected.Should().BeTrue();
        status.Bridges[1].TcpPort.Should().Be(8002);
        status.Bridges[1].ClientConnected.Should().BeFalse();
    }

    [Fact]
    public async Task GetStatusAsync_surfaces_a_404_from_a_pre_statusz_daemon_as_NotFound()
    {
        // Status left null → the stub 404s /statusz like a ≤0.1.3 daemon. The health poller keys
        // its healthz fallback off exactly this StatusCode.
        var act = () => ClientOver(new StubHeadEndHandler(new HeadEndInventory())).GetStatusAsync();

        (await act.Should().ThrowAsync<HttpRequestException>())
            .Which.StatusCode.Should().Be(System.Net.HttpStatusCode.NotFound);
    }
}
