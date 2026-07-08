using System.Text;
using Packet.Node.Core.Configuration;
using Packet.Node.Core.HeadEnd;
using Packet.Node.Core.Radios;
using Packet.Node.Tests.Support;
using Packet.Radio;
using Packet.Radio.Tait;

namespace Packet.Node.Tests.HeadEnd;

/// <summary>
/// Reconnect supervision for the head-end-bound radio-control channel (#576,
/// <see cref="ReconnectingRadioControl"/>): when the control socket dies (a head-end bounce — the
/// exact effect of a routine <c>.deb</c> upgrade's <c>try-restart</c>), the facade disposes the
/// dead driver and re-opens through the factory — re-resolving the head-end inventory (so a
/// moved raw-pipe TCP port is found), re-clocking the line to the CONFIGURED CCDI rate, and
/// re-enabling unsolicited PROGRESS — while every consumer that holds the facade
/// (carrier-sense gate, RSSI sampler, events) keeps working across the swap.
/// </summary>
[Trait("Category", "Node")]
public sealed class ReconnectingRadioControlTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task A_head_end_bounce_reopens_the_control_channel_with_a_fresh_resolve_and_consumers_survive_the_swap()
    {
        using var pipe1 = new LoopbackRawPipe();
        using var pipe2 = new LoopbackRawPipe();
        var received2 = new StringBuilder();
        var responder1 = pipe1.RespondCcdiPromptsAsync();
        var responder2 = pipe2.RespondCcdiPromptsAsync(received2);

        // The inventory is LIVE: after the bounce the device re-enumerates on a different
        // raw-pipe TCP port (pipe2) — only a fresh resolve can find it.
        int currentPort = pipe1.Port;
        var handler = new StubHeadEndHandler(() => new HeadEndInventory
        {
            InstanceId = "pi-shack",
            Ports =
            [
                new HeadEndPortInfo { Id = "tait0", TcpPort = Volatile.Read(ref currentPort), Baud = 9600 },
            ],
        });
        var headEnds = new[] { new HeadEndConfig { Id = "pi-shack", Address = "127.0.0.1:7300" } };
        HeadEndDeviceResolver Resolver() => new(
            headEnds,
            _ => new HeadEndClient(new Uri("http://127.0.0.1:7300/"), new HttpClient(handler)));

        var config = new PortRadioConfig
        {
            Kind = "tait-ccdi", HeadEndId = "pi-shack", DeviceId = "tait0", Baud = 28800,
        };
        var initial = await RadioControlFactory.Instance.CreateAsync(config, timeProvider: null, Resolver());

        await using var facade = new ReconnectingRadioControl(
            initial, "port-a", config, RadioControlFactory.Instance, Resolver,
            logger: null, timeProvider: null,
            minBackoff: TimeSpan.FromMilliseconds(50), maxBackoff: TimeSpan.FromMilliseconds(200));

        // Consumers hold the FACADE for the port's whole life — exactly as the supervisor wires them.
        var gate = new RadioCarrierSense(facade);
        var edges = new List<bool>();
        facade.CarrierSenseChanged += (_, e) =>
        {
            lock (edges)
            {
                edges.Add(e.Busy);
            }
        };
        var innerBefore = facade.Inner;

        _ = await pipe1.Accepted.WaitAsync(Timeout);

        // The bounce: the device comes back on a different raw-pipe port, and the old socket dies.
        Volatile.Write(ref currentPort, pipe2.Port);
        pipe1.Kill();

        // The facade reopens: fresh inventory resolve → dials pipe2 → line re-clock → progress-on.
        _ = await pipe2.Accepted.WaitAsync(Timeout);
        await Wait.ForAsync(() => !ReferenceEquals(facade.Inner, innerBefore), "the facade swapped in a fresh driver");

        handler.InventoryFetches.Should().BeGreaterThanOrEqualTo(2,
            "each reopen must re-resolve the head-end inventory, not re-dial a cached binding");
        handler.LineCalls.Should().HaveCountGreaterThanOrEqualTo(2, "each open re-clocks the line");
        handler.LineCalls.Should().OnlyContain(
            c => c.RawBody.Contains("\"baud\":28800", StringComparison.Ordinal),
            "every re-clock uses the CONFIGURED CCDI rate, never the head-end's stale current rate (#576)");
        await Wait.ForAsync(
            () => { lock (received2) { return received2.ToString().Contains("f03041", StringComparison.Ordinal); } },
            "the reopen re-enabled unsolicited PROGRESS output (DCD events)");

        // Consumers still functional after the swap: a DCD edge pushed on the NEW pipe reaches
        // both the facade's event stream and the carrier-sense gate reading through it.
        await pipe2.SendAsync(".p0205C9\r.");
        await Wait.ForAsync(() => gate.ChannelBusy == true, "the CSMA gate reads live DCD through the facade post-swap");
        lock (edges)
        {
            edges.Should().Contain(true, "the facade re-raises the fresh driver's carrier-sense edges");
        }
        RadioControls.LiveTait(facade).Should().NotBeNull("downcast sites resolve the live driver through the facade");
        RadioControls.LiveTait(facade)!.ConnectionState.Should().Be(TaitConnectionState.Healthy);

        await responder1;
        _ = responder2;
    }

    [Fact]
    public async Task Disposing_the_facade_disposes_the_live_inner_driver()
    {
        using var pipe = new LoopbackRawPipe();
        var responder = pipe.RespondCcdiPromptsAsync();

        var handler = new StubHeadEndHandler(new HeadEndInventory
        {
            InstanceId = "pi-shack",
            Ports = [new HeadEndPortInfo { Id = "tait0", TcpPort = pipe.Port, Baud = 28800 }],
        });
        var headEnds = new[] { new HeadEndConfig { Id = "pi-shack", Address = "127.0.0.1:7300" } };
        HeadEndDeviceResolver Resolver() => new(
            headEnds,
            _ => new HeadEndClient(new Uri("http://127.0.0.1:7300/"), new HttpClient(handler)));

        var config = new PortRadioConfig { Kind = "tait-ccdi", HeadEndId = "pi-shack", DeviceId = "tait0" };
        var initial = await RadioControlFactory.Instance.CreateAsync(config, timeProvider: null, Resolver());
        var facade = new ReconnectingRadioControl(
            initial, "port-a", config, RadioControlFactory.Instance, Resolver);

        await facade.DisposeAsync();

        var act = async () => await facade.ReadRssiDbmAsync();
        await act.Should().ThrowAsync<ObjectDisposedException>("disposing the facade disposes the inner driver");
        await responder;
    }
}
