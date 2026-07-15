using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Packet.Ax25.Transport;
using Packet.Kiss;
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
        // The TNC on the far end answers GETALL with "running mode 6" — bring-up SETHWs mode 6 and,
        // since #633, verifies it took through that readback rather than trusting the unacknowledged
        // SETHW (a node silently left in the wrong mode is deaf and mute, for no visible reason).
        var responder = pipe.RespondNinoTncGetAllAsync(runningMode: 6);

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
            ((NinoTncSerialPort)created).CurrentMode.Should().Be((byte)6, "the configured mode is verified applied at bring-up");
            _ = await pipe.Accepted.WaitAsync(Timeout);
        }
        await responder;

        // #567: a NinoTNC's KISS baud is a fixed 57600 — bring-up clocks the head-end line to it before
        // opening the pipe (the raw socket cannot carry line rate).
        handler.LineCalls.Should().ContainSingle();
        handler.LineCalls[0].DeviceId.Should().Be("nino0");
        handler.LineCalls[0].RawBody.Should().Contain("\"baud\":57600");
    }

    [Fact]
    public async Task Head_end_bound_tait_transparent_opens_the_pipe_and_rides_the_line_verb_for_every_reclock()
    {
        using var pipe = new LoopbackRawPipe();
        var responder = pipe.RespondCcdiPromptsAsync();   // answer the Transparent entry `t` with a prompt

        // The head-end's CURRENT line rate is stale (bridge reopened at its default after a
        // restart); the radio is programmed for the configured 28800 command / 19200 transparent.
        var handler = new StubHeadEndHandler(new HeadEndInventory
        {
            InstanceId = "pi-shack",
            Ports = [new HeadEndPortInfo { Id = "tait0", TcpPort = pipe.Port, Baud = 9600 }],
        });
        var resolver = ResolverOver(handler, "pi-shack");

        var transport = new TaitTransparentTransportConfig
        {
            HeadEndId = "pi-shack",
            DeviceId = "tait0",
            Baud = 28800,
            TransparentBaud = 19200,
        };

        var created = await TransportFactory.Instance.CreateAsync(transport, timeProvider: null, resolver);
        await using (created)
        {
            created.Should().BeOfType<TaitTransparentTransport>("the radio IS the modem — no KISS TNC in the path");
            _ = await pipe.Accepted.WaitAsync(Timeout);

            // The Command↔Transparent runtime re-clock rides the head-end line verb (#585): the
            // data socket is a pure binary pipe, so (1) open clocks the CONFIGURED CCDI command
            // baud (#576's configured-baud convention — never the inventory's stale rate), then
            // (2) entering Transparent re-clocks to the transparent terminal rate.
            handler.LineCalls.Should().HaveCount(2);
            handler.LineCalls.Should().OnlyContain(c => c.DeviceId == "tait0");
            handler.LineCalls[0].RawBody.Should().Contain("\"baud\":28800");
            handler.LineCalls[1].RawBody.Should().Contain("\"baud\":19200");
        }

        // (3) Teardown escapes Transparent (the ~4 s §1.7.2 dance) and restores the command baud
        // — the exit leg of the runtime re-clock, also through the line verb.
        handler.LineCalls.Should().HaveCount(3);
        handler.LineCalls[2].RawBody.Should().Contain("\"baud\":28800");

        await responder;
    }

    [Fact]
    public async Task A_head_end_bound_tait_transparent_with_no_resolver_fails_clearly()
    {
        var transport = new TaitTransparentTransportConfig { HeadEndId = "pi-shack", DeviceId = "tait0" };

        var act = () => TransportFactory.Instance.CreateAsync(transport, timeProvider: null, headEndResolver: null);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*resolver*");
    }

    [Fact]
    public async Task A_dropped_transparent_pipe_reconnects_through_a_fresh_resolve_and_resumes_receiving()
    {
        // The supervision story (#585): the transparent transport IS the port's transport, so a
        // head-end bounce is handled by the same ReconnectingKissModem wrap as nino-tnc-tcp — the
        // dead pipe faults the radio, ReceiveAsync ENDS, and the wrapper re-runs the factory arm
        // (fresh inventory resolve, so a moved raw-pipe TCP port is found) and resumes the stream.
        using var pipeA = new LoopbackRawPipe();
        using var pipeB = new LoopbackRawPipe();
        var receivedB = new StringBuilder();
        var responderA = pipeA.RespondCcdiPromptsAsync();
        var responderB = pipeB.RespondCcdiPromptsAsync(receivedB);

        int currentPort = pipeA.Port;
        var handler = new StubHeadEndHandler(() => new HeadEndInventory
        {
            InstanceId = "pi-shack",
            Ports = [new HeadEndPortInfo { Id = "tait0", TcpPort = currentPort, Baud = 28800 }],
        });
        var resolver = ResolverOver(handler, "pi-shack");

        var config = new TaitTransparentTransportConfig { HeadEndId = "pi-shack", DeviceId = "tait0" };
        Task<IAx25Transport> Create(CancellationToken ct) =>
            TransportFactory.Instance.CreateAsync(config, timeProvider: null, resolver, ct);

        var initial = await Create(CancellationToken.None);
        await using var supervised = new ReconnectingKissModem(
            initial, Create, config.DescribeEndpoint(),
            NullLogger.Instance,
            minBackoff: TimeSpan.FromMilliseconds(50), maxBackoff: TimeSpan.FromMilliseconds(200));

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var firstFrame = FirstFrameAsync(supervised, cts.Token);

        // The head-end bounces: the pipe dies and the device comes back on a NEW TCP port.
        _ = await pipeA.Accepted.WaitAsync(Timeout);
        currentPort = pipeB.Port;
        pipeA.Kill();

        // Once the reconnect has dialled pipe B and re-entered Transparent, push one SLIP frame
        // from the head-end side — it must surface on the SAME (never-torn-down) stream.
        _ = await pipeB.Accepted.WaitAsync(cts.Token);
        while (true)   // wait for the reconnect's Transparent entry command to reach pipe B
        {
            lock (receivedB)
            {
                if (receivedB.ToString().Contains('t'))
                {
                    break;
                }
            }
            await Task.Delay(25, cts.Token);
        }
        await Task.Delay(400, cts.Token);   // let the entry transaction settle (prompt + grace)
        byte[] slip = KissEncoder.Encode(0, KissCommand.Data, [0x01, 0x02, 0x03]);
        await pipeB.SendAsync(Encoding.Latin1.GetString(slip));

        (await firstFrame).Ax25.ToArray().Should().Equal(0x01, 0x02, 0x03);
        handler.InventoryFetches.Should().BeGreaterThanOrEqualTo(2, "the reconnect re-resolves the inventory, not a cached binding");

        await responderA;
    }

    private static async Task<Ax25InboundFrame> FirstFrameAsync(
        ReconnectingKissModem transport, CancellationToken ct)
    {
        await foreach (var frame in transport.ReceiveAsync(ct))
        {
            return frame;
        }
        throw new InvalidOperationException("the stream ended without a frame");
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
