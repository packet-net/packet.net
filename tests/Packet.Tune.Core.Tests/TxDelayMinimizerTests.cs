using System.Collections.Concurrent;
using System.Globalization;

namespace Packet.Tune.Core.Tests;

/// <summary>
/// Coordinator + meter end-to-end over the in-memory link pair with fake stations
/// (NO RF): the propose→confirm→step→sent→report sweep choreography, the knee + margin
/// arithmetic, the pre-data cross-check plumb, the explicit APPLY — and the safety
/// properties: settle-before-probes ordering, K separate keyings per step, and every
/// failure path restoring the original TXDELAY + channel access.
/// </summary>
public class TxDelayMinimizerTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(15);

    /// <summary>Near-zero timings — no radios to guard in a fake rig.</summary>
    private static TxDelayMinOptions FastOptions(
        int start = 500, int step = 40, int min = 20, int probes = 5) => new()
    {
        StartTxDelayMs = start,
        StepMs = step,
        MinTxDelayMs = min,
        ProbesPerStep = probes,
        ConfirmTimeout = TimeSpan.FromSeconds(5),
        ReportTimeout = TimeSpan.FromSeconds(5),
        PreKeyDelay = TimeSpan.Zero,
        ArrivalGrace = TimeSpan.Zero,
        MeterIdleTimeout = TimeSpan.FromSeconds(30),
    };

    [Fact]
    public async Task Sweep_finds_the_knee_applies_the_margin_and_carries_the_predata_cross_check()
    {
        var rig = FakeRig.Create();
        // Full decode down to 300 ms; the 260 ms step drops probes.
        rig.Ether.DecodedAt = ms => ms >= 300 ? 5 : 3;
        var options = FastOptions(start: 500, step: 40, min: 20);

        var meterRun = RunMeter(rig, options, out var meterCts);
        await using var minimizer = new TxDelayMinimizer(rig.CoordinatorLink, rig.Coordinator, options);

        var result = await minimizer.RunSweepAsync().WaitAsync(Timeout);

        result.Outcome.Should().Be(TxDelaySweepOutcome.Complete);
        result.Steps.Select(s => s.TxDelayMs).Should().Equal(500, 460, 420, 380, 340, 300, 260);
        result.Steps.Select(s => s.Decoded).Should().Equal(5, 5, 5, 5, 5, 5, 3);
        result.KneeMs.Should().Be(300);
        result.RecommendedMs.Should().Be(380, "knee 300 + max(2×40, 25%×300=75) = 380");
        result.FloorReached.Should().BeFalse();
        result.Restored.Should().BeTrue();
        result.RestoredToMs.Should().Be(500);

        // The pre-data cross-check: the meter measured each step's as-heard TXDELAY
        // (commanded + the fake rig's 55 ms constant) and it rode the report telegrams.
        result.Steps.Select(s => s.MedianPreDataCarrierMs).Should().Equal(555, 515, 475, 435, 395, 355, 315);

        // The sweep never leaves the reduced TXDELAY behind: the last set is the start
        // value, after the last probe keying, and channel access is restored last.
        var ops = rig.Coordinator.Ops.ToList();
        ops.Last().Should().Be("restore-channel");
        ops.FindLastIndex(o => o == "set:500").Should().BeGreaterThan(
            ops.FindLastIndex(o => o.StartsWith("key:", StringComparison.Ordinal)));

        await minimizer.EndAsync(result.RecommendedMs).WaitAsync(Timeout);
        (await meterRun.WaitAsync(Timeout)).Should().Be(0, "done ends the meter loop cleanly");
        meterCts.Dispose();
    }

    [Fact]
    public async Task Every_step_sets_then_settles_then_keys_k_separate_probes()
    {
        var rig = FakeRig.Create();
        rig.Ether.DecodedAt = ms => ms >= 420 ? 5 : 0;
        var options = FastOptions(start: 500, step: 40);

        var meterRun = RunMeter(rig, options, out var meterCts);
        await using var minimizer = new TxDelayMinimizer(rig.CoordinatorLink, rig.Coordinator, options);
        await minimizer.RunSweepAsync().WaitAsync(Timeout);
        await minimizer.EndAsync().WaitAsync(Timeout);
        await meterRun.WaitAsync(Timeout);
        meterCts.Dispose();

        var ops = rig.Coordinator.Ops.ToList();
        ops[0].Should().Be("pin", "channel access is pinned before anything keys");

        // Per step (500/460/420/380): the KISS set and its settle frame precede the
        // step's probes, and the probes are K SEPARATE keyings — never one train.
        foreach (int ms in new[] { 500, 460, 420, 380 })
        {
            int set = ops.IndexOf($"set:{ms}");
            int settle = ops.IndexOf($"settle:{ms}");
            var keys = ops.Where(o => o.StartsWith($"key:{ms}:", StringComparison.Ordinal)).ToList();

            set.Should().BeGreaterThanOrEqualTo(0, $"step {ms} must set the TXDELAY");
            settle.Should().Be(set + 1, $"the settle frame follows the {ms} ms set immediately");
            keys.Should().HaveCount(5, $"step {ms} keys 5 separate probes");
            ops.IndexOf(keys[0]).Should().BeGreaterThan(settle,
                $"no probe of step {ms} may key before its settle frame absorbed the parameter change");
        }
    }

    [Fact]
    public async Task A_link_that_is_not_solid_at_the_start_produces_no_recommendation()
    {
        var rig = FakeRig.Create();
        rig.Ether.DecodedAt = _ => 3;   // marginal everywhere — stepping down carries no information
        var options = FastOptions();

        var meterRun = RunMeter(rig, options, out var meterCts);
        await using var minimizer = new TxDelayMinimizer(rig.CoordinatorLink, rig.Coordinator, options);
        var result = await minimizer.RunSweepAsync().WaitAsync(Timeout);

        result.Outcome.Should().Be(TxDelaySweepOutcome.NotSolidAtStart);
        result.Steps.Should().HaveCount(1);
        result.KneeMs.Should().BeNull();
        result.RecommendedMs.Should().BeNull();
        result.Restored.Should().BeTrue("even a one-step sweep restores TXDELAY + channel access");
        rig.Coordinator.Ops.Should().Contain("restore-channel");

        await minimizer.EndAsync().WaitAsync(Timeout);
        await meterRun.WaitAsync(Timeout);
        meterCts.Dispose();
    }

    [Fact]
    public async Task Reaching_the_floor_still_solid_reports_the_floor_as_the_knee()
    {
        var rig = FakeRig.Create();
        rig.Ether.DecodedAt = _ => 5;   // solid all the way down
        var options = FastOptions(start: 100, step: 40, min: 20);

        var meterRun = RunMeter(rig, options, out var meterCts);
        await using var minimizer = new TxDelayMinimizer(rig.CoordinatorLink, rig.Coordinator, options);
        var result = await minimizer.RunSweepAsync().WaitAsync(Timeout);

        result.Outcome.Should().Be(TxDelaySweepOutcome.Complete);
        result.Steps.Select(s => s.TxDelayMs).Should().Equal(100, 60, 20);
        result.FloorReached.Should().BeTrue();
        result.KneeMs.Should().Be(20);
        result.RecommendedMs.Should().Be(100, "knee 20 + max(2×40, 25%×20=5) = 100");

        await minimizer.EndAsync().WaitAsync(Timeout);
        await meterRun.WaitAsync(Timeout);
        meterCts.Dispose();
    }

    [Fact]
    public async Task A_mid_sweep_link_failure_restores_the_original_txdelay_and_channel_access()
    {
        var rig = FakeRig.Create();
        rig.Ether.DecodedAt = _ => 5;
        var options = FastOptions(start: 500, step: 40);

        // Sends 1–3 (propose, step 500, sent 500) succeed; everything after — including
        // the abort notification itself — fails. Restore must still happen.
        var failing = new FailingLink(rig.CoordinatorLink, failAfterSends: 3);

        var meterRun = RunMeter(rig, options, out var meterCts);
        await using var minimizer = new TxDelayMinimizer(failing, rig.Coordinator, options);
        var result = await minimizer.RunSweepAsync().WaitAsync(Timeout);

        result.Outcome.Should().Be(TxDelaySweepOutcome.LinkFailed);
        result.Steps.Should().HaveCount(1, "only the 500 ms step completed before the link died");
        result.Restored.Should().BeTrue();

        var ops = rig.Coordinator.Ops.ToList();
        ops.Last().Should().Be("restore-channel");
        ops.FindLastIndex(o => o == "set:500").Should().BeGreaterThan(
            ops.FindLastIndex(o => o.StartsWith("key:", StringComparison.Ordinal)),
            "the original TXDELAY is re-set after the failure");

        meterCts.Cancel();
        await meterRun.WaitAsync(Timeout);
        meterCts.Dispose();
    }

    [Fact]
    public async Task A_station_failure_mid_sweep_aborts_and_restores()
    {
        var rig = FakeRig.Create();
        rig.Ether.DecodedAt = _ => 5;
        rig.Coordinator.FailSetAt = 460;   // the second step's KISS set blows up
        var options = FastOptions(start: 500, step: 40);

        var meterRun = RunMeter(rig, options, out var meterCts);
        await using var minimizer = new TxDelayMinimizer(rig.CoordinatorLink, rig.Coordinator, options);
        var result = await minimizer.RunSweepAsync().WaitAsync(Timeout);

        result.Outcome.Should().Be(TxDelaySweepOutcome.StationFailed);
        result.Restored.Should().BeTrue();
        rig.Coordinator.Ops.Last().Should().Be("restore-channel");
        rig.Coordinator.Ops.Should().Contain("set:500");

        meterCts.Cancel();
        await meterRun.WaitAsync(Timeout);
        meterCts.Dispose();
    }

    [Fact]
    public async Task The_meter_rejects_a_runaway_probe_count_and_nothing_is_keyed()
    {
        var rig = FakeRig.Create();
        var options = FastOptions() with { ProbesPerStep = 25 };   // > MaxProbesPerStep

        var meterRun = RunMeter(rig, options, out var meterCts);
        await using var minimizer = new TxDelayMinimizer(rig.CoordinatorLink, rig.Coordinator, options);
        var result = await minimizer.RunSweepAsync().WaitAsync(Timeout);

        result.Outcome.Should().Be(TxDelaySweepOutcome.Rejected);
        result.Detail.Should().Be("badk");
        rig.Coordinator.Ops.Should().BeEmpty("a rejected sweep never pins, sets or keys anything");

        meterCts.Cancel();
        await meterRun.WaitAsync(Timeout);
        meterCts.Dispose();
    }

    [Fact]
    public async Task Apply_verifies_and_leaves_the_value_in_place()
    {
        var rig = FakeRig.Create();
        rig.Ether.DecodedAt = ms => ms >= 300 ? 5 : 0;
        var options = FastOptions(start: 500);

        var meterRun = RunMeter(rig, options, out var meterCts);
        await using var minimizer = new TxDelayMinimizer(rig.CoordinatorLink, rig.Coordinator, options);

        var apply = await minimizer.ApplyAsync(380).WaitAsync(Timeout);

        apply.Verified.Should().BeTrue();
        apply.Decoded.Should().Be(5);
        apply.MedianPreDataCarrierMs.Should().Be(435, "the meter heard 380 + the rig's 55 ms constant");

        var ops = rig.Coordinator.Ops.ToList();
        ops.FindLastIndex(o => o.StartsWith("set:", StringComparison.Ordinal))
            .Should().Be(ops.IndexOf("set:380"), "a verified apply leaves 380 in place — no restore set");
        ops.Last().Should().Be("restore-channel", "channel access is always restored");

        await minimizer.EndAsync(380).WaitAsync(Timeout);
        await meterRun.WaitAsync(Timeout);
        meterCts.Dispose();
    }

    [Fact]
    public async Task A_failed_apply_verify_restores_the_fallback_txdelay()
    {
        var rig = FakeRig.Create();
        rig.Ether.DecodedAt = ms => ms >= 500 ? 5 : 2;   // 380 will NOT verify
        var options = FastOptions(start: 500);

        var meterRun = RunMeter(rig, options, out var meterCts);
        await using var minimizer = new TxDelayMinimizer(rig.CoordinatorLink, rig.Coordinator, options);

        var apply = await minimizer.ApplyAsync(380).WaitAsync(Timeout);

        apply.Verified.Should().BeFalse();
        apply.Decoded.Should().Be(2);
        apply.Detail.Should().Contain("restored to 500 ms");

        var ops = rig.Coordinator.Ops.ToList();
        ops.FindLastIndex(o => o == "set:500").Should().BeGreaterThan(ops.IndexOf("set:380"));

        meterCts.Cancel();
        await meterRun.WaitAsync(Timeout);
        meterCts.Dispose();
    }

    // ─── the fake rig ────────────────────────────────────────────────────────

    private static Task<int> RunMeter(FakeRig rig, TxDelayMinOptions options, out CancellationTokenSource cts)
    {
        var responder = new TxDelayMinResponder(rig.MeterLink, rig.Meter, options);
        var meterCts = new CancellationTokenSource();
        cts = meterCts;
        return Task.Run(() => responder.RunAsync(meterCts.Token));
    }

    /// <summary>Two fake stations joined by a scripted ether over the in-memory link pair.</summary>
    private sealed class FakeRig
    {
        public required FakeEther Ether { get; init; }

        public required InMemoryTuningLink CoordinatorLink { get; init; }

        public required InMemoryTuningLink MeterLink { get; init; }

        public required FakeStation Coordinator { get; init; }

        public required FakeStation Meter { get; init; }

        public static FakeRig Create()
        {
            var ether = new FakeEther();
            var (a, b) = InMemoryTuningLink.CreatePair();
            return new FakeRig
            {
                Ether = ether,
                CoordinatorLink = a,
                MeterLink = b,
                Coordinator = new FakeStation(ether),
                Meter = new FakeStation(ether),
            };
        }
    }

    /// <summary>
    /// The scripted RF path: how many of a step's probes decode at the meter, as a
    /// function of the coordinator's commanded TXDELAY, plus the constant rig overhead
    /// the meter's pre-data measurement carries. Probe delivery is buffered per tag so
    /// the fake never races the meter's counter registration (the real link's delivery
    /// receipts + wedge-guard delay serve that role on hardware).
    /// </summary>
    private sealed class FakeEther
    {
        private readonly object gate = new();
        private readonly Dictionary<int, FakeCounter> counters = [];
        private readonly Dictionary<int, (int Decoded, double? PreMs)> pending = [];

        public Func<int, int> DecodedAt { get; set; } = _ => 5;

        public double PreDataOverheadMs { get; set; } = 55;

        public int CoordinatorTxDelayMs { get; set; } = -1;

        public void Register(int tag, FakeCounter counter)
        {
            lock (gate)
            {
                counters[tag] = counter;
                if (pending.Remove(tag, out var delivery))
                {
                    counter.Deliver(delivery.Decoded, delivery.PreMs);
                }
            }
        }

        public void Propagate(int tag, int count)
        {
            int decoded = Math.Min(DecodedAt(CoordinatorTxDelayMs), count);
            double? pre = decoded > 0 ? CoordinatorTxDelayMs + PreDataOverheadMs : null;
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

    /// <summary>Scripted <see cref="ITxDelayMinStation"/>: records every hardware-facing
    /// operation (pin / set / settle / per-probe keyings / restore) for the ordering and
    /// abort-safety assertions, and propagates probes over the shared ether.</summary>
    private sealed class FakeStation : ITxDelayMinStation
    {
        private readonly FakeEther ether;

        public FakeStation(FakeEther ether)
        {
            this.ether = ether;
        }

        public ConcurrentQueue<string> Ops { get; } = [];

        /// <summary>When set, <see cref="SetTxDelayAsync"/> for this value throws.</summary>
        public int? FailSetAt { get; set; }

        public Task PinChannelAccessAsync(CancellationToken cancellationToken = default)
        {
            Ops.Enqueue("pin");
            return Task.CompletedTask;
        }

        public Task RestoreChannelAccessAsync(CancellationToken cancellationToken = default)
        {
            Ops.Enqueue("restore-channel");
            return Task.CompletedTask;
        }

        public Task SetTxDelayAsync(int txDelayMs, CancellationToken cancellationToken = default)
        {
            if (FailSetAt == txDelayMs)
            {
                throw new TxDelayMinException($"scripted set failure at {txDelayMs} ms");
            }
            Ops.Enqueue(string.Create(CultureInfo.InvariantCulture, $"set:{txDelayMs}"));
            // The settle frame is part of the station's set contract (the NinoTNC applies
            // the change from the SECOND frame) — model it so ordering is assertable.
            Ops.Enqueue(string.Create(CultureInfo.InvariantCulture, $"settle:{txDelayMs}"));
            ether.CoordinatorTxDelayMs = txDelayMs;
            return Task.CompletedTask;
        }

        public Task<ModeProbeTxStats> TransmitProbesAsync(
            int stepTag, int count, CancellationToken cancellationToken = default)
        {
            for (int i = 1; i <= count; i++)
            {
                // One entry per keying — the "separate keyings" record, keyed by the
                // CURRENT commanded TXDELAY so the per-step assertions can find them.
                Ops.Enqueue(string.Create(
                    CultureInfo.InvariantCulture, $"key:{ether.CoordinatorTxDelayMs}:{i}"));
            }
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

    /// <summary>Delegates to the real in-memory link but fails every send after the
    /// first <c>failAfterSends</c> — including the abort notification, proving restore
    /// does not depend on the link.</summary>
    private sealed class FailingLink : ITuningLink
    {
        private readonly InMemoryTuningLink inner;
        private readonly int failAfterSends;
        private int sends;

        public FailingLink(InMemoryTuningLink inner, int failAfterSends)
        {
            this.inner = inner;
            this.failAfterSends = failAfterSends;
        }

        public Task SendAsync(TuningTelegram telegram, CancellationToken cancellationToken = default)
        {
            if (Interlocked.Increment(ref sends) > failAfterSends)
            {
                throw new TuningLinkException("scripted link failure");
            }
            return inner.SendAsync(telegram, cancellationToken);
        }

        public IAsyncEnumerable<TuningTelegram> ReceiveAsync(CancellationToken cancellationToken = default) =>
            inner.ReceiveAsync(cancellationToken);

        public ValueTask DisposeAsync() => inner.DisposeAsync();
    }
}
