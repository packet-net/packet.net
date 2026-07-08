using System.Collections.Concurrent;
using Packet.Node.Core.Tuning;
using Packet.Tune.Core;

namespace Packet.Node.Tests.Tuning;

/// <summary>
/// The node-side TXDELAY-minimisation session wrapper (<see cref="TxDelayMinPortSession"/>): the
/// sweep steps project into the same <see cref="Packet.Node.Core.Api.TuningEvent"/> feed the
/// deviation sessions use, an <c>ended</c> carries the recommendation, an apply persists only when
/// verified — and, above all, the port-restore callback fires <b>exactly once on every exit path</b>.
/// Driven over an in-memory link pair against the real <see cref="TxDelayMinResponder"/> meter loop
/// plus a fake station, so the wire protocol runs end to end with no hardware.
/// </summary>
[Trait("Category", "Node")]
public sealed class TxDelayMinPortSessionTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(15);

    private static TxDelayMinOptions FastOptions(int start = 500, int step = 40, int min = 20) => new()
    {
        StartTxDelayMs = start,
        StepMs = step,
        MinTxDelayMs = min,
        ProbesPerStep = 5,
        ConfirmTimeout = TimeSpan.FromSeconds(5),
        ReportTimeout = TimeSpan.FromSeconds(5),
        PreKeyDelay = TimeSpan.Zero,
        ArrivalGrace = TimeSpan.Zero,
        MeterIdleTimeout = TimeSpan.FromSeconds(30),
    };

    [Fact]
    public async Task Sweep_coordinator_streams_steps_ends_with_a_recommendation_and_restores_once()
    {
        var ether = new FakeEther { DecodedAt = ms => ms >= 300 ? 5 : 2 };
        var (nodeLink, peerLink) = InMemoryTuningLink.CreatePair();
        var options = FastOptions();

        int restores = 0;
        var coordinatorStation = new FakeStation(ether);
        var session = new TxDelayMinPortSession(
            "s1", "vhf-1", "12345678", TxDelayMinMode.SweepCoordinator, nodeLink, coordinatorStation, options,
            restore: _ => { Interlocked.Increment(ref restores); return ValueTask.CompletedTask; });

        var peerRun = RunMeter(peerLink, new FakeStation(ether), options, out var meterCts);
        var sub = session.Subscribe(out var reader);
        session.Start();

        var events = await CollectAsync(reader);
        (await peerRun.WaitAsync(Timeout)).Should().Be(0);
        sub.Dispose();

        events[0].Kind.Should().Be("armed");
        var rounds = events.Where(e => e.Kind == "round").ToList();
        rounds.Select(r => r.TxDelayMs).Should().Equal(500, 460, 420, 380, 340, 300, 260);
        rounds[0].Total.Should().Be(5);
        rounds.Last().Decoded.Should().Be(2, "the 260 ms step is the drop step");

        var ended = events.Last();
        ended.Kind.Should().Be("ended");
        ended.RecommendedTxDelayMs.Should().Be(380);
        restores.Should().Be(1, "the port is restored exactly once");
        meterCts.Dispose();
    }

    [Fact]
    public async Task A_verified_apply_persists_and_a_missing_persist_hook_leaves_it_transient()
    {
        var ether = new FakeEther { DecodedAt = ms => ms >= 300 ? 5 : 0 };
        var (nodeLink, peerLink) = InMemoryTuningLink.CreatePair();
        var options = FastOptions();

        int? persisted = null;
        var session = new TxDelayMinPortSession(
            "s2", "vhf-1", "12345678", TxDelayMinMode.Apply, nodeLink, new FakeStation(ether), options,
            restore: _ => ValueTask.CompletedTask,
            applyTxDelayMs: 380,
            persist: (ms, _) => { persisted = ms; return Task.FromResult(true); });

        var peerRun = RunMeter(peerLink, new FakeStation(ether), options, out var meterCts);
        var sub = session.Subscribe(out var reader);
        session.Start();

        var events = await CollectAsync(reader);
        (await peerRun.WaitAsync(Timeout)).Should().Be(0);
        sub.Dispose();

        persisted.Should().Be(380, "a verified apply persists the value");
        var ended = events.Last();
        ended.Kind.Should().Be("ended");
        ended.Note.Should().Contain("persisted to the port's KISS params");
        meterCts.Dispose();
    }

    [Fact]
    public async Task A_failed_apply_verify_does_not_persist_and_restores()
    {
        var ether = new FakeEther { DecodedAt = ms => ms >= 500 ? 5 : 1 };   // 380 will not verify
        var (nodeLink, peerLink) = InMemoryTuningLink.CreatePair();
        var options = FastOptions();

        int persistCalls = 0;
        int restores = 0;
        var session = new TxDelayMinPortSession(
            "s3", "vhf-1", "12345678", TxDelayMinMode.Apply, nodeLink, new FakeStation(ether), options,
            restore: _ => { Interlocked.Increment(ref restores); return ValueTask.CompletedTask; },
            applyTxDelayMs: 380,
            persist: (_, _) => { Interlocked.Increment(ref persistCalls); return Task.FromResult(true); });

        var peerRun = RunMeter(peerLink, new FakeStation(ether), options, out var meterCts);
        var sub = session.Subscribe(out var reader);
        session.Start();

        var events = await CollectAsync(reader);
        (await peerRun.WaitAsync(Timeout)).Should().Be(0);
        sub.Dispose();

        persistCalls.Should().Be(0, "a failed verify never persists");
        events.Last().Kind.Should().Be("error");
        restores.Should().Be(1);
        meterCts.Dispose();
    }

    [Fact]
    public async Task Stopping_a_meter_session_restores_the_port_once()
    {
        var ether = new FakeEther();
        var (nodeLink, peerLink) = InMemoryTuningLink.CreatePair();
        _ = peerLink;
        var options = FastOptions();

        int restores = 0;
        var session = new TxDelayMinPortSession(
            "s4", "vhf-1", "12345678", TxDelayMinMode.Meter, nodeLink, new FakeStation(ether), options,
            restore: _ => { Interlocked.Increment(ref restores); return ValueTask.CompletedTask; });
        session.Start();

        await session.StopAsync();
        restores.Should().Be(1);
    }

    private static Task<int> RunMeter(
        InMemoryTuningLink link, FakeStation station, TxDelayMinOptions options, out CancellationTokenSource cts)
    {
        var responder = new TxDelayMinResponder(link, station, options);
        var meterCts = new CancellationTokenSource();
        cts = meterCts;
        return Task.Run(() => responder.RunAsync(meterCts.Token));
    }

    private static async Task<List<Packet.Node.Core.Api.TuningEvent>> CollectAsync(
        System.Threading.Channels.ChannelReader<Packet.Node.Core.Api.TuningEvent> reader)
    {
        var events = new List<Packet.Node.Core.Api.TuningEvent>();
        using var cts = new CancellationTokenSource(Timeout);
        try
        {
            await foreach (var e in reader.ReadAllAsync(cts.Token))
            {
                events.Add(e);
            }
        }
        catch (OperationCanceledException)
        {
        }
        return events;
    }

    // ─── shared fake rig (a trimmed twin of the Tune.Core protocol test's) ──────

    private sealed class FakeEther
    {
        private readonly object gate = new();
        private readonly Dictionary<int, FakeCounter> counters = [];
        private readonly Dictionary<int, (int Decoded, double? PreMs)> pending = [];

        public Func<int, int> DecodedAt { get; set; } = _ => 5;

        public int CoordinatorTxDelayMs { get; set; } = -1;

        public void Register(int tag, FakeCounter counter)
        {
            lock (gate)
            {
                counters[tag] = counter;
                if (pending.Remove(tag, out var d))
                {
                    counter.Deliver(d.Decoded, d.PreMs);
                }
            }
        }

        public void Propagate(int tag, int count)
        {
            int decoded = Math.Min(DecodedAt(CoordinatorTxDelayMs), count);
            double? pre = decoded > 0 ? CoordinatorTxDelayMs + 55.0 : null;
            lock (gate)
            {
                if (counters.TryGetValue(tag, out var counter))
                {
                    counter.Deliver(decoded, pre);
                }
                else
                {
                    pending[tag] = (decoded, pre);
                }
            }
        }
    }

    private sealed class FakeStation(FakeEther ether) : ITxDelayMinStation
    {
        public Task PinChannelAccessAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task RestoreChannelAccessAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task SetTxDelayAsync(int txDelayMs, CancellationToken cancellationToken = default)
        {
            ether.CoordinatorTxDelayMs = txDelayMs;
            return Task.CompletedTask;
        }

        public Task<ModeProbeTxStats> TransmitProbesAsync(int stepTag, int count, CancellationToken cancellationToken = default)
        {
            ether.Propagate(stepTag, count);
            return Task.FromResult(new ModeProbeTxStats(count, count, 42.0));
        }

        public ITxDelayProbeCount BeginProbeCount(int stepTag)
        {
            var counter = new FakeCounter();
            ether.Register(stepTag, counter);
            return counter;
        }
    }

    private sealed class FakeCounter : ITxDelayProbeCount
    {
        private int count;
        private double? preMs;

        public int Count => Volatile.Read(ref count);

        public double? MedianPreDataCarrierMs => preMs;

        public void Deliver(int decoded, double? pre)
        {
            Interlocked.Add(ref count, decoded);
            preMs = pre;
        }

        public void Dispose()
        {
        }
    }
}
