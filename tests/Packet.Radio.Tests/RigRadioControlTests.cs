using Microsoft.Extensions.Time.Testing;
using Packet.Rig;

namespace Packet.Radio.Tests;

/// <summary>
/// The rig→radio bridge: capability mapping, poll-synthesized carrier-sense edges, fail-open
/// fault handling with the slower retry cadence, RSSI/PTT delegation, and the
/// disposal/ownership contracts. All timing on a <see cref="FakeTimeProvider"/>.
/// </summary>
public sealed class RigRadioControlTests
{
    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        // Bounded poll for cross-task visibility (the loop runs on a Task.Run) — the fake clock
        // drives *when* ticks happen; this only waits for the tick's writes to land.
        for (var i = 0; i < 500 && !condition(); i++)
        {
            await Task.Delay(10);
        }
        condition().Should().BeTrue("the poll loop should have reached the expected state");
    }

    // Real-time cushion between "the sample's writes are visible" and advancing the fake clock:
    // the loop must reach its Task.Delay for the advance to land on it.
    private static Task LetThePollLoopParkAsync() => Task.Delay(25);

    [Fact]
    public async Task Full_Rig_Maps_To_CarrierSense_Rssi_And_Transmitter()
    {
        await using var radio = new RigRadioControl(new FakeRig(), timeProvider: new FakeTimeProvider());

        radio.Capabilities.Should().Be(
            RadioCapabilities.CarrierSense | RadioCapabilities.RssiRead | RadioCapabilities.TransmitterControl);
    }

    [Fact]
    public async Task Dcd_Only_Rig_Maps_To_CarrierSense_Only()
    {
        await using var radio = new RigRadioControl(
            new FakeRig { Capabilities = RigCapabilities.DcdRead }, timeProvider: new FakeTimeProvider());

        radio.Capabilities.Should().Be(RadioCapabilities.CarrierSense);
    }

    [Fact]
    public void A_Rig_With_Nothing_The_Radio_Seam_Can_Use_Is_Rejected()
    {
        var act = () => new RigRadioControl(
            new FakeRig { Capabilities = RigCapabilities.FrequencyGet | RigCapabilities.ModeGet },
            timeProvider: new FakeTimeProvider());

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public async Task First_Sample_Populates_ChannelBusy_And_Only_A_Transition_Fires_An_Edge()
    {
        var clock = new FakeTimeProvider();
        var rig = new FakeRig { Capabilities = RigCapabilities.DcdRead, Dcd = false };
        await using var radio = new RigRadioControl(rig, timeProvider: clock);
        var events = new List<CarrierSenseChange>();
        radio.CarrierSenseChanged += (_, e) => { lock (events) { events.Add(e); } };

        await WaitUntilAsync(() => radio.ChannelBusy == false);
        lock (events)
        {
            events.Should().BeEmpty("the first sample is a population, not an edge");
        }

        rig.Dcd = true;
        await LetThePollLoopParkAsync();
        clock.Advance(TimeSpan.FromMilliseconds(100));
        await WaitUntilAsync(() => radio.ChannelBusy == true);

        lock (events)
        {
            events.Should().ContainSingle();
            events[0].Busy.Should().BeTrue();
            events[0].At.Should().Be(clock.GetUtcNow(), "edges are stamped from the injected clock");
        }
    }

    [Fact]
    public async Task A_Stable_Channel_Fires_No_Events()
    {
        var clock = new FakeTimeProvider();
        var rig = new FakeRig { Capabilities = RigCapabilities.DcdRead, Dcd = true };
        await using var radio = new RigRadioControl(rig, timeProvider: clock);
        var events = 0;
        radio.CarrierSenseChanged += (_, _) => Interlocked.Increment(ref events);

        await WaitUntilAsync(() => rig.DcdReads >= 1);
        for (var reads = 2; reads <= 5; reads++)
        {
            await LetThePollLoopParkAsync();
            clock.Advance(TimeSpan.FromMilliseconds(100));
            await WaitUntilAsync(() => rig.DcdReads >= reads);
        }

        radio.ChannelBusy.Should().BeTrue();
        Volatile.Read(ref events).Should().Be(0, "no bool→bool transition ever happened");
    }

    [Fact]
    public async Task A_Faulting_Rig_Fails_Open_And_Backs_Off_To_The_Retry_Interval()
    {
        var clock = new FakeTimeProvider();
        var rig = new FakeRig { Capabilities = RigCapabilities.DcdRead, Dcd = true };
        await using var radio = new RigRadioControl(rig, timeProvider: clock);
        var events = 0;
        radio.CarrierSenseChanged += (_, _) => Interlocked.Increment(ref events);

        await WaitUntilAsync(() => radio.ChannelBusy == true);

        rig.ReadFault = new RigConnectionException("daemon bounced");
        await LetThePollLoopParkAsync();
        clock.Advance(TimeSpan.FromMilliseconds(100));
        await WaitUntilAsync(() => radio.ChannelBusy is null);
        Volatile.Read(ref events).Should().Be(0, "a fault is unknown-state, not a carrier edge");

        // Faulted, the loop waits FaultRetryInterval (2 s) — a poll interval passing must not tick.
        var readsWhenFaulted = rig.DcdReads;
        await LetThePollLoopParkAsync();
        clock.Advance(TimeSpan.FromMilliseconds(100));
        await Task.Delay(50);
        rig.DcdReads.Should().Be(readsWhenFaulted, "a faulted loop retries at FaultRetryInterval, not DcdPollInterval");

        clock.Advance(TimeSpan.FromMilliseconds(1900));
        await WaitUntilAsync(() => rig.DcdReads == readsWhenFaulted + 1);
        radio.ChannelBusy.Should().BeNull("the rig is still faulting");
    }

    [Fact]
    public async Task Recovery_Repopulates_And_Edges_Only_On_A_Changed_Value()
    {
        var clock = new FakeTimeProvider();
        var rig = new FakeRig { Capabilities = RigCapabilities.DcdRead, Dcd = false };
        await using var radio = new RigRadioControl(rig, timeProvider: clock);
        var events = new List<CarrierSenseChange>();
        radio.CarrierSenseChanged += (_, e) => { lock (events) { events.Add(e); } };

        await WaitUntilAsync(() => radio.ChannelBusy == false);

        // Fault, then recover to the same value: ChannelBusy repopulates, no edge.
        rig.ReadFault = new RigConnectionException("daemon bounced");
        await LetThePollLoopParkAsync();
        clock.Advance(TimeSpan.FromMilliseconds(100));
        await WaitUntilAsync(() => radio.ChannelBusy is null);
        rig.ReadFault = null;
        await LetThePollLoopParkAsync();
        clock.Advance(TimeSpan.FromSeconds(2));
        await WaitUntilAsync(() => radio.ChannelBusy == false);
        lock (events)
        {
            events.Should().BeEmpty("recovery to the last known value is not an edge");
        }

        // Fault again, recover to the opposite value: repopulates AND edges.
        rig.ReadFault = new RigConnectionException("daemon bounced again");
        await LetThePollLoopParkAsync();
        clock.Advance(TimeSpan.FromMilliseconds(100));
        await WaitUntilAsync(() => radio.ChannelBusy is null);
        rig.ReadFault = null;
        rig.Dcd = true;
        await LetThePollLoopParkAsync();
        clock.Advance(TimeSpan.FromSeconds(2));
        await WaitUntilAsync(() => radio.ChannelBusy == true);
        lock (events)
        {
            events.Should().ContainSingle("the carrier state genuinely changed across the outage");
            events[0].Busy.Should().BeTrue();
        }
    }

    [Fact]
    public async Task ReadRssi_Delegates_To_The_Rig_Signal_Strength_Read()
    {
        var rig = new FakeRig { StrengthDbm = -73.5 };
        await using var radio = new RigRadioControl(rig, timeProvider: new FakeTimeProvider());

        (await radio.ReadRssiDbmAsync()).Should().Be(-73.5f);
        rig.StrengthReads.Should().Be(1);
    }

    [Fact]
    public async Task ReadRssi_On_A_Rig_Without_SignalStrengthRead_Throws_Without_Touching_The_Rig()
    {
        var rig = new FakeRig { Capabilities = RigCapabilities.DcdRead };
        await using var radio = new RigRadioControl(rig, timeProvider: new FakeTimeProvider());

        var act = async () => await radio.ReadRssiDbmAsync();

        await act.Should().ThrowAsync<NotSupportedException>();
        rig.StrengthReads.Should().Be(0);
    }

    [Fact]
    public async Task SetTransmitter_Delegates_To_The_Rig_Ptt()
    {
        var rig = new FakeRig();
        await using var radio = new RigRadioControl(rig, timeProvider: new FakeTimeProvider());

        await radio.SetTransmitterAsync(true);
        rig.Ptt.Should().BeTrue();

        await radio.SetTransmitterAsync(false);
        rig.Ptt.Should().BeFalse();
    }

    [Fact]
    public async Task SetTransmitter_On_A_Rig_Without_PttSet_Throws()
    {
        var rig = new FakeRig { Capabilities = RigCapabilities.DcdRead };
        await using var radio = new RigRadioControl(rig, timeProvider: new FakeTimeProvider());

        var act = async () => await radio.SetTransmitterAsync(true);

        await act.Should().ThrowAsync<NotSupportedException>();
        rig.PttSets.Should().Be(0);
    }

    [Fact]
    public async Task Dispose_Unkeys_A_Keyed_Unowned_Rig()
    {
        var rig = new FakeRig();
        var radio = new RigRadioControl(rig, timeProvider: new FakeTimeProvider());
        await radio.SetTransmitterAsync(true);

        await radio.DisposeAsync();

        rig.Ptt.Should().BeFalse("the rig outlives the adapter, so the adapter must not leave it keyed");
        rig.Disposed.Should().BeFalse();
    }

    [Fact]
    public async Task Dispose_Skips_The_Unkey_When_The_Adapter_Left_The_Rig_Unkeyed()
    {
        var rig = new FakeRig();
        var radio = new RigRadioControl(rig, timeProvider: new FakeTimeProvider());
        await radio.SetTransmitterAsync(true);
        await radio.SetTransmitterAsync(false);

        await radio.DisposeAsync();

        rig.PttSets.Should().Be(2, "an explicitly-unkeyed rig needs no dispose-time unkey");
    }

    [Fact]
    public async Task Dispose_Of_An_Owned_Keyed_Rig_Leaves_The_Unkey_To_The_Rig_Own_Dispose()
    {
        var rig = new FakeRig();
        var radio = new RigRadioControl(rig, timeProvider: new FakeTimeProvider(), ownsRig: true);
        await radio.SetTransmitterAsync(true);

        await radio.DisposeAsync();

        rig.Disposed.Should().BeTrue();
        rig.PttSets.Should().Be(1, "an owned rig's own dispose guarantees the unkey — no double work");
    }

    [Fact]
    public async Task Dispose_Stops_The_Poll_Loop_And_Leaves_An_Unowned_Rig_Undisposed()
    {
        var clock = new FakeTimeProvider();
        var rig = new FakeRig { Capabilities = RigCapabilities.DcdRead };
        var radio = new RigRadioControl(rig, timeProvider: clock);
        await WaitUntilAsync(() => rig.DcdReads >= 1);

        await radio.DisposeAsync();

        var readsAfterDispose = rig.DcdReads;
        clock.Advance(TimeSpan.FromMinutes(1));
        await Task.Delay(50);
        rig.DcdReads.Should().Be(readsAfterDispose, "a disposed adapter must not poll");
        rig.Disposed.Should().BeFalse("ownsRig: false leaves the rig's lifetime with the caller");
    }

    [Fact]
    public async Task Dispose_Of_An_Owning_Adapter_Disposes_The_Rig_And_Double_Dispose_Is_Safe()
    {
        var rig = new FakeRig { Capabilities = RigCapabilities.DcdRead };
        var radio = new RigRadioControl(rig, timeProvider: new FakeTimeProvider(), ownsRig: true);

        await radio.DisposeAsync();
        await radio.DisposeAsync();

        rig.Disposed.Should().BeTrue("ownsRig: true hands the rig's lifetime to the adapter");
    }

    [Fact]
    public async Task No_CarrierSense_Capability_Means_No_Poll_Loop()
    {
        var clock = new FakeTimeProvider();
        var rig = new FakeRig { Capabilities = RigCapabilities.SignalStrengthRead | RigCapabilities.PttSet };
        await using var radio = new RigRadioControl(rig, timeProvider: clock);

        clock.Advance(TimeSpan.FromSeconds(10));
        await Task.Delay(50);

        rig.DcdReads.Should().Be(0, "there is no DCD to poll");
        radio.ChannelBusy.Should().BeNull();
    }
}
