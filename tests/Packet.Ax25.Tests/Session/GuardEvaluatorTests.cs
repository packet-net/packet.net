using AwesomeAssertions;
using Packet.Ax25.Sdl;
using Packet.Ax25.Session;

namespace Packet.Ax25.Tests.Session;

public class GuardEvaluatorTests
{
    private static GuardEvaluator MakeEvaluator(params (Ax25Guard Atom, bool Value)[] bindings)
    {
        var dict = bindings.ToDictionary(b => b.Atom, b => (Func<bool>)(() => b.Value));
        return new GuardEvaluator(dict);
    }

    [Fact]
    public void Empty_Or_Null_Guard_Is_Trivially_True()
    {
        var evaluator = MakeEvaluator();
        evaluator.Evaluate((IReadOnlyList<GuardTerm>?)null).Should().BeTrue();
        evaluator.Evaluate(Array.Empty<GuardTerm>()).Should().BeTrue();
    }

    [Theory]
    [InlineData(false, true)]   // atom true, not negated → holds
    [InlineData(true, false)]   // atom true, negated → fails
    public void Single_Term_Honours_Negate(bool negate, bool expected)
    {
        var evaluator = MakeEvaluator((Ax25Guard.OwnReceiverBusy, true));
        var guard = new GuardTerm[] { new(Ax25Guard.OwnReceiverBusy, negate) };
        evaluator.Evaluate(guard).Should().Be(expected);
    }

    [Theory]
    // term-A value, term-B value, expected (both un-negated → conjunction)
    [InlineData(true, true, true)]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    public void Conjunction_Ands_All_Terms(bool a, bool b, bool expected)
    {
        var evaluator = MakeEvaluator((Ax25Guard.OwnReceiverBusy, a), (Ax25Guard.PeerReceiverBusy, b));
        var guard = new GuardTerm[]
        {
            new(Ax25Guard.OwnReceiverBusy, false),
            new(Ax25Guard.PeerReceiverBusy, false),
        };
        evaluator.Evaluate(guard).Should().Be(expected);
    }

    [Fact]
    public void Conjunction_Mixes_Negated_And_Unnegated_Terms()
    {
        // own_receiver_busy=true AND not T1_running (T1_running=false) → holds.
        var evaluator = MakeEvaluator((Ax25Guard.OwnReceiverBusy, true), (Ax25Guard.T1Running, false));
        var guard = new GuardTerm[]
        {
            new(Ax25Guard.OwnReceiverBusy, false),
            new(Ax25Guard.T1Running, true),
        };
        evaluator.Evaluate(guard).Should().BeTrue();
    }

    [Fact]
    public void Single_GuardTerm_Overload_Evaluates_The_Term()
    {
        // The LoopRange.Predicate shape: one optionally-negated atom.
        var evaluator = MakeEvaluator((Ax25Guard.VsEqX, false));
        evaluator.Evaluate(new GuardTerm(Ax25Guard.VsEqX, false)).Should().BeFalse();
        evaluator.Evaluate(new GuardTerm(Ax25Guard.VsEqX, true)).Should().BeTrue();
    }

    [Fact]
    public void Unbound_Atom_Throws()
    {
        // A binding map missing an atom (only possible with a hand-built partial
        // map; CreateDefault is exhaustive) surfaces as a GuardEvaluationException.
        var evaluator = MakeEvaluator((Ax25Guard.OwnReceiverBusy, true));
        var guard = new GuardTerm[] { new(Ax25Guard.PeerReceiverBusy, false) };
        var act = () => evaluator.Evaluate(guard);
        act.Should().Throw<GuardEvaluationException>()
           .WithMessage("*PeerReceiverBusy*");
    }

    [Fact]
    public void Bindings_Are_Re_Evaluated_Each_Call()
    {
        // Closures that look at mutable state must reflect changes between
        // evaluations — guards are checked at dispatch time, not at
        // binding-construction time.
        bool busy = false;
        var bindings = new Dictionary<Ax25Guard, Func<bool>>
        {
            [Ax25Guard.OwnReceiverBusy] = () => busy,
        };
        var evaluator = new GuardEvaluator(bindings);
        var guard = new GuardTerm[] { new(Ax25Guard.OwnReceiverBusy, false) };

        evaluator.Evaluate(guard).Should().BeFalse();
        busy = true;
        evaluator.Evaluate(guard).Should().BeTrue();
    }

    [Fact]
    public void Real_World_Guard_From_Connected_Transcription()
    {
        // The exact figc4.4a flow-control guard `own_receiver_busy and not
        // T1_running` as the typed conjunction the codegen emits.
        var bindings = new Dictionary<Ax25Guard, Func<bool>>
        {
            [Ax25Guard.OwnReceiverBusy] = () => true,
            [Ax25Guard.T1Running]       = () => false,
        };
        var evaluator = new GuardEvaluator(bindings);

        evaluator.Evaluate(new GuardTerm[] { new(Ax25Guard.OwnReceiverBusy, false) }).Should().BeTrue();
        evaluator.Evaluate(new GuardTerm[] { new(Ax25Guard.OwnReceiverBusy, true) }).Should().BeFalse();
        evaluator.Evaluate(new GuardTerm[]
        {
            new(Ax25Guard.OwnReceiverBusy, false),
            new(Ax25Guard.T1Running, true),
        }).Should().BeTrue();
        evaluator.Evaluate(new GuardTerm[]
        {
            new(Ax25Guard.OwnReceiverBusy, false),
            new(Ax25Guard.T1Running, false),
        }).Should().BeFalse();
    }
}
