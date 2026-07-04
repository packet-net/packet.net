using AwesomeAssertions;
using Microsoft.Extensions.Time.Testing;
using Packet.Ax25.Session;
using Packet.Ax25.Transport;
using Xunit;

namespace Packet.Ax25.Tests.Session;

/// <summary>
/// The native carrier-sense CSMA gate (<see cref="CarrierSenseGate"/>, OQ-012) at the AX.25
/// link-multiplexer's transmit path: it holds a keyup while the channel is busy and releases
/// it when the channel clears (or a bounded wait expires — fail-open). The clear / no-source /
/// unknown paths must key up immediately so a stack with no carrier-sense wired is unchanged.
/// </summary>
public class CarrierSenseGateTests
{
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(2);

    /// <summary>A scripted <see cref="ICarrierSense"/> whose busy state the test flips.</summary>
    private sealed class FakeCarrierSense : ICarrierSense
    {
        public bool? ChannelBusy { get; set; }
    }

    [Fact]
    public async Task No_source_keys_up_immediately()
    {
        var gate = new CarrierSenseGate(source: null, new FakeTimeProvider());

        var wait = gate.WaitForClearAsync();

        gate.HasSource.Should().BeFalse("the always-clear degenerate gate has no source");
        wait.IsCompleted.Should().BeTrue("no source ⇒ synchronous completion (no async hop)");
        (await wait).Should().Be(TimeSpan.Zero);
    }

    [Fact]
    public async Task Clear_channel_keys_up_immediately()
    {
        var gate = new CarrierSenseGate(new FakeCarrierSense { ChannelBusy = false }, new FakeTimeProvider());

        var wait = gate.WaitForClearAsync();

        wait.IsCompleted.Should().BeTrue();
        (await wait).Should().Be(TimeSpan.Zero);
    }

    [Fact]
    public async Task Unknown_busy_state_fails_open_immediately()
    {
        // null = "no report yet / cannot sense" — must not wedge traffic.
        var gate = new CarrierSenseGate(new FakeCarrierSense { ChannelBusy = null }, new FakeTimeProvider());

        var wait = gate.WaitForClearAsync();

        wait.IsCompleted.Should().BeTrue("unknown fails open");
        (await wait).Should().Be(TimeSpan.Zero);
    }

    [Fact]
    public async Task Busy_channel_defers_until_the_carrier_clears()
    {
        var time = new FakeTimeProvider();
        var cs = new FakeCarrierSense { ChannelBusy = true };
        var gate = new CarrierSenseGate(cs, time, new CarrierSenseGateOptions { SlotTime = TimeSpan.FromMilliseconds(100) });

        var task = gate.WaitForClearAsync().AsTask();

        await Task.Delay(50);
        task.IsCompleted.Should().BeFalse("the channel is busy — the keyup is held");

        cs.ChannelBusy = false;
        time.Advance(TimeSpan.FromMilliseconds(100));   // one slot: re-sample carrier-sense

        var waited = await task.WaitAsync(Budget);
        waited.Should().BeGreaterThan(TimeSpan.Zero, "the transmission waited for the channel to clear");
    }

    [Fact]
    public async Task Bounded_wait_expiry_fails_open_by_default()
    {
        var time = new FakeTimeProvider();
        var cs = new FakeCarrierSense { ChannelBusy = true };   // never clears
        var gate = new CarrierSenseGate(cs, time, new CarrierSenseGateOptions
        {
            SlotTime = TimeSpan.FromMilliseconds(50),
            MaxWait = TimeSpan.FromMilliseconds(50),
        });

        var task = gate.WaitForClearAsync().AsTask();
        await Task.Delay(50);
        task.IsCompleted.Should().BeFalse("still holding — the channel has not cleared");

        time.Advance(TimeSpan.FromMilliseconds(50));   // one slot past MaxWait

        var waited = await task.WaitAsync(Budget);
        waited.Should().BeGreaterThanOrEqualTo(TimeSpan.FromMilliseconds(50),
            "fail-open keys up after the bounded wait rather than dropping the frame");
    }

    [Fact]
    public async Task Bounded_wait_expiry_throws_when_fail_open_disabled()
    {
        var time = new FakeTimeProvider();
        var cs = new FakeCarrierSense { ChannelBusy = true };
        var gate = new CarrierSenseGate(cs, time, new CarrierSenseGateOptions
        {
            SlotTime = TimeSpan.FromMilliseconds(50),
            MaxWait = TimeSpan.FromMilliseconds(50),
            FailOpen = false,
        });

        var task = gate.WaitForClearAsync().AsTask();
        await Task.Delay(50);
        time.Advance(TimeSpan.FromMilliseconds(50));

        var act = async () => await task.WaitAsync(Budget);
        await act.Should().ThrowAsync<TimeoutException>("fail-open disabled surfaces the busy-channel timeout");
    }
}
