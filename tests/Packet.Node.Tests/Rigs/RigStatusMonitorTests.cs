using Microsoft.Extensions.Time.Testing;
using Packet.Node.Core.Configuration;
using Packet.Node.Core.Rigs;
using Packet.Node.Tests.Support;
using Packet.Rig;

namespace Packet.Node.Tests.Rigs;

/// <summary>
/// The rig status poller: capability-gated reads, idle-vs-keyed cadence, per-read fault
/// isolation with a self-healing <c>faulted</c> projection, telemetry publication, and the
/// stop-before-the-rig disposal contract. All timing on a <see cref="FakeTimeProvider"/>.
/// </summary>
[Trait("Category", "Node")]
public sealed class RigStatusMonitorTests
{
    private static readonly PortRigConfig Config = new() { Kind = "hamlib" };

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

    [Fact]
    public async Task First_tick_projects_frequency_mode_and_ptt()
    {
        var clock = new FakeTimeProvider();
        var rig = new FakeRigControl { FrequencyHz = 7_074_000, ModeToken = "PKTUSB", PassbandHz = 3000 };
        await using var monitor = RigStatusMonitors.Create("hf", Config, rig, telemetry: null, clock);

        await WaitUntilAsync(() => monitor.Snapshot().SampledAt is not null);

        var status = monitor.Snapshot();
        status.Attached.Should().BeTrue();
        status.Kind.Should().Be("hamlib");
        status.Endpoint.Should().Be("127.0.0.1:4532"); // config defaults resolved
        status.Backend.Should().Be("Fake rig");
        status.Model.Should().Be("Fake-1000");
        status.ConnectionState.Should().Be("healthy");
        status.FrequencyHz.Should().Be(7_074_000);
        status.Mode.Should().Be("PKTUSB");
        status.PassbandHz.Should().Be(3000);
        status.Transmitting.Should().BeFalse();
        status.Meters.Should().BeNull("meters are only read while the transmitter is keyed");
        status.Capabilities.Should().Contain(["frequencyGet", "swrMeter"]);
    }

    [Fact]
    public async Task Idle_rig_polls_at_the_slow_cadence()
    {
        var clock = new FakeTimeProvider();
        var rig = new FakeRigControl();
        await using var monitor = RigStatusMonitors.Create(
            "hf", new PortRigConfig { Kind = "hamlib", PollIntervalSeconds = 5 }, rig, null, clock);

        await WaitUntilAsync(() => rig.FrequencyReads == 1);

        // No time passes → no second tick.
        await Task.Delay(50);
        rig.FrequencyReads.Should().Be(1);

        clock.Advance(TimeSpan.FromSeconds(5));
        await WaitUntilAsync(() => rig.FrequencyReads == 2);
    }

    [Fact]
    public async Task Keyed_transmitter_switches_to_the_fast_cadence_and_samples_meters()
    {
        var clock = new FakeTimeProvider();
        var rig = new FakeRigControl { Ptt = true, Swr = 1.7, RfPowerWatts = 40, RfPowerRelative = 0.4 };
        await using var monitor = RigStatusMonitors.Create(
            "hf", new PortRigConfig { Kind = "hamlib", PollIntervalSeconds = 5, MeterIntervalSeconds = 1 },
            rig, null, clock);

        await WaitUntilAsync(() => monitor.Snapshot().Meters is not null);

        var status = monitor.Snapshot();
        status.Transmitting.Should().BeTrue();
        status.Meters!.Swr.Should().Be(1.7);
        status.Meters.RfPowerWatts.Should().Be(40);
        status.Meters.RfPowerRelative.Should().Be(0.4);

        // The keyed cadence is the meter interval (1 s), not the poll interval (5 s).
        clock.Advance(TimeSpan.FromSeconds(1));
        await WaitUntilAsync(() => rig.SwrReads >= 2);
    }

    [Fact]
    public async Task Meters_are_never_read_while_idle()
    {
        var clock = new FakeTimeProvider();
        var rig = new FakeRigControl { Ptt = false };
        await using var monitor = RigStatusMonitors.Create("hf", Config, rig, null, clock);

        await WaitUntilAsync(() => monitor.Snapshot().SampledAt is not null);

        rig.SwrReads.Should().Be(0);
    }

    [Fact]
    public async Task Unadvertised_capabilities_are_never_called_and_project_null()
    {
        var clock = new FakeTimeProvider();
        // A Tait-shaped capability slice: PTT + relative power only.
        var rig = new FakeRigControl
        {
            Capabilities = RigCapabilities.PttGet | RigCapabilities.PttSet | RigCapabilities.RfPowerMeter,
            Ptt = false,
        };
        await using var monitor = RigStatusMonitors.Create("hf", Config, rig, null, clock);

        await WaitUntilAsync(() => monitor.Snapshot().SampledAt is not null);

        var status = monitor.Snapshot();
        status.FrequencyHz.Should().BeNull();
        status.Mode.Should().BeNull();
        rig.FrequencyReads.Should().Be(0, "FrequencyGet is not advertised");
        status.Capabilities.Should().BeEquivalentTo(["pttGet", "pttSet", "rfPowerMeter"]);
    }

    [Fact]
    public async Task A_bounced_daemon_projects_faulted_then_self_heals()
    {
        var clock = new FakeTimeProvider();
        var rig = new FakeRigControl();
        await using var monitor = RigStatusMonitors.Create("hf", Config, rig, null, clock);
        await WaitUntilAsync(() => monitor.Snapshot().ConnectionState == "healthy");

        rig.ReadFault = new RigConnectionException("daemon bounced");
        clock.Advance(RigStatusMonitor.DefaultPollInterval);
        await WaitUntilAsync(() => monitor.Snapshot().ConnectionState == "faulted");

        // Last-known-good values survive the outage — the UI keeps showing the dial.
        monitor.Snapshot().FrequencyHz.Should().Be(14_074_000);

        rig.ReadFault = null;
        clock.Advance(RigStatusMonitor.DefaultPollInterval);
        await WaitUntilAsync(() => monitor.Snapshot().ConnectionState == "healthy");
    }

    [Fact]
    public async Task Every_tick_publishes_to_the_telemetry_hub()
    {
        var clock = new FakeTimeProvider();
        var telemetry = new RigTelemetry();
        using var sub = telemetry.Subscribe(out var reader);
        var rig = new FakeRigControl();
        await using var monitor = RigStatusMonitors.Create("hf", Config, rig, telemetry, clock);

        await WaitUntilAsync(() => reader.TryPeek(out _));

        reader.TryRead(out var status).Should().BeTrue();
        status!.PortId.Should().Be("hf");
        status.FrequencyHz.Should().Be(14_074_000);
    }

    [Fact]
    public async Task RequestRefresh_ticks_immediately_without_waiting_for_the_cadence()
    {
        var clock = new FakeTimeProvider();
        var rig = new FakeRigControl { FrequencyHz = 14_074_000 };
        await using var monitor = RigStatusMonitors.Create("hf", Config, rig, null, clock);
        await WaitUntilAsync(() => monitor.Snapshot().FrequencyHz == 14_074_000);

        // A mutation lands between ticks; the wake makes it visible NOW, no clock advance.
        rig.FrequencyHz = 7_074_000;
        monitor.RequestRefresh();

        await WaitUntilAsync(() => monitor.Snapshot().FrequencyHz == 7_074_000);
    }

    [Fact]
    public async Task Dispose_stops_the_loop_but_not_the_rig()
    {
        var clock = new FakeTimeProvider();
        var rig = new FakeRigControl();
        var monitor = RigStatusMonitors.Create("hf", Config, rig, null, clock);
        await WaitUntilAsync(() => rig.FrequencyReads >= 1);

        await monitor.DisposeAsync();

        var readsAfterDispose = rig.FrequencyReads;
        clock.Advance(TimeSpan.FromMinutes(1));
        await Task.Delay(50);
        rig.FrequencyReads.Should().Be(readsAfterDispose, "a disposed monitor must not poll");
        rig.Disposed.Should().BeFalse("the monitor does not own the rig — the port supervisor does");
    }
}
