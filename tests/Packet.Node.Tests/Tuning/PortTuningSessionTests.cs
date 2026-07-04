using Packet.Node.Core.Api;
using Packet.Node.Core.Tuning;
using Packet.Tune.Core;

namespace Packet.Node.Tests.Tuning;

/// <summary>
/// The node-side tuning session state machine (<see cref="PortTuningSession"/>): the structured
/// event feed (armed → peer-connected → rounds → terminal), the operator "next round" gate, and —
/// above all — that the port-restore callback fires <b>exactly once on every exit path</b>. Driven
/// over an in-memory link pair against a scripted peer running the real <see cref="TuningSession"/>
/// loop, so the wire protocol is exercised end to end with no hardware.
/// </summary>
[Trait("Category", "Node")]
public sealed class PortTuningSessionTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(15);

    private static TuningSessionOptions NoDelay => new() { PreBurstDelay = TimeSpan.Zero };

    [Fact]
    public async Task Tuned_session_streams_armed_peer_connected_rounds_and_awaiting_then_restores_on_stop()
    {
        var (nodeLink, peerLink) = InMemoryTuningLink.CreatePair();
        int restores = 0;
        var stimulus = new FakeStimulus();
        var session = new PortTuningSession(
            "s1", "vhf-1", "12345678", TuningRole.Tuned, nodeLink, stimulus, meter: null, NoDelay,
            restore: _ => { Interlocked.Increment(ref restores); return ValueTask.CompletedTask; });

        // The peer meter: a solid burst, then a mostly-missed one.
        var peerMeter = new FakeMeter(
            new MeterReport(5, 5, 0, 0, -90.2, AudioLevelDb: -62.5),
            new MeterReport(1, 5, 0, 0, -90.0));

        var sub = session.Subscribe(out var reader);
        session.Start();
        var peerRun = TuningSession.RunMeterAsync(peerLink, peerMeter, NoDelay, TextWriter.Null);

        // Drive: advance once after the first round, stop after the second.
        var events = await DriveAsync(session, reader, advanceRounds: 1);
        (await peerRun.WaitAsync(Timeout)).Should().Be(0);
        sub.Dispose();

        events.Select(e => e.Kind).Should().ContainInOrder(
            "armed", "peer-connected", "round", "awaiting-adjustment", "round", "awaiting-adjustment");
        events[^1].Kind.Should().Be("ended");
        events[^1].State.Should().Be("stopped");

        var rounds = events.Where(e => e.Kind == "round").ToList();
        rounds.Should().HaveCount(2);
        rounds[0].BurstIndex.Should().Be(1);
        rounds[0].Decoded.Should().Be(5);
        rounds[0].Total.Should().Be(5);
        rounds[0].Advice.Should().Be("ok");
        rounds[0].LevelDb.Should().Be(-62.5);
        rounds[0].Note.Should().Contain("leave the pot alone");
        rounds[1].BurstIndex.Should().Be(2);
        rounds[1].Decoded.Should().Be(1);
        rounds[1].Advice.Should().Be("up");
        rounds[1].Note.Should().Contain("turn the deviation up");

        stimulus.Bursts.Should().Equal(5, 5);
        restores.Should().Be(1, "the port is restored exactly once");
    }

    [Fact]
    public async Task A_dead_burst_reports_sweep_advice()
    {
        var (nodeLink, peerLink) = InMemoryTuningLink.CreatePair();
        var session = new PortTuningSession(
            "s1", "vhf-1", "12345678", TuningRole.Tuned, nodeLink, new FakeStimulus(), meter: null, NoDelay,
            restore: _ => ValueTask.CompletedTask);
        var peerMeter = new FakeMeter(new MeterReport(0, 5, 0, 0, -89.5)); // 0 decode, no clip → sweep

        var sub = session.Subscribe(out var reader);
        session.Start();
        var peerRun = TuningSession.RunMeterAsync(peerLink, peerMeter, NoDelay, TextWriter.Null);

        var events = await DriveAsync(session, reader, advanceRounds: 0);
        await peerRun.WaitAsync(Timeout);
        sub.Dispose();

        var round = events.Single(e => e.Kind == "round");
        round.Advice.Should().Be("sweep");
        round.Note.Should().Contain("sweep");
    }

    [Fact]
    public async Task Meter_role_streams_rounds_and_ends_cleanly_when_the_peer_says_goodbye()
    {
        var (nodeLink, peerLink) = InMemoryTuningLink.CreatePair();
        int restores = 0;
        var meter = new FakeMeter(new MeterReport(5, 5, 2, 0, -90.1, AudioLevelDb: -60.0));
        var session = new PortTuningSession(
            "s2", "uhf-2", "87654321", TuningRole.Meter, nodeLink, stimulus: null, meter, NoDelay,
            restore: _ => { Interlocked.Increment(ref restores); return ValueTask.CompletedTask; });

        var sub = session.Subscribe(out var reader);
        session.Start();
        // The peer tuned end runs one round then finishes (BY), which ends the node meter cleanly.
        var peerRun = TuningSession.RunTunedAsync(
            peerLink, new FakeStimulus(), new ScriptedPrompt(false), NoDelay, TextWriter.Null);

        var events = new List<TuningEvent>();
        using var cts = new CancellationTokenSource(Timeout);
        await foreach (var e in reader.ReadAllAsync(cts.Token))
        {
            events.Add(e);
        }
        (await peerRun.WaitAsync(Timeout)).Should().Be(0);
        sub.Dispose();

        events.Select(e => e.Kind).Should().ContainInOrder("armed", "peer-connected", "round");
        events[^1].Kind.Should().Be("ended");
        events[^1].State.Should().Be("ended");
        var round = events.Single(e => e.Kind == "round");
        round.Decoded.Should().Be(5);
        round.Advice.Should().Be("ok");
        round.LevelDb.Should().Be(-60.0);
        restores.Should().Be(1);
    }

    [Fact]
    public async Task A_link_that_closes_without_a_goodbye_ends_in_error_and_still_restores()
    {
        int restores = 0;
        var session = new PortTuningSession(
            "s3", "vhf-1", "12345678", TuningRole.Tuned, new DeadTuningLink(), new FakeStimulus(), meter: null,
            NoDelay, restore: _ => { Interlocked.Increment(ref restores); return ValueTask.CompletedTask; });

        var sub = session.Subscribe(out var reader);
        session.Start();

        var events = new List<TuningEvent>();
        using var cts = new CancellationTokenSource(Timeout);
        await foreach (var e in reader.ReadAllAsync(cts.Token))
        {
            events.Add(e);
        }
        sub.Dispose();

        events[^1].Kind.Should().Be("error");
        events[^1].State.Should().Be("error");
        events[^1].Error.Should().NotBeNullOrEmpty();
        restores.Should().Be(1, "an error path must still restore the port");
    }

    [Fact]
    public async Task Disposing_a_session_that_never_started_still_restores_the_port()
    {
        int restores = 0;
        var session = new PortTuningSession(
            "s4", "vhf-1", "12345678", TuningRole.Tuned, new DeadTuningLink(), new FakeStimulus(), meter: null,
            NoDelay, restore: _ => { Interlocked.Increment(ref restores); return ValueTask.CompletedTask; });

        await session.DisposeAsync();

        restores.Should().Be(1);
    }

    [Fact]
    public async Task Restore_runs_exactly_once_even_when_stop_races_a_natural_end()
    {
        var (nodeLink, peerLink) = InMemoryTuningLink.CreatePair();
        int restores = 0;
        var session = new PortTuningSession(
            "s5", "vhf-1", "12345678", TuningRole.Tuned, nodeLink, new FakeStimulus(), meter: null, NoDelay,
            restore: _ => { Interlocked.Increment(ref restores); return ValueTask.CompletedTask; });
        var peerMeter = new FakeMeter(new MeterReport(5, 5, 0, 0, -90.0));

        var sub = session.Subscribe(out var reader);
        session.Start();
        var peerRun = TuningSession.RunMeterAsync(peerLink, peerMeter, NoDelay, TextWriter.Null);

        _ = await DriveAsync(session, reader, advanceRounds: 0);
        await session.StopAsync(); // second stop — must not restore again
        await session.DisposeAsync(); // third teardown — still no extra restore
        await peerRun.WaitAsync(Timeout);
        sub.Dispose();

        restores.Should().Be(1);
    }

    [Fact]
    public void Signal_next_is_rejected_for_the_meter_role()
    {
        var session = new PortTuningSession(
            "s6", "uhf-2", "12345678", TuningRole.Meter, new DeadTuningLink(), stimulus: null,
            new FakeMeter(new MeterReport(5, 5, 0, 0, null)), NoDelay, restore: _ => ValueTask.CompletedTask);

        session.SignalNext().Should().BeFalse("there is no operator pot on the meter end");
    }

    [Fact]
    public async Task A_late_subscriber_replays_the_full_history()
    {
        var (nodeLink, peerLink) = InMemoryTuningLink.CreatePair();
        var session = new PortTuningSession(
            "s7", "vhf-1", "12345678", TuningRole.Tuned, nodeLink, new FakeStimulus(), meter: null, NoDelay,
            restore: _ => ValueTask.CompletedTask);
        var peerMeter = new FakeMeter(new MeterReport(5, 5, 0, 0, -90.0));

        var driverSub = session.Subscribe(out var reader);
        session.Start();
        var peerRun = TuningSession.RunMeterAsync(peerLink, peerMeter, NoDelay, TextWriter.Null);
        var events = await DriveAsync(session, reader, advanceRounds: 0);
        await peerRun.WaitAsync(Timeout);
        driverSub.Dispose();

        // Subscribe AFTER the session has finished — the replay must carry the whole trend, then end.
        var lateSub = session.Subscribe(out var lateReader);
        var replayed = new List<TuningEvent>();
        using var cts = new CancellationTokenSource(Timeout);
        await foreach (var e in lateReader.ReadAllAsync(cts.Token))
        {
            replayed.Add(e);
        }
        lateSub.Dispose();

        replayed.Select(e => e.Kind).Should().BeEquivalentTo(
            events.Select(e => e.Kind), o => o.WithStrictOrdering());
        replayed.Should().ContainSingle(e => e.Kind == "round");
    }

    /// <summary>
    /// Read the event feed to completion, driving the tuned operator gate: signal "next"
    /// <paramref name="advanceRounds"/> times as rounds complete, then stop.
    /// </summary>
    private static async Task<List<TuningEvent>> DriveAsync(
        PortTuningSession session, System.Threading.Channels.ChannelReader<TuningEvent> reader, int advanceRounds)
    {
        var events = new List<TuningEvent>();
        int advanced = 0;
        using var cts = new CancellationTokenSource(Timeout);
        await foreach (var e in reader.ReadAllAsync(cts.Token))
        {
            events.Add(e);
            if (e.Kind == "awaiting-adjustment")
            {
                if (advanced < advanceRounds)
                {
                    advanced++;
                    session.SignalNext();
                }
                else
                {
                    await session.StopAsync();
                }
            }
        }
        return events;
    }
}
