using Packet.Kiss.Adaptive;

namespace Packet.Kiss.Tests.Adaptive;

public class CompositeAdaptiveEstimatorTests
{
    [Fact]
    public void Composite_Of_TxDelay_And_Csma_Produces_All_Fields()
    {
        var composite = new CompositeAdaptiveEstimator(
            new TxDelayHillClimbEstimator(initialTxDelay: 25),
            new CsmaContentionEstimator(initialPersistence: 100, initialSlotTime: 8));

        var rec = composite.Recommend("M0LTE-1");
        rec.TxDelayTenMsUnits.Should().Be((byte)25);
        rec.Persistence.Should().Be((byte)100);
        rec.SlotTimeTenMsUnits.Should().Be((byte)8);
        rec.TxTailTenMsUnits.Should().BeNull();
    }

    [Fact]
    public void Observe_Is_Forwarded_To_Every_Child()
    {
        var txDelay = new TxDelayHillClimbEstimator(initialTxDelay: 50)
        {
            LossPenaltyUnits = 10,
        };
        var csma = new CsmaContentionEstimator(initialPersistence: 100, initialSlotTime: 10)
        {
            PersistStepDown = 16,
            SlotTimeStepUp = 2,
        };
        var composite = new CompositeAdaptiveEstimator(txDelay, csma);

        composite.Observe(new FrameOutcomeSample(
            Peer: "PEER",
            Outcome: FrameOutcome.AckModeTimedOut,
            PayloadBytes: 100,
            ParametersUsed: KissParameters.SpecDefaults,
            AckElapsed: null,
            ObservedAtUtc: DateTimeOffset.UtcNow));

        txDelay.CurrentTxDelayFor("PEER").Should().Be((byte)60, "TXDELAY hill-climb treats AckModeTimedOut as loss");
        csma.CurrentPersistenceFor("PEER").Should().Be((byte)84);
        csma.CurrentSlotTimeFor("PEER").Should().Be((byte)12);
    }

    [Fact]
    public void Empty_Children_Rejected()
    {
        ((Action)(() => new CompositeAdaptiveEstimator()))
            .Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Later_Child_Wins_When_Two_Estimators_Touch_The_Same_Field()
    {
        // Edge case: both children recommend TXDELAY. By Override semantics
        // the later non-null wins, so the composite gives back the second's
        // value.
        var first = new TxDelayHillClimbEstimator(initialTxDelay: 30);
        var second = new TxDelayHillClimbEstimator(initialTxDelay: 80);
        var composite = new CompositeAdaptiveEstimator(first, second);

        composite.Recommend("PEER").TxDelayTenMsUnits.Should().Be((byte)80);
    }
}
