using Packet.Kiss.Adaptive;

namespace Packet.Kiss.Tests.Adaptive;

public class TxDelayHillClimbTests
{
    [Fact]
    public void Default_Recommendation_Matches_Initial_TxDelay()
    {
        var estimator = new TxDelayHillClimbEstimator(initialTxDelay: 40);
        var rec = estimator.Recommend("G7XYZ-1");
        rec.TxDelayTenMsUnits.Should().Be((byte)40);
        rec.Persistence.Should().BeNull();
        rec.SlotTimeTenMsUnits.Should().BeNull();
        rec.TxTailTenMsUnits.Should().BeNull();
    }

    [Fact]
    public void Step_Down_Occurs_After_Configured_Successes()
    {
        var estimator = new TxDelayHillClimbEstimator(initialTxDelay: 50)
        {
            SuccessesPerStepDown = 3,
            StepUnits = 2,
            MinTxDelay = 10,
        };

        for (int i = 0; i < 2; i++)
        {
            estimator.Observe(Sample("M0LTE", FrameOutcome.AcknowledgedFirstTry));
            estimator.CurrentTxDelayFor("M0LTE").Should().Be((byte)50, "no step until threshold");
        }

        estimator.Observe(Sample("M0LTE", FrameOutcome.AcknowledgedFirstTry));
        estimator.CurrentTxDelayFor("M0LTE").Should().Be((byte)48, "stepped down by StepUnits");
    }

    [Fact]
    public void Loss_Bumps_TxDelay_Up_By_Loss_Penalty()
    {
        var estimator = new TxDelayHillClimbEstimator(initialTxDelay: 40)
        {
            LossPenaltyUnits = 7,
            MaxTxDelay = 100,
        };
        estimator.Observe(Sample("M0LTE", FrameOutcome.Lost));
        estimator.CurrentTxDelayFor("M0LTE").Should().Be((byte)47);
    }

    [Fact]
    public void AckMode_Timeout_Counts_As_Loss()
    {
        var estimator = new TxDelayHillClimbEstimator(initialTxDelay: 30) { LossPenaltyUnits = 5 };
        estimator.Observe(Sample("M0LTE", FrameOutcome.AckModeTimedOut));
        estimator.CurrentTxDelayFor("M0LTE").Should().Be((byte)35);
    }

    [Fact]
    public void Retransmit_Resets_Streak_But_Does_Not_Penalise()
    {
        var estimator = new TxDelayHillClimbEstimator(initialTxDelay: 50)
        {
            SuccessesPerStepDown = 3,
            StepUnits = 1,
        };
        estimator.Observe(Sample("M0LTE", FrameOutcome.AcknowledgedFirstTry));
        estimator.Observe(Sample("M0LTE", FrameOutcome.AcknowledgedFirstTry));
        estimator.Observe(Sample("M0LTE", FrameOutcome.AcknowledgedAfterRetransmit));
        // Streak reset → next two successes are not enough to trigger step-down.
        estimator.Observe(Sample("M0LTE", FrameOutcome.AcknowledgedFirstTry));
        estimator.Observe(Sample("M0LTE", FrameOutcome.AcknowledgedFirstTry));
        estimator.CurrentTxDelayFor("M0LTE").Should().Be((byte)50);

        // The third success after the retransmit-reset *does* trigger.
        estimator.Observe(Sample("M0LTE", FrameOutcome.AcknowledgedFirstTry));
        estimator.CurrentTxDelayFor("M0LTE").Should().Be((byte)49);
    }

    [Fact]
    public void TxDelay_Is_Clamped_To_Min_And_Max()
    {
        var estimator = new TxDelayHillClimbEstimator(initialTxDelay: 10)
        {
            MinTxDelay = 8,
            MaxTxDelay = 12,
            SuccessesPerStepDown = 1,
            StepUnits = 5,    // would otherwise overshoot the floor
            LossPenaltyUnits = 50,
        };

        // Drive down — should clamp at MinTxDelay (8) instead of going negative.
        for (int i = 0; i < 10; i++)
        {
            estimator.Observe(Sample("PEER", FrameOutcome.AcknowledgedFirstTry));
        }
        estimator.CurrentTxDelayFor("PEER").Should().Be((byte)8);

        // Drive up — should clamp at MaxTxDelay (12), not wrap or overflow.
        estimator.Observe(Sample("PEER", FrameOutcome.Lost));
        estimator.CurrentTxDelayFor("PEER").Should().Be((byte)12);
    }

    [Fact]
    public void Peers_Have_Independent_State()
    {
        var estimator = new TxDelayHillClimbEstimator(initialTxDelay: 40);
        estimator.Observe(Sample("PEER-A", FrameOutcome.Lost));
        estimator.Recommend("PEER-A").TxDelayTenMsUnits.Should().BeGreaterThan((byte)40);
        estimator.Recommend("PEER-B").TxDelayTenMsUnits.Should().Be((byte)40);
    }

    [Fact]
    public void Constructor_Rejects_Zero_Initial_TxDelay()
    {
        ((Action)(() => new TxDelayHillClimbEstimator(initialTxDelay: 0)))
            .Should().Throw<ArgumentOutOfRangeException>();
    }

    private static FrameOutcomeSample Sample(string peer, FrameOutcome outcome) =>
        new(peer, outcome, PayloadBytes: 100, ParametersUsed: KissParameters.SpecDefaults,
            AckElapsed: null, ObservedAtUtc: DateTimeOffset.UtcNow);
}

public class KissParametersTests
{
    [Fact]
    public void Spec_Defaults_Match_Kiss_TNC_Spec()
    {
        var defaults = KissParameters.SpecDefaults;
        defaults.TxDelayTenMsUnits.Should().Be((byte)50);
        defaults.Persistence.Should().Be((byte)63);
        defaults.SlotTimeTenMsUnits.Should().Be((byte)10);
        defaults.TxTailTenMsUnits.Should().Be((byte)0);
    }

    [Fact]
    public void Override_Replaces_Only_Non_Null_Fields()
    {
        var baseline = KissParameters.SpecDefaults;
        var adaptive = new KissParameters(TxDelayTenMsUnits: 30, Persistence: null, SlotTimeTenMsUnits: null, TxTailTenMsUnits: null);
        var merged = baseline.Override(adaptive);
        merged.TxDelayTenMsUnits.Should().Be((byte)30, "adaptive value wins where non-null");
        merged.Persistence.Should().Be((byte)63, "baseline preserved where adaptive is null");
        merged.SlotTimeTenMsUnits.Should().Be((byte)10);
        merged.TxTailTenMsUnits.Should().Be((byte)0);
    }
}
