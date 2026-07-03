using System.Collections.Concurrent;

namespace Packet.Tune.Core.Tests;

/// <summary>
/// Coordinator + responder end-to-end over the in-memory link pair with fake
/// stations: the propose→confirm→commit→probe choreography, per-direction cells,
/// the rejection path, and — the safety property — every failure mode ending with
/// BOTH stations back at the session's home mode/channel.
/// </summary>
public class ModeCoordinationTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(15);

    /// <summary>Near-zero timings — no radios to guard in a fake rig.</summary>
    private static ModeCoordOptions FastOptions => new()
    {
        HomeMode = 6,
        HomeChannel = 0,
        ProbeFrames = 5,
        HomeVerifyProbeFrames = 2,
        ConfirmTimeout = TimeSpan.FromSeconds(3),
        ReportTimeout = TimeSpan.FromSeconds(5),
        PeerProbeTimeout = TimeSpan.FromSeconds(5),
        // Non-zero: the peer's switch is asynchronous even on a fake rig — the
        // settle is what covers it, exactly as on hardware.
        SwitchSettle = TimeSpan.FromMilliseconds(500),
        PreProbeDelay = TimeSpan.Zero,
        ArrivalGrace = TimeSpan.FromMilliseconds(50),
        ResponderIdleRevert = TimeSpan.FromSeconds(60),
    };

    [Fact]
    public async Task A_successful_switch_probes_both_ways_and_stays()
    {
        var rig = FakeRig.Create();
        await using var coordinator = new ModeCoordinator(rig.CoordinatorLink, rig.CoordinatorStation, FastOptions);
        var responderRun = RunResponder(rig, out var responderCts);

        (await coordinator.HelloAsync().WaitAsync(Timeout)).Should().BeTrue();
        var attempt = await coordinator.CoordinateAsync(7).WaitAsync(Timeout);

        attempt.Outcome.Should().Be(ModeCoordOutcome.Switched);
        attempt.Success.Should().BeTrue();
        attempt.CoordinatorToResponder.Should().NotBeNull();
        attempt.CoordinatorToResponder!.Decoded.Should().Be(5);
        attempt.ResponderToCoordinator!.Decoded.Should().Be(5);
        attempt.Reverted.Should().BeFalse();

        rig.CoordinatorStation.Mode.Should().Be(7);
        rig.ResponderStation.Mode.Should().Be(7, "a verified switch stands");
        coordinator.CurrentMode.Should().Be(7);

        await EndSession(coordinator, responderRun, responderCts);
    }

    [Fact]
    public async Task A_switch_with_channel_moves_both_radios()
    {
        var rig = FakeRig.Create();
        await using var coordinator = new ModeCoordinator(rig.CoordinatorLink, rig.CoordinatorStation, FastOptions);
        var responderRun = RunResponder(rig, out var responderCts);

        var attempt = await coordinator.CoordinateAsync(1, channel: 1).WaitAsync(Timeout);

        attempt.Outcome.Should().Be(ModeCoordOutcome.Switched);
        attempt.Channel.Should().Be(1);
        attempt.ChannelInEffect.Should().Be(1);
        rig.CoordinatorStation.Channel.Should().Be(1);
        rig.ResponderStation.Channel.Should().Be(1);
        coordinator.CurrentChannel.Should().Be(1);

        await EndSession(coordinator, responderRun, responderCts);
    }

    [Fact]
    public async Task A_dead_link_probe_reverts_both_ends_and_verifies_home_alive()
    {
        var rig = FakeRig.Create();
        // Probes transmitted in mode 1 never arrive anywhere (the "19k2 on a
        // narrow channel" case); home mode 6 carries traffic fine.
        rig.Ether.DeadModes.Add(1);
        await using var coordinator = new ModeCoordinator(rig.CoordinatorLink, rig.CoordinatorStation, FastOptions);
        var responderRun = RunResponder(rig, out var responderCts);

        var attempt = await coordinator.CoordinateAsync(1).WaitAsync(Timeout);

        attempt.Outcome.Should().Be(ModeCoordOutcome.ProbeDead);
        attempt.CoordinatorToResponder!.Decoded.Should().Be(0);
        attempt.Reverted.Should().BeTrue();
        attempt.HomeLinkAlive.Should().BeTrue("the home-verify probes ran in home mode, which works");

        rig.CoordinatorStation.Mode.Should().Be(6, "coordinator reverted to home");
        rig.ResponderStation.Mode.Should().Be(6, "responder reverted to home on the revert telegram");
        coordinator.CurrentMode.Should().Be(6);

        // Regression (bench-found): the responder must open its home-verify
        // probe window BEFORE applying home — a slow apply (blocked settle
        // frame) must not lose the coordinator's verify probes.
        var events = rig.ResponderStation.Events;
        int homeVerifyCount = events.FindLastIndex(e => e.StartsWith("count:", StringComparison.Ordinal));
        int homeApply = events.FindLastIndex(e => e == "mode:6");
        homeVerifyCount.Should().BeLessThan(homeApply,
            "the home-verify counter opens before the home mode apply");

        await EndSession(coordinator, responderRun, responderCts);
    }

    [Fact]
    public async Task An_unknown_mode_is_rejected_and_nothing_changes()
    {
        var rig = FakeRig.Create();
        await using var coordinator = new ModeCoordinator(rig.CoordinatorLink, rig.CoordinatorStation, FastOptions);
        var responderRun = RunResponder(rig, out var responderCts);

        var attempt = await coordinator.CoordinateAsync(15).WaitAsync(Timeout); // "Set from KISS" — not a real mode

        attempt.Outcome.Should().Be(ModeCoordOutcome.Rejected);
        attempt.Detail.Should().Be("unkmode");
        rig.CoordinatorStation.ModeApplies.Should().BeEmpty("a rejected proposal must not touch the rig");
        rig.ResponderStation.ModeApplies.Should().BeEmpty();

        await EndSession(coordinator, responderRun, responderCts);
    }

    [Fact]
    public async Task A_responder_side_switch_failure_self_reverts_and_the_coordinator_follows()
    {
        var rig = FakeRig.Create();
        rig.ResponderStation.FailChannelSwitchTo = 1;
        await using var coordinator = new ModeCoordinator(rig.CoordinatorLink, rig.CoordinatorStation, FastOptions);
        var responderRun = RunResponder(rig, out var responderCts);

        var attempt = await coordinator.CoordinateAsync(2, channel: 1).WaitAsync(Timeout);

        attempt.Success.Should().BeFalse();
        attempt.Reverted.Should().BeTrue();
        rig.ResponderStation.Mode.Should().Be(6, "the responder self-reverted after its channel apply failed");
        rig.ResponderStation.Channel.Should().Be(0);
        rig.CoordinatorStation.Mode.Should().Be(6, "the responder's revert telegram sent the coordinator home too");
        rig.CoordinatorStation.Channel.Should().Be(0);

        await EndSession(coordinator, responderRun, responderCts);
    }

    [Fact]
    public async Task A_coordinator_side_switch_failure_reverts_both_ends()
    {
        var rig = FakeRig.Create();
        rig.CoordinatorStation.FailChannelSwitchTo = 1;
        await using var coordinator = new ModeCoordinator(rig.CoordinatorLink, rig.CoordinatorStation, FastOptions);
        var responderRun = RunResponder(rig, out var responderCts);

        var attempt = await coordinator.CoordinateAsync(2, channel: 1).WaitAsync(Timeout);

        attempt.Outcome.Should().Be(ModeCoordOutcome.SwitchFailed);
        attempt.Reverted.Should().BeTrue();
        rig.CoordinatorStation.Mode.Should().Be(6);
        rig.ResponderStation.Mode.Should().Be(6, "the coordinator's revert telegram reached the responder");
        rig.ResponderStation.Channel.Should().Be(0);

        await EndSession(coordinator, responderRun, responderCts);
    }

    [Fact]
    public async Task No_responder_means_confirm_timeout_and_an_untouched_rig()
    {
        var rig = FakeRig.Create(); // responder never started
        await using var coordinator = new ModeCoordinator(rig.CoordinatorLink, rig.CoordinatorStation, FastOptions);

        var attempt = await coordinator.CoordinateAsync(7).WaitAsync(Timeout);

        attempt.Outcome.Should().Be(ModeCoordOutcome.ConfirmTimeout);
        rig.CoordinatorStation.ModeApplies.Should().BeEmpty("nothing may switch before a confirm");
    }

    [Fact]
    public async Task The_responder_watchdog_reverts_home_when_the_coordinator_goes_silent_after_commit()
    {
        var rig = FakeRig.Create();
        var options = FastOptions with { ResponderIdleRevert = TimeSpan.FromMilliseconds(400) };
        var responder = new ModeResponder(rig.ResponderLink, rig.ResponderStation, options);
        using var responderCts = new CancellationTokenSource();
        var responderRun = Task.Run(() => responder.RunAsync(responderCts.Token));

        // Drive the wire by hand: propose + commit, then go silent forever.
        int seq = 0;
        await rig.CoordinatorLink.SendAsync(
            new ModeCoordMessage { Action = ModeCoordAction.Propose, Mode = 2 }.ToTelegram(++seq));
        await rig.CoordinatorLink.SendAsync(
            new ModeCoordMessage { Action = ModeCoordAction.Commit, Mode = 2 }.ToTelegram(++seq));

        await WaitUntilAsync(() => rig.ResponderStation.Mode == 2, Timeout,
            "the responder switches on commit");
        await WaitUntilAsync(() => rig.ResponderStation.Mode == 6, Timeout,
            "the watchdog reverts the silent session to home");

        await responderCts.CancelAsync();
        await responderRun.WaitAsync(Timeout);
    }

    [Fact]
    public async Task Bye_sends_an_off_home_responder_back_home_before_it_exits()
    {
        var rig = FakeRig.Create();
        var responder = new ModeResponder(rig.ResponderLink, rig.ResponderStation, FastOptions);
        var responderRun = Task.Run(() => responder.RunAsync(CancellationToken.None));

        int seq = 0;
        await rig.CoordinatorLink.SendAsync(
            new ModeCoordMessage { Action = ModeCoordAction.Propose, Mode = 2 }.ToTelegram(++seq));
        await rig.CoordinatorLink.SendAsync(
            new ModeCoordMessage { Action = ModeCoordAction.Commit, Mode = 2 }.ToTelegram(++seq));
        await WaitUntilAsync(() => rig.ResponderStation.Mode == 2, Timeout, "responder switched");

        await rig.CoordinatorLink.SendAsync(new TuningTelegram(++seq, TuningVerb.Bye, string.Empty));

        (await responderRun.WaitAsync(Timeout)).Should().Be(0);
        rig.ResponderStation.Mode.Should().Be(6, "BY must never strand the rig off-home");
    }

    [Fact]
    public async Task Probe_frames_from_a_stale_attempt_do_not_count_for_the_current_one()
    {
        var station = new FakeStation(new FakeEther());
        using var counter = station.BeginProbeCount(attemptTag: 42);
        await station.TransmitProbesAsync(41, 5); // stale tag
        station.DeliverLoopback(41, 5);

        counter.Count.Should().Be(0);
    }

    private static Task<int> RunResponder(FakeRig rig, out CancellationTokenSource cts)
    {
        var responder = new ModeResponder(rig.ResponderLink, rig.ResponderStation, FastOptions);
        var responderCts = new CancellationTokenSource();
        cts = responderCts;
        return Task.Run(() => responder.RunAsync(responderCts.Token));
    }

    private static async Task EndSession(ModeCoordinator coordinator, Task<int> responderRun, CancellationTokenSource cts)
    {
        await coordinator.EndAsync();
        (await responderRun.WaitAsync(Timeout)).Should().Be(0);
        cts.Dispose();
    }

    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout, string because)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (!condition())
        {
            (DateTimeOffset.UtcNow < deadline).Should().BeTrue(because);
            await Task.Delay(20);
        }
    }

    /// <summary>Two stations sharing one fake ether, linked by the in-memory pair.</summary>
    private sealed class FakeRig
    {
        public required FakeEther Ether { get; init; }

        public required InMemoryTuningLink CoordinatorLink { get; init; }

        public required InMemoryTuningLink ResponderLink { get; init; }

        public required FakeStation CoordinatorStation { get; init; }

        public required FakeStation ResponderStation { get; init; }

        public static FakeRig Create()
        {
            var ether = new FakeEther();
            var (a, b) = InMemoryTuningLink.CreatePair();
            var coordinator = new FakeStation(ether);
            var responder = new FakeStation(ether);
            ether.Stations.Add(coordinator);
            ether.Stations.Add(responder);
            return new FakeRig
            {
                Ether = ether,
                CoordinatorLink = a,
                ResponderLink = b,
                CoordinatorStation = coordinator,
                ResponderStation = responder,
            };
        }
    }

    /// <summary>The shared RF path: probes transmitted by one station arrive at every
    /// OTHER station — unless the sender's mode is dead, or the two ends sit on
    /// different channels.</summary>
    private sealed class FakeEther
    {
        public List<FakeStation> Stations { get; } = [];

        public HashSet<byte> DeadModes { get; } = [];

        public void Propagate(FakeStation sender, int attemptTag, int count)
        {
            if (DeadModes.Contains(sender.Mode))
            {
                return;
            }
            foreach (var station in Stations)
            {
                if (!ReferenceEquals(station, sender) &&
                    station.Channel == sender.Channel &&
                    station.Mode == sender.Mode)
                {
                    station.DeliverLoopback(attemptTag, count);
                }
            }
        }
    }

    /// <summary>Scripted <see cref="IModeCoordStation"/>: instant mode/channel applies
    /// (optionally failing), probe TX via the shared ether, tag-checked counters.</summary>
    private sealed class FakeStation : IModeCoordStation
    {
        private readonly FakeEther ether;
        private readonly ConcurrentBag<Counter> counters = [];

        public FakeStation(FakeEther ether)
        {
            this.ether = ether;
        }

        public byte Mode { get; private set; } = 6;

        public int Channel { get; private set; }

        public List<byte> ModeApplies { get; } = [];

        /// <summary>Ordered station-call trace: <c>mode:N</c> / <c>channel:N</c> /
        /// <c>count:TAG</c> / <c>probes:TAG</c>.</summary>
        public List<string> Events { get; } = [];

        public byte? FailModeSwitchTo { get; set; }

        public int? FailChannelSwitchTo { get; set; }

        public Task ApplyModeAsync(byte mode, CancellationToken cancellationToken = default)
        {
            if (mode == FailModeSwitchTo)
            {
                throw new ModeCoordException($"scripted failure applying mode {mode}");
            }
            Mode = mode;
            ModeApplies.Add(mode);
            Record($"mode:{mode}");
            return Task.CompletedTask;
        }

        public Task ApplyChannelAsync(int channel, CancellationToken cancellationToken = default)
        {
            if (channel == FailChannelSwitchTo)
            {
                throw new ModeCoordException($"scripted failure switching to channel {channel}");
            }
            Channel = channel;
            Record($"channel:{channel}");
            return Task.CompletedTask;
        }

        public Task<ModeProbeTxStats> TransmitProbesAsync(
            int attemptTag, int count, CancellationToken cancellationToken = default)
        {
            Record($"probes:{attemptTag}");
            ether.Propagate(this, attemptTag, count);
            return Task.FromResult(new ModeProbeTxStats(count, count, 42.0));
        }

        public IModeProbeCounter BeginProbeCount(int attemptTag)
        {
            Record($"count:{attemptTag}");
            var counter = new Counter(attemptTag);
            counters.Add(counter);
            return counter;
        }

        private void Record(string entry)
        {
            lock (Events)
            {
                Events.Add(entry);
            }
        }

        public void DeliverLoopback(int attemptTag, int count)
        {
            foreach (var counter in counters)
            {
                counter.Deliver(attemptTag, count);
            }
        }

        private sealed class Counter : IModeProbeCounter
        {
            private readonly int tag;
            private int count;
            private bool disposed;

            public Counter(int tag)
            {
                this.tag = tag;
            }

            public int Count => Volatile.Read(ref count);

            public void Deliver(int attemptTag, int frames)
            {
                if (!disposed && attemptTag == tag)
                {
                    Interlocked.Add(ref count, frames);
                }
            }

            public void Dispose() => disposed = true;
        }
    }
}
