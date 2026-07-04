using Microsoft.Extensions.Time.Testing;

// CarrierSenseTxGate is [Obsolete] (superseded by the native CarrierSenseGate seam, OQ-012) but
// still supported as a degenerate fallback, so its behaviour stays under test. Suppress CS0618
// for this whole file — the deprecation is a design signpost, not a reason to drop coverage.
#pragma warning disable CS0618

namespace Packet.Radio.Tests;

public class CarrierSenseTxGateTests
{
    [Fact]
    public async Task Clear_Channel_Sends_Immediately()
    {
        var time = new FakeTimeProvider();
        var radio = new FakeRadio { ChannelBusy = false };
        var transport = new FakeTransport();
        await using var gate = new CarrierSenseTxGate(transport, radio, timeProvider: time);

        await gate.SendAsync(new byte[10]);

        transport.Sent.Should().HaveCount(1);
    }

    [Fact]
    public async Task Unknown_Busy_State_Fails_Open()
    {
        var time = new FakeTimeProvider();
        var radio = new FakeRadio { ChannelBusy = null };
        var transport = new FakeTransport();
        await using var gate = new CarrierSenseTxGate(transport, radio, timeProvider: time);

        await gate.SendAsync(new byte[10]);

        transport.Sent.Should().HaveCount(1, "telemetry loss must never stop traffic");
    }

    [Fact]
    public async Task Busy_Channel_Defers_Until_Carrier_Clears()
    {
        var time = new FakeTimeProvider();
        var radio = new FakeRadio();
        var transport = new FakeTransport();
        await using var gate = new CarrierSenseTxGate(transport, radio, timeProvider: time);
        var deferrals = new List<TimeSpan>();
        gate.TransmissionDeferred += (_, waited) => deferrals.Add(waited);

        radio.RaiseCarrierSense(true, time.GetUtcNow());
        var send = gate.SendAsync(new byte[10]);

        await Task.Delay(50);
        send.IsCompleted.Should().BeFalse("the channel is busy");
        transport.Sent.Should().BeEmpty();

        time.Advance(TimeSpan.FromSeconds(2));
        radio.RaiseCarrierSense(false, time.GetUtcNow());
        await send.WaitAsync(TimeSpan.FromSeconds(5));

        transport.Sent.Should().HaveCount(1);
        deferrals.Should().ContainSingle().Which.Should().Be(TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task MaxWait_Expiry_Fails_Open_By_Default()
    {
        var time = new FakeTimeProvider();
        var radio = new FakeRadio();
        var transport = new FakeTransport();
        var options = new CarrierSenseTxGateOptions { MaxWait = TimeSpan.FromSeconds(3) };
        await using var gate = new CarrierSenseTxGate(transport, radio, options, time);

        radio.RaiseCarrierSense(true, time.GetUtcNow());
        var send = gate.SendAsync(new byte[10]);
        await Task.Delay(50);

        time.Advance(TimeSpan.FromSeconds(3.5));
        await send.WaitAsync(TimeSpan.FromSeconds(5));

        transport.Sent.Should().HaveCount(1, "fail-open sends after the bounded wait");
    }

    [Fact]
    public async Task MaxWait_Expiry_Throws_When_FailOpen_Disabled()
    {
        var time = new FakeTimeProvider();
        var radio = new FakeRadio();
        var transport = new FakeTransport();
        var options = new CarrierSenseTxGateOptions { MaxWait = TimeSpan.FromSeconds(3), FailOpen = false };
        await using var gate = new CarrierSenseTxGate(transport, radio, options, time);

        radio.RaiseCarrierSense(true, time.GetUtcNow());
        var send = gate.SendAsync(new byte[10]);
        await Task.Delay(50);

        time.Advance(TimeSpan.FromSeconds(3.5));

        await send.Invoking(t => t.WaitAsync(TimeSpan.FromSeconds(5)))
            .Should().ThrowAsync<TimeoutException>();
        transport.Sent.Should().BeEmpty();
    }
}
