using Packet.Kiss.NinoTnc;
using Packet.Node.Core.Configuration;
using Packet.Node.Core.HeadEnd;
using Packet.Node.Core.Radios;
using Packet.Node.Core.Transports;
using Packet.Node.Tests.Support;
using Packet.Radio.Tait;

namespace Packet.Node.Tests.HeadEnd;

/// <summary>
/// The head-end (split-station) branches of the real <see cref="RadioControlFactory"/> and
/// <see cref="TransportFactory"/>: resolve a device via a stub inventory server, dial the raw TCP
/// pipe (a loopback listener), and — for the Tait radio — route <c>setBaud</c> to the head-end's
/// <c>POST /ports/{id}/line</c> verb. Plus the resolution-failure paths.
/// </summary>
[Trait("Category", "Node")]
public sealed class HeadEndFactoryTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    // A resolver whose HeadEndClient talks to the stub HTTP handler but whose base-address host is
    // the loopback the raw pipe listens on (so the resolved dial host is 127.0.0.1).
    private static HeadEndDeviceResolver ResolverOver(StubHeadEndHandler handler, string headEndId)
    {
        var headEnds = new[] { new HeadEndConfig { Id = headEndId, Address = "127.0.0.1:7300" } };
        return new HeadEndDeviceResolver(
            headEnds,
            _ => new HeadEndClient(new Uri("http://127.0.0.1:7300/"), new HttpClient(handler)));
    }

    [Fact]
    public async Task Head_end_bound_tait_resolves_the_inventory_opens_the_pipe_and_clocks_the_configured_rate()
    {
        using var pipe = new LoopbackRawPipe();
        var responder = pipe.RespondCcdiPromptsAsync();   // answer the progress-enable with a prompt

        // The head-end's CURRENT line rate is stale (its bridge reopened at the 9600 default —
        // e.g. after a head-end restart); the radio itself is programmed for the configured 28800.
        var handler = new StubHeadEndHandler(new HeadEndInventory
        {
            InstanceId = "pi-shack",
            Ports = [new HeadEndPortInfo { Id = "tait0", TcpPort = pipe.Port, Baud = 9600 }],
        });
        var resolver = ResolverOver(handler, "pi-shack");

        var radio = new PortRadioConfig { Kind = "tait-ccdi", HeadEndId = "pi-shack", DeviceId = "tait0", Baud = 28800 };

        var control = await RadioControlFactory.Instance.CreateAsync(radio, timeProvider: null, resolver);
        await using (control)
        {
            control.Should().BeOfType<TaitCcdiRadio>();
            _ = await pipe.Accepted.WaitAsync(Timeout);

            // setBaud fired at OpenTcp → POST /ports/tait0/line with the CONFIGURED CCDI rate (#576):
            // passing the inventory's current rate would "re-clock" the port to the rate it is
            // already at, so a restarted head-end (bridge back at its default) would never recover.
            handler.LineCalls.Should().ContainSingle();
            handler.LineCalls[0].DeviceId.Should().Be("tait0");
            handler.LineCalls[0].RawBody.Should().Contain("\"baud\":28800");
        }

        await responder;
    }

    [Fact]
    public async Task Head_end_nino_tnc_tcp_resolves_the_inventory_and_opens_the_full_control_pipe()
    {
        using var pipe = new LoopbackRawPipe();

        var handler = new StubHeadEndHandler(new HeadEndInventory
        {
            InstanceId = "pi-shack",
            Ports = [new HeadEndPortInfo { Id = "nino0", TcpPort = pipe.Port, Baud = 57600 }],
        });
        var resolver = ResolverOver(handler, "pi-shack");

        var transport = new NinoTncTcpTransport { HeadEndId = "pi-shack", DeviceId = "nino0", Mode = 6 };

        var created = await TransportFactory.Instance.CreateAsync(transport, timeProvider: null, resolver);
        await using (created)
        {
            created.Should().BeOfType<NinoTncSerialPort>("nino-tnc-tcp is the full-control NinoTNC path, not a bare KISS pipe");
            _ = await pipe.Accepted.WaitAsync(Timeout);
        }

        // #567: a NinoTNC's KISS baud is a fixed 57600 — bring-up clocks the head-end line to it before
        // opening the pipe (the raw socket cannot carry line rate).
        handler.LineCalls.Should().ContainSingle();
        handler.LineCalls[0].DeviceId.Should().Be("nino0");
        handler.LineCalls[0].RawBody.Should().Contain("\"baud\":57600");
    }

    [Fact]
    public async Task An_unknown_head_end_id_throws_at_resolve()
    {
        var resolver = new HeadEndDeviceResolver([]);   // no head-ends configured
        var transport = new NinoTncTcpTransport { HeadEndId = "ghost", DeviceId = "nino0" };

        var act = () => TransportFactory.Instance.CreateAsync(transport, timeProvider: null, resolver);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*ghost*");
    }

    [Fact]
    public async Task An_unknown_device_id_throws_at_resolve()
    {
        var handler = new StubHeadEndHandler(new HeadEndInventory
        {
            InstanceId = "pi-shack",
            Ports = [new HeadEndPortInfo { Id = "nino0", TcpPort = 9, Baud = 57600 }],
        });
        var resolver = ResolverOver(handler, "pi-shack");
        var transport = new NinoTncTcpTransport { HeadEndId = "pi-shack", DeviceId = "not-there" };

        var act = () => TransportFactory.Instance.CreateAsync(transport, timeProvider: null, resolver);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*not-there*");
    }

    [Fact]
    public async Task A_head_end_bound_radio_with_no_resolver_fails_clearly()
    {
        var radio = new PortRadioConfig { Kind = "tait-ccdi", HeadEndId = "pi-shack", DeviceId = "tait0" };

        var act = () => RadioControlFactory.Instance.CreateAsync(radio, timeProvider: null, headEndResolver: null);

        await act.Should().ThrowAsync<InvalidOperationException>();
    }
}
