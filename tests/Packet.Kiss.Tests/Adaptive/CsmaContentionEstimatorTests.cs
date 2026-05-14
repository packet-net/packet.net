using Packet.Kiss.Adaptive;

namespace Packet.Kiss.Tests.Adaptive;

public class CsmaContentionEstimatorTests
{
    [Fact]
    public void Default_Recommendation_Matches_Initial_Persistence_And_SlotTime()
    {
        var estimator = new CsmaContentionEstimator(initialPersistence: 63, initialSlotTime: 10);
        var rec = estimator.Recommend("M0LTE-1");
        rec.Persistence.Should().Be((byte)63);
        rec.SlotTimeTenMsUnits.Should().Be((byte)10);
        rec.TxDelayTenMsUnits.Should().BeNull("CSMA estimator has no opinion on TXDELAY");
        rec.TxTailTenMsUnits.Should().BeNull();
    }

    [Fact]
    public void AckModeTimedOut_Drops_Persistence_And_Raises_SlotTime()
    {
        var estimator = new CsmaContentionEstimator(initialPersistence: 63, initialSlotTime: 10)
        {
            PersistStepDown = 16,
            SlotTimeStepUp = 2,
        };
        estimator.Observe(Sample("PEER", FrameOutcome.AckModeTimedOut));

        estimator.CurrentPersistenceFor("PEER").Should().Be((byte)47);
        estimator.CurrentSlotTimeFor("PEER").Should().Be((byte)12);
    }

    [Fact]
    public void Lost_Drops_Persistence_By_Half_And_Leaves_SlotTime_Alone()
    {
        var estimator = new CsmaContentionEstimator(initialPersistence: 100, initialSlotTime: 10)
        {
            PersistStepDown = 20,
        };
        estimator.Observe(Sample("PEER", FrameOutcome.Lost));

        estimator.CurrentPersistenceFor("PEER").Should().Be((byte)90, "Lost is a softer signal than AckModeTimedOut — half the step");
        estimator.CurrentSlotTimeFor("PEER").Should().Be((byte)10, "Lost shouldn't touch SLOTTIME");
    }

    [Fact]
    public void Long_Success_Run_Raises_Persistence_Step_By_Step()
    {
        var estimator = new CsmaContentionEstimator(initialPersistence: 63, initialSlotTime: 10)
        {
            SuccessesPerPersistStepUp = 3,
            PersistStepUp = 4,
            MaxPersistence = 255,
        };
        // 2 successes: nothing yet.
        estimator.Observe(Sample("PEER", FrameOutcome.AcknowledgedFirstTry));
        estimator.Observe(Sample("PEER", FrameOutcome.AcknowledgedFirstTry));
        estimator.CurrentPersistenceFor("PEER").Should().Be((byte)63);

        // 3rd: step up.
        estimator.Observe(Sample("PEER", FrameOutcome.AcknowledgedFirstTry));
        estimator.CurrentPersistenceFor("PEER").Should().Be((byte)67);
    }

    [Fact]
    public void Retransmit_Resets_Streak_But_Does_Not_Penalise()
    {
        var estimator = new CsmaContentionEstimator(initialPersistence: 100)
        {
            SuccessesPerPersistStepUp = 3,
            PersistStepUp = 5,
        };
        estimator.Observe(Sample("PEER", FrameOutcome.AcknowledgedFirstTry));
        estimator.Observe(Sample("PEER", FrameOutcome.AcknowledgedFirstTry));
        estimator.Observe(Sample("PEER", FrameOutcome.AcknowledgedAfterRetransmit));
        // The retransmit reset the streak; the next two successes shouldn't push past 100 yet.
        estimator.Observe(Sample("PEER", FrameOutcome.AcknowledgedFirstTry));
        estimator.Observe(Sample("PEER", FrameOutcome.AcknowledgedFirstTry));
        estimator.CurrentPersistenceFor("PEER").Should().Be((byte)100);
        // Third success after the reset triggers the step-up.
        estimator.Observe(Sample("PEER", FrameOutcome.AcknowledgedFirstTry));
        estimator.CurrentPersistenceFor("PEER").Should().Be((byte)105);
    }

    [Fact]
    public void Persistence_Clamped_To_Min_And_Max()
    {
        var estimator = new CsmaContentionEstimator(initialPersistence: 20, initialSlotTime: 10)
        {
            MinPersistence = 16,
            PersistStepDown = 100,
            MaxPersistence = 30,
            PersistStepUp = 100,
            SuccessesPerPersistStepUp = 1,
        };
        estimator.Observe(Sample("P1", FrameOutcome.AckModeTimedOut));
        estimator.CurrentPersistenceFor("P1").Should().Be((byte)16, "clamped to MinPersistence");

        estimator.Observe(Sample("P2", FrameOutcome.AcknowledgedFirstTry));
        estimator.CurrentPersistenceFor("P2").Should().Be((byte)30, "clamped to MaxPersistence");
    }

    [Fact]
    public void SlotTime_Clamped_To_Max()
    {
        var estimator = new CsmaContentionEstimator(initialPersistence: 63, initialSlotTime: 48)
        {
            MaxSlotTime = 50,
            SlotTimeStepUp = 10,
        };
        estimator.Observe(Sample("PEER", FrameOutcome.AckModeTimedOut));
        estimator.CurrentSlotTimeFor("PEER").Should().Be((byte)50);
    }

    [Fact]
    public void Per_Peer_State_Is_Independent()
    {
        var estimator = new CsmaContentionEstimator(initialPersistence: 63);
        estimator.Observe(Sample("PEER-A", FrameOutcome.AckModeTimedOut));
        estimator.Recommend("PEER-A").Persistence.Should().BeLessThan((byte)63);
        estimator.Recommend("PEER-B").Persistence.Should().Be((byte)63);
    }

    private static FrameOutcomeSample Sample(string peer, FrameOutcome outcome) =>
        new(peer, outcome, PayloadBytes: 100, ParametersUsed: KissParameters.SpecDefaults,
            AckElapsed: null, ObservedAtUtc: DateTimeOffset.UtcNow);
}
