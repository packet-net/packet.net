using Packet.Kiss;
using Packet.Kiss.Adaptive;

namespace Packet.Kiss.NinoTnc.Tests;

public class AdaptiveNinoTncTransportTests
{
    [Fact]
    public async Task Send_Applies_Estimator_Parameters_Before_Tx_Then_Observes_Success()
    {
        var modem = new FakeModem();
        var estimator = new RecordingEstimator
        {
            Recommendation = new KissParameters(TxDelayTenMsUnits: 30, Persistence: null, SlotTimeTenMsUnits: null, TxTailTenMsUnits: null),
        };
        await using var transport = new AdaptiveNinoTncTransport(modem, estimator, TimeProvider.System);

        var payload = new byte[] { 1, 2, 3 };
        await transport.SendAsync("M0LTE-1", payload, TimeSpan.FromSeconds(1));

        modem.TxDelayCalls.Should().Equal(new byte[] { 30 });
        modem.SentFrames.Should().HaveCount(1);
        modem.SentFrames[0].ToArray().Should().Equal(payload);
        estimator.Observations.Should().HaveCount(1);
        estimator.Observations[0].Peer.Should().Be("M0LTE-1");
        estimator.Observations[0].Outcome.Should().Be(FrameOutcome.AcknowledgedFirstTry);
    }

    [Fact]
    public async Task Parameters_Are_Only_Re_Sent_When_The_Value_Changes()
    {
        var modem = new FakeModem();
        var estimator = new RecordingEstimator
        {
            Recommendation = new KissParameters(TxDelayTenMsUnits: 20, Persistence: 100, SlotTimeTenMsUnits: 5, TxTailTenMsUnits: null),
        };
        await using var transport = new AdaptiveNinoTncTransport(modem, estimator);

        for (int i = 0; i < 3; i++)
        {
            await transport.SendAsync("PEER", new byte[] { (byte)i }, TimeSpan.FromSeconds(1));
        }

        modem.TxDelayCalls.Should().Equal(new byte[] { 20 });
        modem.PersistenceCalls.Should().Equal(new byte[] { 100 });
        modem.SlotTimeCalls.Should().Equal(new byte[] { 5 });
        modem.SentFrames.Should().HaveCount(3);
    }

    [Fact]
    public async Task Parameter_Diff_Updates_Live_When_Estimator_Recommendation_Changes()
    {
        var modem = new FakeModem();
        var estimator = new RecordingEstimator
        {
            Recommendation = new KissParameters(TxDelayTenMsUnits: 40, Persistence: null, SlotTimeTenMsUnits: null, TxTailTenMsUnits: null),
        };
        await using var transport = new AdaptiveNinoTncTransport(modem, estimator);

        await transport.SendAsync("PEER", new byte[] { 1 }, TimeSpan.FromSeconds(1));
        estimator.Recommendation = estimator.Recommendation with { TxDelayTenMsUnits = 30 };
        await transport.SendAsync("PEER", new byte[] { 2 }, TimeSpan.FromSeconds(1));
        estimator.Recommendation = estimator.Recommendation with { TxDelayTenMsUnits = 30 }; // unchanged
        await transport.SendAsync("PEER", new byte[] { 3 }, TimeSpan.FromSeconds(1));

        modem.TxDelayCalls.Should().Equal(new byte[] { 40, 30 });
    }

    [Fact]
    public async Task Timeout_Records_AckModeTimedOut_Then_Rethrows()
    {
        var modem = new FakeModem { Behavior = FakeModem.Mode.AlwaysTimeOut };
        var estimator = new RecordingEstimator { Recommendation = KissParameters.SpecDefaults };
        await using var transport = new AdaptiveNinoTncTransport(modem, estimator);

        var act = async () => await transport.SendAsync("PEER", new byte[] { 1 }, TimeSpan.FromMilliseconds(50));
        await act.Should().ThrowAsync<TimeoutException>();

        estimator.Observations.Should().HaveCount(1);
        estimator.Observations[0].Outcome.Should().Be(FrameOutcome.AckModeTimedOut);
    }

    [Fact]
    public async Task Observation_Carries_The_Parameter_Set_That_Was_Actually_Applied()
    {
        var modem = new FakeModem();
        var estimator = new RecordingEstimator
        {
            Recommendation = new KissParameters(TxDelayTenMsUnits: 25, Persistence: 200, SlotTimeTenMsUnits: 7, TxTailTenMsUnits: null),
        };
        await using var transport = new AdaptiveNinoTncTransport(modem, estimator);
        await transport.SendAsync("PEER", new byte[] { 1 }, TimeSpan.FromSeconds(1));

        var sample = estimator.Observations.Single();
        sample.ParametersUsed.TxDelayTenMsUnits.Should().Be((byte)25);
        sample.ParametersUsed.Persistence.Should().Be((byte)200);
        sample.ParametersUsed.SlotTimeTenMsUnits.Should().Be((byte)7);
        sample.PayloadBytes.Should().Be(1);
    }

    [Fact]
    public async Task RecordLoss_Surfaces_A_Lost_Outcome_To_The_Estimator()
    {
        var modem = new FakeModem();
        var estimator = new RecordingEstimator { Recommendation = KissParameters.SpecDefaults };
        await using var transport = new AdaptiveNinoTncTransport(modem, estimator);
        transport.RecordLoss("M0LTE-3", payloadBytes: 250);

        estimator.Observations.Should().HaveCount(1);
        estimator.Observations[0].Peer.Should().Be("M0LTE-3");
        estimator.Observations[0].Outcome.Should().Be(FrameOutcome.Lost);
        estimator.Observations[0].PayloadBytes.Should().Be(250);
    }

    [Fact]
    public async Task RecordRetransmittedAck_Surfaces_A_Retransmit_Outcome()
    {
        var modem = new FakeModem();
        var estimator = new RecordingEstimator { Recommendation = KissParameters.SpecDefaults };
        await using var transport = new AdaptiveNinoTncTransport(modem, estimator);
        transport.RecordRetransmittedAck("M0LTE-2", payloadBytes: 50);

        estimator.Observations.Should().HaveCount(1);
        estimator.Observations[0].Outcome.Should().Be(FrameOutcome.AcknowledgedAfterRetransmit);
    }

    [Fact]
    public async Task Concurrent_Sends_Are_Serialised_Through_The_Lock()
    {
        var modem = new FakeModem
        {
            Behavior = FakeModem.Mode.SuccessDelayed,
            SuccessDelay = TimeSpan.FromMilliseconds(50),
        };
        var estimator = new RecordingEstimator { Recommendation = KissParameters.SpecDefaults };
        await using var transport = new AdaptiveNinoTncTransport(modem, estimator);

        var tasks = Enumerable.Range(0, 5)
            .Select(i => transport.SendAsync("PEER", new byte[] { (byte)i }, TimeSpan.FromSeconds(2)))
            .ToList();
        await Task.WhenAll(tasks);

        // 5 frames sent, in some order. The fake modem records the order it
        // saw them; concurrent invocations must not have overlapped.
        modem.SentFrames.Should().HaveCount(5);
        modem.MaxConcurrentSendObserved.Should().Be(1);
    }

    private sealed class FakeModem : INinoTncModem
    {
        public enum Mode { Success, SuccessDelayed, AlwaysTimeOut }

        public Mode Behavior { get; set; } = Mode.Success;
        public TimeSpan SuccessDelay { get; set; } = TimeSpan.Zero;
        public List<byte> TxDelayCalls { get; } = new();
        public List<byte> PersistenceCalls { get; } = new();
        public List<byte> SlotTimeCalls { get; } = new();
        public List<ReadOnlyMemory<byte>> SentFrames { get; } = new();
        public int MaxConcurrentSendObserved { get; private set; }

        private int currentSendDepth;

        public Task SendFrameAsync(ReadOnlyMemory<byte> ax25Bytes, CancellationToken cancellationToken = default)
        {
            SentFrames.Add(ax25Bytes.ToArray());
            return Task.CompletedTask;
        }

        public async Task<AckModeReceipt> SendFrameWithAckAsync(
            ReadOnlyMemory<byte> ax25Bytes,
            TimeSpan? timeout = null,
            ushort? sequenceTag = null,
            CancellationToken cancellationToken = default)
        {
            int depth = Interlocked.Increment(ref currentSendDepth);
            try
            {
                MaxConcurrentSendObserved = Math.Max(MaxConcurrentSendObserved, depth);

                if (Behavior == Mode.AlwaysTimeOut)
                {
                    await Task.Delay(timeout ?? TimeSpan.FromMilliseconds(200), cancellationToken).ConfigureAwait(false);
                    throw new TimeoutException("fake-modem forced timeout");
                }

                if (Behavior == Mode.SuccessDelayed)
                {
                    await Task.Delay(SuccessDelay, cancellationToken).ConfigureAwait(false);
                }

                SentFrames.Add(ax25Bytes.ToArray());
                var now = DateTimeOffset.UtcNow;
                return new AckModeReceipt(sequenceTag ?? 1, now, now);
            }
            finally
            {
                Interlocked.Decrement(ref currentSendDepth);
            }
        }

        public Task SetTxDelayAsync(byte tenMsUnits, CancellationToken cancellationToken = default)
        {
            TxDelayCalls.Add(tenMsUnits);
            return Task.CompletedTask;
        }

        public Task SetPersistenceAsync(byte value, CancellationToken cancellationToken = default)
        {
            PersistenceCalls.Add(value);
            return Task.CompletedTask;
        }

        public Task SetSlotTimeAsync(byte tenMsUnits, CancellationToken cancellationToken = default)
        {
            SlotTimeCalls.Add(tenMsUnits);
            return Task.CompletedTask;
        }

        public List<byte> TxTailCalls { get; } = new();

        public Task SetTxTailAsync(byte tenMsUnits, CancellationToken cancellationToken = default)
        {
            TxTailCalls.Add(tenMsUnits);
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingEstimator : IAdaptiveParameterEstimator
    {
        public KissParameters Recommendation { get; set; } = KissParameters.SpecDefaults;
        public List<FrameOutcomeSample> Observations { get; } = new();

        public KissParameters Recommend(string peer) => Recommendation;
        public void Observe(FrameOutcomeSample sample) => Observations.Add(sample);
    }
}
