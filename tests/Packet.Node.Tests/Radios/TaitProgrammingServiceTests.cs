using Microsoft.Extensions.Time.Testing;
using System.Threading.Channels;
using Packet.Node.Core.Configuration;
using Packet.Node.Core.Radios.Programming;
using Packet.Node.Tests.Support;

namespace Packet.Node.Tests.Radios;

/// <summary>
/// The orchestration half of codeplug programming (#779): the part that takes a live port out of
/// service to get at its radio's serial device and therefore <b>must</b> put the port back on every
/// path. Driven through the internal gateway + writer seams, so the whole run - preflight, port
/// down, program, port back, feed - executes with no supervisor, no port and no radio.
/// </summary>
[Trait("Category", "Node")]
public sealed class TaitProgrammingServiceTests
{
    private const string Port = "vhf-1";

    /// <summary>The kind of modem a Tait CCDI radio pairs with. Irrelevant to programming - it is
    /// the radio's own serial device that gets driven - but PortConfig requires one.</summary>
    private static readonly NinoTncTransport Modem = new() { Device = "/dev/ttyACM0" };

    [Fact]
    public async Task A_run_stops_the_port_programs_the_radio_and_puts_the_port_back()
    {
        await using var h = new Harness();
        h.AddTaitPort(Port, devicePath: "/dev/ttyUSB3");

        var info = await h.StartAsync(Port);
        await h.WaitForTerminalAsync(Port);

        h.Gateway.Timeline.Should().Equal("down:vhf-1", "program:/dev/ttyUSB3", "radio-back:/dev/ttyUSB3", "up:vhf-1");
        info.PortId.Should().Be(Port);
        h.Get(Port).State.Should().Be("done");
        h.Get(Port).RadioModel.Should().Be("TMAB12-B100_0201");
        h.Get(Port).RadioSerial.Should().Be("19925328");
        h.Get(Port).Error.Should().BeNull();
    }

    [Fact]
    public async Task The_live_radios_device_path_is_used_when_the_port_is_running()
    {
        // A running port's CCDI driver knows exactly which device it is on, so nothing has to be
        // scanned for. The config here binds by CCDI serial, which is the case where that matters.
        await using var h = new Harness();
        h.AddTaitPort(Port, serial: "19925328");
        h.Gateway.LivePath = "/dev/ttyUSB7";

        await h.StartAsync(Port);
        await h.WaitForTerminalAsync(Port);

        h.Gateway.Timeline.Should().Equal("down:vhf-1", "program:/dev/ttyUSB7", "radio-back:/dev/ttyUSB7", "up:vhf-1");
        h.Gateway.Resolved.Should().BeEmpty("a scan is only needed when nothing has the radio open");
    }

    [Fact]
    public async Task A_serial_bound_radio_on_a_stopped_port_is_located_after_the_teardown()
    {
        // The scan opens candidate serial ports, so it can only find a device nothing holds open -
        // which is why it happens after the port is down, not on the request thread.
        await using var h = new Harness();
        h.AddTaitPort(Port, serial: "19925328");
        h.Gateway.LivePath = null;
        h.Gateway.ResolvesTo = "/dev/ttyUSB9";

        await h.StartAsync(Port);
        await h.WaitForTerminalAsync(Port);

        h.Gateway.Timeline.Should().Equal("down:vhf-1", "resolve:19925328", "program:/dev/ttyUSB9", "radio-back:/dev/ttyUSB9", "up:vhf-1");
        h.Get(Port).DevicePath.Should().Be("/dev/ttyUSB9");
    }

    [Fact]
    public async Task The_port_is_held_down_until_the_radio_has_finished_restarting()
    {
        // The bug this is here for: every programming session ends by resetting the radio, so for
        // several seconds afterwards nothing answers on the control channel. Restoring the port
        // into that window left it serving traffic with no radio control - and no retry - until an
        // operator noticed and restarted it by hand.
        var clock = new FakeTimeProvider();
        await using var h = new Harness(clock: clock);
        h.AddTaitPort(Port, devicePath: "/dev/ttyUSB3");
        h.Gateway.RadioSilentProbes = 4;   // the radio is still booting for the first four probes

        await h.StartAsync(Port);
        await h.WaitForTerminalAsync(Port);

        h.Gateway.Timeline.Should().Equal(
            "down:vhf-1", "program:/dev/ttyUSB3", "radio-back:/dev/ttyUSB3", "up:vhf-1");
        h.Gateway.RadioProbes.Should().Be(5, "the run keeps asking until the radio answers");
        h.Get(Port).State.Should().Be("done");
        h.Get(Port).Log.Should().Contain(l => l.Contains("answering again", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_radio_that_never_comes_back_still_gives_the_port_back_and_says_so()
    {
        // The codeplug is written either way, so a radio that stays silent must not turn a good
        // write into a failed run - but the operator has to be told the port is coming back
        // without it, because that is exactly the state that used to be invisible.
        var clock = new FakeTimeProvider();
        await using var h = new Harness(clock: clock);
        h.AddTaitPort(Port, devicePath: "/dev/ttyUSB3");
        h.Gateway.RadioSilentProbes = int.MaxValue;

        await h.StartAsync(Port);
        await h.WaitForTerminalAsync(Port);

        h.Gateway.Timeline.Should().Equal("down:vhf-1", "program:/dev/ttyUSB3", "up:vhf-1");
        var info = h.Get(Port);
        info.State.Should().Be("done", "a silent radio is not a failed write");
        info.Log.Should().Contain(l => l.Contains("without radio control", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_failed_write_still_puts_the_port_back_and_reports_why()
    {
        await using var h = new Harness();
        h.AddTaitPort(Port, devicePath: "/dev/ttyUSB3");
        h.Writer.Fail = new TimeoutException("radio did not enter programming mode");

        await h.StartAsync(Port);
        await h.WaitForTerminalAsync(Port);

        h.Gateway.Timeline.Should().Equal("down:vhf-1", "program:/dev/ttyUSB3", "up:vhf-1");
        var info = h.Get(Port);
        info.State.Should().Be("failed");
        info.Error.Should().Contain("power-cycle");
        info.FinishedAt.Should().NotBeNull();
    }

    [Fact]
    public async Task A_radio_that_never_answers_is_reported_as_a_power_cycle_problem_not_a_stack_trace()
    {
        await using var h = new Harness();
        h.AddTaitPort(Port, devicePath: "/dev/ttyUSB3");
        h.Writer.Fail = new NotSupportedException("refusing to write: the radio's database version '0090' is not one the write path is validated for");

        await h.StartAsync(Port);
        await h.WaitForTerminalAsync(Port);

        h.Get(Port).Error.Should().Be(
            "refusing to write: the radio's database version '0090' is not one the write path is validated for");
    }

    [Fact]
    public async Task Cancelling_ends_the_run_and_puts_the_port_back()
    {
        await using var h = new Harness();
        h.AddTaitPort(Port, devicePath: "/dev/ttyUSB3");
        h.Writer.BlockUntilCancelled = true;

        await h.StartAsync(Port);
        await h.Writer.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        (await h.Service.CancelAsync(Port)).Should().BeTrue();

        h.Get(Port).State.Should().Be("cancelled");
        h.Gateway.Timeline.Should().Equal("down:vhf-1", "program:/dev/ttyUSB3", "up:vhf-1");
    }

    [Fact]
    public async Task Cancelling_when_nothing_is_running_is_a_no_op()
    {
        await using var h = new Harness();
        h.AddTaitPort(Port, devicePath: "/dev/ttyUSB3");

        (await h.Service.CancelAsync(Port)).Should().BeFalse();

        await h.StartAsync(Port);
        await h.WaitForTerminalAsync(Port);

        (await h.Service.CancelAsync(Port)).Should().BeFalse("a finished run has nothing to cancel");
    }

    [Fact]
    public async Task A_second_run_on_a_port_already_being_programmed_is_a_conflict()
    {
        await using var h = new Harness();
        h.AddTaitPort(Port, devicePath: "/dev/ttyUSB3");
        h.Writer.BlockUntilCancelled = true;

        await h.StartAsync(Port);
        await h.Writer.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var act = async () => await h.StartAsync(Port);

        (await act.Should().ThrowAsync<TaitProgramStartException>())
            .Which.Error.Should().Be(TaitProgramStartError.Conflict);

        await h.Service.CancelAsync(Port);
    }

    [Fact]
    public async Task A_run_supersedes_the_finished_one_on_the_same_port()
    {
        await using var h = new Harness();
        h.AddTaitPort(Port, devicePath: "/dev/ttyUSB3");

        await h.StartAsync(Port);
        await h.WaitForTerminalAsync(Port);
        await h.StartAsync(Port, rxHz: 145_500_000);
        await h.WaitForTerminalAsync(Port);

        h.Get(Port).Plan!.RxFrequencyHz.Should().Be(145_500_000);
        h.Gateway.Timeline.Should().Equal(
            "down:vhf-1", "program:/dev/ttyUSB3", "radio-back:/dev/ttyUSB3", "up:vhf-1",
            "down:vhf-1", "program:/dev/ttyUSB3", "radio-back:/dev/ttyUSB3", "up:vhf-1");
    }

    [Fact]
    public async Task A_port_busy_with_a_tuning_session_is_a_conflict()
    {
        await using var h = new Harness(portBusy: id => id == Port);
        h.AddTaitPort(Port, devicePath: "/dev/ttyUSB3");

        var act = async () => await h.StartAsync(Port);

        var thrown = await act.Should().ThrowAsync<TaitProgramStartException>();
        thrown.Which.Error.Should().Be(TaitProgramStartError.Conflict);
        thrown.Which.Message.Should().Contain("tuning session");
        h.Gateway.Timeline.Should().BeEmpty("nothing is touched when the preflight refuses");
    }

    [Fact]
    public async Task An_unknown_port_is_a_not_found()
    {
        await using var h = new Harness();

        var act = async () => await h.StartAsync("nope");

        (await act.Should().ThrowAsync<TaitProgramStartException>())
            .Which.Error.Should().Be(TaitProgramStartError.NotFound);
    }

    [Fact]
    public async Task A_port_with_no_radio_is_refused()
    {
        await using var h = new Harness();
        h.Gateway.Ports[Port] = new PortConfig { Id = Port, Transport = Modem };

        var act = async () => await h.StartAsync(Port);

        var thrown = await act.Should().ThrowAsync<TaitProgramStartException>();
        thrown.Which.Error.Should().Be(TaitProgramStartError.BadRequest);
        thrown.Which.Message.Should().Contain("Radio control");
    }

    [Fact]
    public async Task A_rig_backed_radio_is_refused()
    {
        await using var h = new Harness();
        h.Gateway.Ports[Port] = new PortConfig
        {
            Id = Port,
            Transport = Modem,
            Radio = new PortRadioConfig { Kind = RadioKinds.Rig },
        };

        var act = async () => await h.StartAsync(Port);

        (await act.Should().ThrowAsync<TaitProgramStartException>())
            .Which.Message.Should().Contain("tait-ccdi");
    }

    [Fact]
    public async Task A_head_end_bound_radio_is_refused_with_the_reason_spelled_out()
    {
        // Programming latches the radio at boot over a directly-cabled line. A radio at the far end
        // of a head-end's TCP bridge is refused rather than half-supported.
        await using var h = new Harness();
        h.Gateway.Ports[Port] = new PortConfig
        {
            Id = Port,
            Transport = Modem,
            Radio = new PortRadioConfig
            {
                Kind = RadioKinds.TaitCcdi,
                HeadEndId = "mast",
                DeviceId = "usb-0:1.2",
            },
        };

        var act = async () => await h.StartAsync(Port);

        var thrown = await act.Should().ThrowAsync<TaitProgramStartException>();
        thrown.Which.Error.Should().Be(TaitProgramStartError.BadRequest);
        thrown.Which.Message.Should().Contain("head-end 'mast'").And.Contain("tait-codeplug CLI");
    }

    [Fact]
    public async Task Bad_settings_are_refused_before_the_port_is_touched()
    {
        await using var h = new Harness();
        h.AddTaitPort(Port, devicePath: "/dev/ttyUSB3");

        var act = async () => await h.Service.StartAsync(
            Port, new TaitProgramRequest(144_812_500, null, "very-narrow", "high", "none"));

        (await act.Should().ThrowAsync<TaitProgramStartException>())
            .Which.Error.Should().Be(TaitProgramStartError.BadRequest);
        h.Gateway.Timeline.Should().BeEmpty();
    }

    [Fact]
    public async Task The_feed_replays_the_whole_run_to_a_late_subscriber()
    {
        // The panel is opened, closed and re-opened while a run is in flight, and a browser reload
        // must not lose the story - so a subscriber gets the history before the live events.
        await using var h = new Harness();
        h.AddTaitPort(Port, devicePath: "/dev/ttyUSB3");

        await h.StartAsync(Port);
        await h.WaitForTerminalAsync(Port);

        using var subscription = h.Service.Subscribe(Port, out ChannelReader<TaitProgramEvent> reader);
        subscription.Should().NotBeNull();

        var seen = new List<TaitProgramEvent>();
        await foreach (var evt in reader.ReadAllAsync().WithCancellation(TestCts().Token))
        {
            seen.Add(evt);
        }

        seen.Select(e => e.State).Should().ContainInOrder("starting", "power-cycle", "writing", "restoring", "done");
        seen[^1].Kind.Should().Be("state");
    }

    [Fact]
    public void Subscribing_to_a_port_that_has_never_run_gives_nothing_to_subscribe_to()
    {
        using var h = new Harness();

        h.Service.Subscribe(Port, out _).Should().BeNull();
        h.Service.Get(Port).Should().BeNull();
    }

    [Fact]
    public async Task Disposing_the_service_cancels_a_live_run_and_restores_its_port()
    {
        var h = new Harness();
        h.AddTaitPort(Port, devicePath: "/dev/ttyUSB3");
        h.Writer.BlockUntilCancelled = true;

        await h.StartAsync(Port);
        await h.Writer.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        await h.DisposeAsync();

        h.Gateway.Timeline.Should().Equal("down:vhf-1", "program:/dev/ttyUSB3", "up:vhf-1");
    }

    [Fact]
    public async Task A_read_run_walks_the_same_orchestration_but_hands_the_writer_no_plan()
    {
        // The Read button exists so an operator can find out what a radio is set to without
        // betting a codeplug on it. It costs the port the same few minutes, so it goes through the
        // same port-down / port-back machinery - but nothing is written.
        await using var h = new Harness();
        h.AddTaitPort(Port, devicePath: "/dev/ttyUSB3");

        await h.StartReadAsync(Port);
        await h.WaitForTerminalAsync(Port);

        h.Gateway.Timeline.Should().Equal("down:vhf-1", "read:/dev/ttyUSB3", "radio-back:/dev/ttyUSB3", "up:vhf-1");
        h.Writer.LastPlan.Should().BeNull("a read must not carry a plan anywhere near the radio");

        var info = h.Get(Port);
        info.State.Should().Be("done");
        info.Mode.Should().Be("read");
        info.Plan.Should().BeNull();
        info.Current!.RxFrequencyHz.Should().Be(145_287_500);
        info.Current.ChannelCount.Should().Be(6);
        info.Current.DatabaseVersion.Should().Be("0095");
    }

    [Fact]
    public async Task A_write_run_records_what_the_radio_was_set_to_before_it_was_changed()
    {
        await using var h = new Harness();
        h.AddTaitPort(Port, devicePath: "/dev/ttyUSB3");

        await h.StartAsync(Port);
        await h.WaitForTerminalAsync(Port);

        var info = h.Get(Port);
        info.Mode.Should().Be("program");
        info.Plan!.RxFrequencyHz.Should().Be(144_812_500);
        info.Current!.RxFrequencyHz.Should().Be(145_287_500, "the run reports what it replaced");
    }

    [Fact]
    public async Task A_failure_says_which_state_it_happened_in_and_keeps_the_runs_log()
    {
        // "The last run failed" with nothing after it is the worst thing this panel can say. The
        // run keeps the reason, the state it failed in, and every line it printed on the way there.
        await using var h = new Harness();
        h.AddTaitPort(Port, devicePath: "/dev/ttyUSB3");
        h.Writer.Fail = new NotSupportedException("refusing to write: the radio's database version '0091'...");

        await h.StartAsync(Port);
        await h.WaitForTerminalAsync(Port);

        var info = h.Get(Port);
        info.State.Should().Be("failed");
        info.Error.Should().Contain("database version '0091'");
        info.FailedState.Should().Be("power-cycle", "the scripted writer throws while still in the power-cycle window");
        info.Log.Should().NotBeEmpty();
        h.Gateway.Timeline.Should().Equal("down:vhf-1", "program:/dev/ttyUSB3", "up:vhf-1");
    }

    [Fact]
    public async Task A_timeout_after_the_handshake_is_not_reported_as_a_missed_power_cycle()
    {
        // A timeout means two different things depending on when it lands, and telling an operator
        // to power-cycle a radio that has already answered sends them the wrong way entirely.
        await using var h = new Harness();
        h.AddTaitPort(Port, devicePath: "/dev/ttyUSB3");
        h.Writer.Fail = new TimeoutException("no programming response within the transaction deadline");
        h.Writer.ReachWritingFirst = true;

        await h.StartAsync(Port);
        await h.WaitForTerminalAsync(Port);

        var info = h.Get(Port);
        info.FailedState.Should().Be("writing");
        info.Error.Should().Contain("stopped answering while writing the codeplug");
        info.Error.Should().NotContain("never entered programming mode");
    }

    private static CancellationTokenSource TestCts() => new(TimeSpan.FromSeconds(5));

    /// <summary>The service over fake node-host operations and a scripted codeplug writer.</summary>
    private sealed class Harness : IAsyncDisposable, IDisposable
    {
        private readonly FakeTimeProvider? virtualClock;

        internal Harness(Func<string, bool>? portBusy = null, FakeTimeProvider? clock = null)
        {
            // The writer's own entry lands on the gateway's timeline, so one list tells the whole
            // story: port down -> device located -> radio programmed -> radio back -> port back.
            Writer.Recorder = Gateway.Record;
            virtualClock = clock;
            Service = new TaitProgrammingService(
                Gateway, Writer, Logger, backupDirectory: null, portBusy, clock ?? TimeProvider.System);
        }

        internal FakeProgrammingGateway Gateway { get; } = new();

        internal ScriptedCodeplugWriter Writer { get; } = new();

        internal CapturingLogger<TaitProgrammingService> Logger { get; } = new();

        internal TaitProgrammingService Service { get; }

        internal void AddTaitPort(string id, string? devicePath = null, string? serial = null)
        {
            Gateway.Ports[id] = new PortConfig
            {
                Id = id,
                Transport = Modem,
                Radio = new PortRadioConfig
                {
                    Kind = RadioKinds.TaitCcdi,
                    Port = devicePath ?? string.Empty,
                    Serial = serial ?? string.Empty,
                },
            };
            Gateway.LivePath = devicePath;
        }

        internal Task<TaitProgramInfo> StartAsync(string portId, long rxHz = 144_812_500) =>
            Service.StartAsync(portId, new TaitProgramRequest(rxHz, null, "narrow", "high", "pdn-basic"));

        internal Task<TaitProgramInfo> StartReadAsync(string portId) => Service.StartReadAsync(portId);

        internal TaitProgramInfo Get(string portId) => Service.Get(portId)!;

        internal async Task WaitForTerminalAsync(string portId)
        {
            using var cts = TestCts();
            while (!cts.IsCancellationRequested)
            {
                if (Service.Get(portId) is { } info
                    && info.State is "done" or "failed" or "cancelled")
                {
                    return;
                }

                // A harness with a virtual clock drives the run's own waits (the radio-restart
                // poll), so the pump advances it rather than sleeping through 45 real seconds.
                virtualClock?.Advance(TaitProgrammingSession.RadioRestartPoll);
                await Task.Delay(5, cts.Token);
            }

            throw new TimeoutException($"run on '{portId}' never reached a terminal state");
        }

        public ValueTask DisposeAsync() => Service.DisposeAsync();

        public void Dispose() => Service.DisposeAsync().AsTask().GetAwaiter().GetResult();
    }

    /// <summary>Records what the node host was asked to do, in order.</summary>
    private sealed class FakeProgrammingGateway : ITaitProgrammingGateway
    {
        private readonly List<string> timeline = [];

        internal Dictionary<string, PortConfig> Ports { get; } = new(StringComparer.Ordinal);

        /// <summary>What the live radio's device path resolves to (null = the port is not running).</summary>
        internal string? LivePath { get; set; }

        /// <summary>What a scan for a serial-bound radio finds.</summary>
        internal string ResolvesTo { get; set; } = "/dev/ttyUSB0";

        internal List<string> Resolved { get; } = [];

        internal IReadOnlyList<string> Timeline
        {
            get
            {
                lock (timeline)
                {
                    return [.. timeline];
                }
            }
        }

        public PortConfig? GetPortConfig(string portId) =>
            Ports.TryGetValue(portId, out var port) ? port : null;

        public string? LiveRadioDevicePath(string portId) => Ports.ContainsKey(portId) ? LivePath : null;

        public Task<string> ResolveDevicePathAsync(PortRadioConfig radio, CancellationToken cancellationToken)
        {
            Resolved.Add(radio.Serial);
            Record($"resolve:{radio.Serial}");
            return Task.FromResult(ResolvesTo);
        }

        /// <summary>How many probes answer "not yet" before the radio is back. Default 0 - the
        /// radio answers straight away, which is every test that is not about the wait.</summary>
        internal int RadioSilentProbes { get; set; }

        /// <summary>How many times the radio was probed for its return.</summary>
        internal int RadioProbes { get; private set; }

        public Task<bool> ProbeRadioAsync(
            PortRadioConfig radio, string devicePath, CancellationToken cancellationToken)
        {
            RadioProbes++;
            if (RadioProbes > RadioSilentProbes)
            {
                Record($"radio-back:{devicePath}");
                return Task.FromResult(true);
            }

            return Task.FromResult(false);
        }

        public async Task RunWithPortDownAsync(
            string portId, Func<CancellationToken, Task> work, CancellationToken cancellationToken)
        {
            Record($"down:{portId}");
            try
            {
                await work(cancellationToken);
            }
            finally
            {
                // Exactly the production contract: the port comes back whatever happened.
                Record($"up:{portId}");
            }
        }

        internal void Record(string entry)
        {
            lock (timeline)
            {
                timeline.Add(entry);
            }
        }
    }

    /// <summary>A codeplug writer that never touches a serial port.</summary>
    private sealed class ScriptedCodeplugWriter : ITaitCodeplugWriter
    {
        /// <summary>Thrown instead of writing, to drive the failure paths.</summary>
        internal Exception? Fail { get; set; }

        /// <summary>Sit in the write until the run is cancelled (the cancel / conflict tests).</summary>
        internal bool BlockUntilCancelled { get; set; }

        /// <summary>Report having got as far as the write before throwing <see cref="Fail"/>.</summary>
        internal bool ReachWritingFirst { get; set; }

        /// <summary>Completes once the writer has been entered.</summary>
        internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>The plan the last call was handed - null on a read-only run.</summary>
        internal TaitProgramPlan? LastPlan { get; private set; }

        /// <summary>What the scripted radio turns out to be set to.</summary>
        internal TaitRadioSettings Current { get; } = new(
            145_287_500, 145_287_500, "wide", "medium", "none", 6, "0095", "none", "none");

        public TaitCodeplugWriteOutcome Program(
            string devicePath,
            TaitProgramPlan? plan,
            string? backupDirectory,
            Action<TaitProgramState, double?, string> report,
            CancellationToken cancellationToken)
        {
            Recorder?.Invoke($"{(plan is null ? "read" : "program")}:{devicePath}");
            LastPlan = plan;
            Entered.TrySetResult();

            report(TaitProgramState.PowerCycle, null, "power-cycle the radio now");

            if (BlockUntilCancelled)
            {
                cancellationToken.WaitHandle.WaitOne(TimeSpan.FromSeconds(10));
                cancellationToken.ThrowIfCancellationRequested();
            }

            if (ReachWritingFirst)
            {
                report(TaitProgramState.Reading, null, "read complete");
                report(TaitProgramState.Writing, null, "writing the codeplug back");
            }

            if (Fail is { } failure)
            {
                throw failure;
            }

            if (plan is null)
            {
                report(TaitProgramState.Reading, null, "read only - nothing was written to the radio");
                return new TaitCodeplugWriteOutcome("TMAB12-B100_0201", "19925328", null, 0, Current);
            }

            report(TaitProgramState.Writing, 0.5, "record 500 of 1000");
            return new TaitCodeplugWriteOutcome("TMAB12-B100_0201", "19925328", null, 1000, Current);
        }

        internal Action<string>? Recorder { get; set; }
    }
}
