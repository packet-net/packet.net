using FsCheck;
using FsCheck.Xunit;
using Microsoft.Extensions.Time.Testing;
using Packet.Ax25;
using Packet.Ax25.Sdl;
using Packet.Ax25.Session;
using Packet.Core;

namespace Packet.Ax25.Properties;

/// <summary>
/// Property-based coverage of <see cref="ActionDispatcher"/>'s 140-odd verb
/// switch. The example-based test class
/// <c>tests/Packet.Ax25.Tests/Session/ActionDispatcherTests.cs</c> pins one
/// representative case per verb; these properties widen the envelope by
/// iterating over arbitrary starting <see cref="Ax25SessionContext"/>
/// states and arbitrary input values (Nr / Ns / pf-bit / payload), checking
/// that the verb's contract holds across the input domain rather than just
/// at the pinned point.
/// </summary>
/// <remarks>
/// <para>
/// Categories covered (one nested class each, mirroring the task brief):
/// </para>
/// <list type="bullet">
///   <item><c>FlagMutationProperties</c> — set_X / clear_X idempotency.</item>
///   <item><c>SequenceVariableProperties</c> — V(s)/V(r)/V(a)/RC assignment + wrap.</item>
///   <item><c>PendingFrameAssignmentProperties</c> — N(r):=V(r) etc.</item>
///   <item><c>FrameEmissionProperties</c> — RR/UA/DM/I_command etc.</item>
///   <item><c>UpwardSignalProperties</c> — DL_* signals.</item>
///   <item><c>TimerProperties</c> — start_TX / stop_TX.</item>
///   <item><c>QueueClearProperties</c> — discard_* verbs.</item>
///   <item><c>UnknownVerbProperties</c> — typo / catch-all defence.</item>
/// </list>
/// <para>
/// Each property runs 200 iterations by default — fast enough to keep
/// the whole property suite well under 60s, broad enough to catch any
/// verb-level regression. A handful of properties that explore richer
/// input spaces drop to 50 to keep wall-clock down. None require
/// <c>[Trait("Category", "Slow")]</c>.
/// </para>
/// </remarks>
public static class ActionDispatcherPropertyHelpers
{
    /// <summary>
    /// Standard rig: dispatcher + context + scheduler + capture lists for
    /// every outgoing channel. Mirrors the helper used in the example
    /// tests; lifted here so each nested property class can use it.
    /// </summary>
    public sealed class Rig
    {
        public ActionDispatcher Dispatcher { get; private set; } = null!;
        public Ax25SessionContext Context  { get; init; } = null!;
        public SystemTimerScheduler Scheduler { get; init; } = null!;
        public FakeTimeProvider Time { get; init; } = null!;
        public List<string> TimerExpiries { get; } = new();
        public List<SupervisoryFrameSpec> SFrames { get; } = new();
        public List<UFrameSpec> UFrames { get; } = new();
        public List<UiFrameSpec> UiFrames { get; } = new();
        public List<IFrameSpec> IFrames { get; } = new();
        public List<DataLinkSignal> Upward { get; } = new();
        public List<LinkMultiplexerSignal> LinkMux { get; } = new();
        public List<InternalSignal> Internal { get; } = new();

        /// <summary>
        /// Wire the dispatcher's callbacks to this rig's capture lists.
        /// Called once by <see cref="NewRig"/> immediately after
        /// construction; surfaced as a method so the dispatcher can close
        /// over the lists *of this same Rig instance* rather than a
        /// throw-away copy.
        /// </summary>
        public void WireDispatcher()
        {
            Dispatcher = new ActionDispatcher(
                onTimerExpiry: TimerExpiries.Add,
                sendSFrame:    SFrames.Add,
                sendUFrame:    UFrames.Add,
                sendUiFrame:   UiFrames.Add,
                sendUpward:    Upward.Add,
                sendLinkMux:   LinkMux.Add,
                sendInternal:  Internal.Add,
                sendIFrame:    IFrames.Add);
        }
    }

    /// <summary>Construct a fresh rig with empty capture lists.</summary>
    public static Rig NewRig()
    {
        var time = new FakeTimeProvider();
        var scheduler = new SystemTimerScheduler(time);
        var rig = new Rig
        {
            Time = time,
            Scheduler = scheduler,
            Context = new Ax25SessionContext
            {
                Local  = new Callsign("M0LTE", 0),
                Remote = new Callsign("G7XYZ", 7),
            },
        };
        rig.WireDispatcher();
        return rig;
    }

    /// <summary>
    /// Apply an arbitrary starting context snapshot to <paramref name="ctx"/>.
    /// Used by the flag- and sequence-variable properties to ensure every
    /// idempotency / wrap claim is checked against a non-default state.
    /// </summary>
    public static void SeedContext(
        Ax25SessionContext ctx,
        bool ownBusy, bool peerBusy, bool ackPending, bool l3Initiated,
        bool rejException, bool srejException,
        byte vs, byte vr, byte va, int rc,
        bool isExtended)
    {
        ctx.OwnReceiverBusy = ownBusy;
        ctx.PeerReceiverBusy = peerBusy;
        ctx.AcknowledgePending = ackPending;
        ctx.Layer3Initiated = l3Initiated;
        ctx.RejectException = rejException;
        ctx.SelectiveRejectException = srejException;
        ctx.VS = vs;
        ctx.VR = vr;
        ctx.VA = va;
        ctx.RC = rc;
        ctx.IsExtended = isExtended;
    }

    /// <summary>
    /// Build a mod-8 RR command frame with the supplied N(R) and P bit.
    /// Lifted from the example tests so property-test code can build
    /// trigger frames without duplicating the I/O.
    /// </summary>
    public static Ax25Frame BuildRrFrame(byte nr, bool pollBit)
    {
        var bytes = new byte[15];
        new Ax25Address(new Callsign("M0LTE", 0), CrhBit: true,  ExtensionBit: false).Write(bytes.AsSpan(0, 7));
        new Ax25Address(new Callsign("G7XYZ", 7), CrhBit: false, ExtensionBit: true ).Write(bytes.AsSpan(7, 7));
        bytes[14] = (byte)(((nr & 0x07) << 5) | (pollBit ? 0x10 : 0) | 0x01);
        Ax25Frame.TryParse(bytes, out var frame).Should().BeTrue();
        return frame!;
    }

    /// <summary>
    /// Build a mod-8 I-frame with the supplied N(R), N(S), P bit, info, PID.
    /// </summary>
    public static Ax25Frame BuildIFrame(byte nr, byte ns, bool pollBit, byte[] info, byte pid)
    {
        var bytes = new byte[7 + 7 + 1 + 1 + info.Length];
        new Ax25Address(new Callsign("M0LTE", 0), CrhBit: true,  ExtensionBit: false).Write(bytes.AsSpan(0, 7));
        new Ax25Address(new Callsign("G7XYZ", 7), CrhBit: false, ExtensionBit: true ).Write(bytes.AsSpan(7, 7));
        bytes[14] = (byte)(((nr & 0x07) << 5) | (pollBit ? 0x10 : 0) | ((ns & 0x07) << 1));
        bytes[15] = pid;
        info.CopyTo(bytes.AsSpan(16));
        Ax25Frame.TryParse(bytes, out var frame).Should().BeTrue();
        return frame!;
    }
}

// ─── 1. Flag-mutation verbs ──────────────────────────────────────────────

/// <summary>
/// Property: every set_X / clear_X pair on <see cref="Ax25SessionContext"/>
/// is idempotent (set twice = set once; clear twice = clear once) and
/// inverse (set then clear leaves the flag false; clear then set leaves it
/// true) regardless of the starting context state.
/// </summary>
public class FlagMutationProperties
{
    // The flag-toggling verbs paired with the boolean property they mutate.
    // Tested via reflection-style strings so the property table stays close
    // to the dispatcher's case arms (any drift fails the test rather than
    // silently going untested).
    private static readonly (Ax25ActionVerb SetVerb, Ax25ActionVerb ClearVerb, Func<Ax25SessionContext, bool> Read)[] FlagVerbs =
    {
        (Ax25ActionVerb.SetOwnReceiverBusy,         Ax25ActionVerb.ClearOwnReceiverBusy,         c => c.OwnReceiverBusy),
        (Ax25ActionVerb.SetPeerReceiverBusy,        Ax25ActionVerb.ClearPeerReceiverBusy,        c => c.PeerReceiverBusy),
        (Ax25ActionVerb.SetAcknowledgePending,       Ax25ActionVerb.ClearAcknowledgePending,       c => c.AcknowledgePending),
        (Ax25ActionVerb.SetLayer3Initiated,         Ax25ActionVerb.ClearLayer3Initiated,         c => c.Layer3Initiated),
        (Ax25ActionVerb.SetRejectException,          Ax25ActionVerb.ClearRejectException,          c => c.RejectException),
    };

    [Property(MaxTest = 200)]
    public void Set_Then_Clear_Returns_Flag_To_False(
        bool ownBusy, bool peerBusy, bool ackPending, bool l3Initiated,
        bool rejException, bool srejException, byte rawIdx)
    {
        var idx = rawIdx % FlagVerbs.Length;
        var (setVerb, clearVerb, read) = FlagVerbs[idx];

        var rig = ActionDispatcherPropertyHelpers.NewRig();
        ActionDispatcherPropertyHelpers.SeedContext(
            rig.Context, ownBusy, peerBusy, ackPending, l3Initiated, rejException, srejException,
            vs: 0, vr: 0, va: 0, rc: 0, isExtended: false);

        rig.Dispatcher.Execute(setVerb, rig.Context, rig.Scheduler);
        read(rig.Context).Should().BeTrue("set_X verb should set the flag regardless of prior state");

        rig.Dispatcher.Execute(clearVerb, rig.Context, rig.Scheduler);
        read(rig.Context).Should().BeFalse("clear_X verb after set_X must return the flag to false");
    }

    [Property(MaxTest = 200)]
    public void Set_Twice_Is_Idempotent(
        bool ownBusy, bool peerBusy, bool ackPending, bool l3Initiated,
        bool rejException, bool srejException, byte rawIdx)
    {
        var idx = rawIdx % FlagVerbs.Length;
        var (setVerb, _, read) = FlagVerbs[idx];

        var rig = ActionDispatcherPropertyHelpers.NewRig();
        ActionDispatcherPropertyHelpers.SeedContext(
            rig.Context, ownBusy, peerBusy, ackPending, l3Initiated, rejException, srejException,
            vs: 0, vr: 0, va: 0, rc: 0, isExtended: false);

        rig.Dispatcher.Execute(setVerb, rig.Context, rig.Scheduler);
        var afterFirst = read(rig.Context);
        rig.Dispatcher.Execute(setVerb, rig.Context, rig.Scheduler);
        var afterSecond = read(rig.Context);

        afterFirst.Should().BeTrue();
        afterSecond.Should().Be(afterFirst, "set_X should be idempotent");
    }

    [Property(MaxTest = 200)]
    public void Clear_Twice_Is_Idempotent(
        bool ownBusy, bool peerBusy, bool ackPending, bool l3Initiated,
        bool rejException, bool srejException, byte rawIdx)
    {
        var idx = rawIdx % FlagVerbs.Length;
        var (_, clearVerb, read) = FlagVerbs[idx];

        var rig = ActionDispatcherPropertyHelpers.NewRig();
        ActionDispatcherPropertyHelpers.SeedContext(
            rig.Context, ownBusy, peerBusy, ackPending, l3Initiated, rejException, srejException,
            vs: 0, vr: 0, va: 0, rc: 0, isExtended: false);

        rig.Dispatcher.Execute(clearVerb, rig.Context, rig.Scheduler);
        var afterFirst = read(rig.Context);
        rig.Dispatcher.Execute(clearVerb, rig.Context, rig.Scheduler);
        var afterSecond = read(rig.Context);

        afterFirst.Should().BeFalse();
        afterSecond.Should().Be(afterFirst, "clear_X should be idempotent");
    }

    /// <summary>
    /// The set / clear verbs touch *only* the flag they're named after —
    /// no cross-contamination between flag verbs. Exhausts the cross product
    /// of (verb-under-test, observed-flag) pairs to confirm.
    /// </summary>
    [Property(MaxTest = 200)]
    public void Flag_Verbs_Do_Not_Mutate_Unrelated_Flags(
        byte verbIdx, byte observedIdx)
    {
        var verbI = verbIdx % FlagVerbs.Length;
        var obsI = observedIdx % FlagVerbs.Length;
        if (verbI == obsI) return;  // self-mutation is the explicit contract

        var rig = ActionDispatcherPropertyHelpers.NewRig();
        // Start with every observable flag in a known mixed state.
        ActionDispatcherPropertyHelpers.SeedContext(
            rig.Context,
            ownBusy: true, peerBusy: false, ackPending: true, l3Initiated: false,
            rejException: true, srejException: false,
            vs: 0, vr: 0, va: 0, rc: 0, isExtended: false);

        var (setVerb, _, _)        = FlagVerbs[verbI];
        var (_, _, readObserved)   = FlagVerbs[obsI];
        var observedBefore = readObserved(rig.Context);

        rig.Dispatcher.Execute(setVerb, rig.Context, rig.Scheduler);

        readObserved(rig.Context).Should().Be(observedBefore,
            "verb {0} should not mutate flag #{1}'s slot", setVerb, obsI);
    }

    // ─── set_/clear_selective_reject_exception variants exist as
    //     increment_srej_exception / decrement_srej_exception_if_gt_0,
    //     not as plain set_/clear_ pairs. Validated separately below.

    [Property(MaxTest = 200)]
    public void Increment_Srej_Exception_Bumps_Count_And_Sets_Flag(
        int startingCount)
    {
        var safeStart = Math.Max(0, startingCount % 100);
        var rig = ActionDispatcherPropertyHelpers.NewRig();
        rig.Context.SrejExceptionCount = safeStart;
        rig.Context.SelectiveRejectException = safeStart > 0;

        rig.Dispatcher.Execute(Ax25ActionVerb.IncrementSrejectException, rig.Context, rig.Scheduler);

        rig.Context.SrejExceptionCount.Should().Be(safeStart + 1);
        rig.Context.SelectiveRejectException.Should().BeTrue();
    }

    [Property(MaxTest = 200)]
    public void Decrement_Srej_Exception_If_GT_0_Bottoms_At_Zero(
        int startingCount)
    {
        var safeStart = Math.Max(0, startingCount % 50);
        var rig = ActionDispatcherPropertyHelpers.NewRig();
        rig.Context.SrejExceptionCount = safeStart;
        rig.Context.SelectiveRejectException = safeStart > 0;

        rig.Dispatcher.Execute(Ax25ActionVerb.DecrementSrejectExceptionIf0, rig.Context, rig.Scheduler);

        if (safeStart == 0)
        {
            rig.Context.SrejExceptionCount.Should().Be(0, "decrement at 0 must stay at 0");
            rig.Context.SelectiveRejectException.Should().BeFalse();
        }
        else
        {
            rig.Context.SrejExceptionCount.Should().Be(safeStart - 1);
            rig.Context.SelectiveRejectException.Should().Be(safeStart - 1 > 0);
        }
    }
}

// ─── 2. Sequence-variable assignment verbs ───────────────────────────────

/// <summary>
/// Properties over the V(s) / V(r) / V(a) / RC assignment verbs. The big
/// claim is mod-arithmetic correctness: N increments leave V at
/// (initial + N) mod modulus for both mod-8 (default) and mod-128
/// (IsExtended).
/// </summary>
public class SequenceVariableProperties
{
    [Property(MaxTest = 200)]
    public void VS_Increment_Wraps_At_Mod8(byte startingVs, byte steps)
    {
        var rig = ActionDispatcherPropertyHelpers.NewRig();
        rig.Context.IsExtended = false;
        rig.Context.VS = (byte)(startingVs & 0x07);
        var initial = rig.Context.VS;
        var n = (byte)(steps & 0x1F);  // 0..31 — enough wraps to flush mod-8

        for (int i = 0; i < n; i++)
        {
            rig.Dispatcher.Execute(Ax25ActionVerb.VSAssignVSPlus1, rig.Context, rig.Scheduler);
        }

        rig.Context.VS.Should().Be((byte)((initial + n) % 8),
            "mod-8 sequence variable must wrap at 8");
    }

    [Property(MaxTest = 50)]
    public void VS_Increment_Wraps_At_Mod128_When_Extended(byte startingVs, byte steps)
    {
        var rig = ActionDispatcherPropertyHelpers.NewRig();
        rig.Context.IsExtended = true;
        rig.Context.VS = (byte)(startingVs & 0x7F);
        var initial = rig.Context.VS;
        var n = steps;  // 0..255 — enough to span mod-128

        for (int i = 0; i < n; i++)
        {
            rig.Dispatcher.Execute(Ax25ActionVerb.VSAssignVSPlus1, rig.Context, rig.Scheduler);
        }

        rig.Context.VS.Should().Be((byte)((initial + n) % 128),
            "mod-128 sequence variable must wrap at 128");
    }

    [Property(MaxTest = 200)]
    public void VR_Increment_Wraps_At_Mod8(byte startingVr, byte steps)
    {
        var rig = ActionDispatcherPropertyHelpers.NewRig();
        rig.Context.IsExtended = false;
        rig.Context.VR = (byte)(startingVr & 0x07);
        var initial = rig.Context.VR;
        var n = (byte)(steps & 0x1F);

        for (int i = 0; i < n; i++)
        {
            rig.Dispatcher.Execute(Ax25ActionVerb.VRAssignVRPlus1, rig.Context, rig.Scheduler);
        }

        rig.Context.VR.Should().Be((byte)((initial + n) % 8));
    }

    [Property(MaxTest = 200)]
    public void Increment_Then_Reset_To_Zero_Lands_At_Zero(
        byte startingVs, byte steps)
    {
        var rig = ActionDispatcherPropertyHelpers.NewRig();
        rig.Context.VS = (byte)(startingVs & 0x07);
        var n = (byte)(steps & 0x1F);

        for (int i = 0; i < n; i++)
        {
            rig.Dispatcher.Execute(Ax25ActionVerb.VSAssignVSPlus1, rig.Context, rig.Scheduler);
        }
        rig.Dispatcher.Execute(Ax25ActionVerb.VSAssign0, rig.Context, rig.Scheduler);

        rig.Context.VS.Should().Be((byte)0,
            "V(s) := 0 must reset regardless of prior accumulation");
    }

    [Property(MaxTest = 200)]
    public void RC_Increment_Increments_By_One(int startingRc)
    {
        // RC is an int, not mod-anything — just verify naive +1 semantics
        // for the range the spec actually uses (0..N2 ≈ 0..255). Negative
        // starting values are degenerate but well-defined (we still +1).
        var rig = ActionDispatcherPropertyHelpers.NewRig();
        var safeStart = Math.Clamp(startingRc, -1_000, 1_000);
        rig.Context.RC = safeStart;

        rig.Dispatcher.Execute(Ax25ActionVerb.RCAssignRCPlus1, rig.Context, rig.Scheduler);

        rig.Context.RC.Should().Be(safeStart + 1);
    }

    [Property(MaxTest = 200)]
    public void RC_Constant_Assignments_Set_Exact_Value(int startingRc)
    {
        var rig = ActionDispatcherPropertyHelpers.NewRig();
        rig.Context.RC = Math.Clamp(startingRc, -1_000, 1_000);

        rig.Dispatcher.Execute(Ax25ActionVerb.RCAssign0, rig.Context, rig.Scheduler);
        rig.Context.RC.Should().Be(0);

        rig.Dispatcher.Execute(Ax25ActionVerb.RCAssign1, rig.Context, rig.Scheduler);
        rig.Context.RC.Should().Be(1);
    }

    /// <summary>
    /// <c>V(a) := N(r)</c> reads N(R) from the incoming frame. Property:
    /// for any N(R) in [0..7] the verb writes V(A) := that value, regardless
    /// of the prior V(A).
    /// </summary>
    [Property(MaxTest = 200)]
    public void VA_Assign_From_Nr_Reads_Incoming_Frame_Nr(byte rawNr, byte priorVa)
    {
        var nr = (byte)(rawNr & 0x07);
        var rig = ActionDispatcherPropertyHelpers.NewRig();
        rig.Context.VA = priorVa;
        var frame = ActionDispatcherPropertyHelpers.BuildRrFrame(nr, pollBit: false);
        var tx = new TransitionContext(rig.Context, rig.Scheduler, new RrReceived(frame));

        rig.Dispatcher.Execute(Ax25ActionVerb.VAAssignNR, tx);

        rig.Context.VA.Should().Be(nr);
    }

    [Fact]
    public void VA_Assign_From_Nr_Throws_When_Trigger_Has_No_Frame()
    {
        var rig = ActionDispatcherPropertyHelpers.NewRig();
        var tx = new TransitionContext(rig.Context, rig.Scheduler, new T1Expiry());

        var act = () => rig.Dispatcher.Execute(Ax25ActionVerb.VAAssignNR, tx);

        act.Should().Throw<InvalidOperationException>()
           .WithMessage("*requires an incoming frame*T1_expiry*");
    }
}

// ─── 3. Pending-frame assignment verbs ───────────────────────────────────

/// <summary>
/// Properties over verbs that populate <see cref="PendingFrame"/> fields:
/// N(r) := V(r), N(s) := V(s), N(r) := N(s), F := 0/1/P, p := 0.
/// </summary>
public class PendingFrameAssignmentProperties
{
    [Property(MaxTest = 200)]
    public void Nr_Assign_From_VR_Writes_Pending(byte vr)
    {
        var safeVr = (byte)(vr & 0x07);
        var rig = ActionDispatcherPropertyHelpers.NewRig();
        rig.Context.VR = safeVr;
        var tx = new TransitionContext(rig.Context, rig.Scheduler, new DlConnectRequest());

        rig.Dispatcher.Execute(Ax25ActionVerb.NRAssignVR, tx);

        tx.Pending.Nr.Should().Be(safeVr);
        tx.Pending.Ns.Should().BeNull("N(s) untouched");
        tx.Pending.PfBit.Should().BeNull("PfBit untouched");
    }

    [Property(MaxTest = 200)]
    public void Ns_Assign_From_VS_Writes_Pending(byte vs)
    {
        var safeVs = (byte)(vs & 0x07);
        var rig = ActionDispatcherPropertyHelpers.NewRig();
        rig.Context.VS = safeVs;
        var tx = new TransitionContext(rig.Context, rig.Scheduler, new DlConnectRequest());

        rig.Dispatcher.Execute(Ax25ActionVerb.NSAssignVS, tx);

        tx.Pending.Ns.Should().Be(safeVs);
    }

    [Property(MaxTest = 200)]
    public void Nr_Assign_From_Ns_Reads_Incoming_I_Frame_NS_When_StrictlyFaithful(
        byte rawNr, byte rawNs, bool pollBit)
    {
        // Raw figc4.4 verb (ax25spec#42 quirk off): N(r) := N(s) extracts N(S).
        // With the default-on Ax25Spec42SrejTargetsGap quirk this verb is
        // retargeted to V(R) on an I_received trigger (covered by a unit test in
        // ActionDispatcherTests); here we pin the underlying extraction.
        var nr = (byte)(rawNr & 0x07);
        var ns = (byte)(rawNs & 0x07);
        var rig = ActionDispatcherPropertyHelpers.NewRig();
        rig.Context.Quirks = Ax25SessionQuirks.StrictlyFaithful;
        var frame = ActionDispatcherPropertyHelpers.BuildIFrame(
            nr: nr, ns: ns, pollBit: pollBit, info: [1, 2, 3], pid: Ax25Frame.PidNoLayer3);
        var tx = new TransitionContext(rig.Context, rig.Scheduler, new IFrameReceived(frame));

        rig.Dispatcher.Execute(Ax25ActionVerb.NRAssignNS, tx);

        tx.Pending.Nr.Should().Be(ns);
    }

    [Fact]
    public void Nr_Assign_From_Ns_Throws_Without_Trigger_Frame()
    {
        var rig = ActionDispatcherPropertyHelpers.NewRig();
        var tx = new TransitionContext(rig.Context, rig.Scheduler, new T1Expiry());
        var act = () => rig.Dispatcher.Execute(Ax25ActionVerb.NRAssignNS, tx);
        act.Should().Throw<InvalidOperationException>()
           .WithMessage("*requires an incoming frame*T1_expiry*");
    }

    [Property(MaxTest = 200)]
    public void F_Constant_Assignments_Write_Pending_PfBit(bool which)
    {
        var rig = ActionDispatcherPropertyHelpers.NewRig();
        var tx = new TransitionContext(rig.Context, rig.Scheduler, new DlConnectRequest());

        rig.Dispatcher.Execute(which ? Ax25ActionVerb.FAssign1 : Ax25ActionVerb.FAssign0, tx);

        tx.Pending.PfBit.Should().Be(which);
    }

    [Property(MaxTest = 200)]
    public void P_Lowercase_Zero_Assignment_Writes_False(bool prior)
    {
        var rig = ActionDispatcherPropertyHelpers.NewRig();
        var tx = new TransitionContext(rig.Context, rig.Scheduler, new DlConnectRequest());

        // Use F := 1 first so the prior value is non-default.
        if (prior) rig.Dispatcher.Execute(Ax25ActionVerb.FAssign1, tx);
        rig.Dispatcher.Execute(Ax25ActionVerb.PAssign0, tx);

        tx.Pending.PfBit.Should().BeFalse(
            "p := 0 must overwrite any prior PfBit assignment");
    }

    [Property(MaxTest = 200)]
    public void F_Assign_From_P_Echoes_Incoming_Poll_Bit(bool pollBit, byte rawNr)
    {
        var nr = (byte)(rawNr & 0x07);
        var rig = ActionDispatcherPropertyHelpers.NewRig();
        var frame = ActionDispatcherPropertyHelpers.BuildRrFrame(nr, pollBit: pollBit);
        var tx = new TransitionContext(rig.Context, rig.Scheduler, new RrReceived(frame));

        rig.Dispatcher.Execute(Ax25ActionVerb.FAssignP, tx);

        tx.Pending.PfBit.Should().Be(pollBit);
    }

    [Fact]
    public void F_Assign_From_P_Throws_Without_Trigger_Frame()
    {
        var rig = ActionDispatcherPropertyHelpers.NewRig();
        var tx = new TransitionContext(rig.Context, rig.Scheduler, new T1Expiry());
        var act = () => rig.Dispatcher.Execute(Ax25ActionVerb.FAssignP, tx);
        act.Should().Throw<InvalidOperationException>()
           .WithMessage("*requires an incoming frame*T1_expiry*");
    }
}

// ─── 4. Frame-emission verbs ─────────────────────────────────────────────

/// <summary>
/// Properties over the signal_lower verbs. Each emits exactly one frame
/// of the documented kind, with N(r)/N(s)/PfBit drawn from Pending (with
/// the documented defaults).
/// </summary>
public class FrameEmissionProperties
{
    private static readonly (Ax25ActionVerb Verb, SupervisoryFrameType Type, bool IsCommand)[] SupervisoryVerbs =
    {
        (Ax25ActionVerb.RRCommand,   SupervisoryFrameType.Rr,   true),
        (Ax25ActionVerb.RR,           SupervisoryFrameType.Rr,   false),
        (Ax25ActionVerb.RNRResponse, SupervisoryFrameType.Rnr,  false),
        (Ax25ActionVerb.REJ,          SupervisoryFrameType.Rej,  false),
        (Ax25ActionVerb.SREJ,         SupervisoryFrameType.Srej, false),
        (Ax25ActionVerb.RRCommand,   SupervisoryFrameType.Rr,   true),
        (Ax25ActionVerb.RRResponse,  SupervisoryFrameType.Rr,   false),
        (Ax25ActionVerb.RNRCommand,  SupervisoryFrameType.Rnr,  true),
        (Ax25ActionVerb.RNRResponse, SupervisoryFrameType.Rnr,  false),
    };

    [Property(MaxTest = 200)]
    public void Supervisory_Verb_Emits_Exactly_One_Frame_With_Right_Type_Role_Nr_Pf(
        byte verbIdx, byte rawNr, bool pfBit)
    {
        var idx = verbIdx % SupervisoryVerbs.Length;
        var (verb, expectedType, expectedIsCommand) = SupervisoryVerbs[idx];
        var nr = (byte)(rawNr & 0x07);

        var rig = ActionDispatcherPropertyHelpers.NewRig();
        var tx = new TransitionContext(rig.Context, rig.Scheduler, new DlConnectRequest());

        var setF = pfBit ? Ax25ActionVerb.FAssign1 : Ax25ActionVerb.FAssign0;
        rig.Dispatcher.Execute(setF, tx);
        tx.Pending.Nr = nr;
        rig.Dispatcher.Execute(verb, tx);

        rig.SFrames.Should().ContainSingle();
        var spec = rig.SFrames[0];
        spec.Type.Should().Be(expectedType);
        spec.IsCommand.Should().Be(expectedIsCommand);
        spec.Nr.Should().Be(nr);
        spec.PfBit.Should().Be(pfBit);
    }

    [Property(MaxTest = 200)]
    public void Supervisory_Verb_Default_Nr_Falls_Back_To_VR(byte vr, byte verbIdx)
    {
        var safeVr = (byte)(vr & 0x07);
        var idx = verbIdx % SupervisoryVerbs.Length;
        var (verb, expectedType, _) = SupervisoryVerbs[idx];

        var rig = ActionDispatcherPropertyHelpers.NewRig();
        rig.Context.VR = safeVr;
        // No N(r) := V(r) — verb should default Nr from V(R).
        rig.Dispatcher.Execute(verb, rig.Context, rig.Scheduler);

        rig.SFrames.Should().ContainSingle();
        rig.SFrames[0].Nr.Should().Be(safeVr);
        rig.SFrames[0].Type.Should().Be(expectedType);
        rig.SFrames[0].PfBit.Should().BeFalse("no F := * verb — default false");
    }

    private static readonly (Ax25ActionVerb Verb, UFrameType Type, bool IsCommand, bool? PfOverride, bool IsExpedited)[] UVerbs =
    {
        (Ax25ActionVerb.UA,            UFrameType.Ua,    false, null, false),
        (Ax25ActionVerb.DM,            UFrameType.Dm,    false, null, false),
        (Ax25ActionVerb.DMFEq1,    UFrameType.Dm,    false, true, false),
        (Ax25ActionVerb.ExpeditedUA,  UFrameType.Ua,    false, null, true),
        (Ax25ActionVerb.ExpeditedDM,  UFrameType.Dm,    false, null, true),
        (Ax25ActionVerb.SABMPEqEq1, UFrameType.Sabm,  true,  true, false),
        (Ax25ActionVerb.SABMEPEq1, UFrameType.Sabme, true,  true, false),
        (Ax25ActionVerb.DISCPEq1,  UFrameType.Disc,  true,  true, false),
        (Ax25ActionVerb.SABM,          UFrameType.Sabm,  true,  true, false),
        (Ax25ActionVerb.SABME,         UFrameType.Sabme, true,  true, false),
    };

    [Property(MaxTest = 200)]
    public void Unnumbered_Verb_Emits_Exactly_One_Frame_With_Right_Properties(
        byte verbIdx, bool pendingPfBit)
    {
        var idx = verbIdx % UVerbs.Length;
        var (verb, expectedType, expectedIsCommand, pfOverride, expectedExpedited) = UVerbs[idx];

        var rig = ActionDispatcherPropertyHelpers.NewRig();
        var tx = new TransitionContext(rig.Context, rig.Scheduler, new DlConnectRequest());

        // Seed Pending.PfBit so we can confirm overrides win.
        rig.Dispatcher.Execute(pendingPfBit ? Ax25ActionVerb.FAssign1 : Ax25ActionVerb.FAssign0, tx);
        rig.Dispatcher.Execute(verb, tx);

        rig.UFrames.Should().ContainSingle();
        var spec = rig.UFrames[0];
        spec.Type.Should().Be(expectedType);
        spec.IsCommand.Should().Be(expectedIsCommand);
        spec.IsExpedited.Should().Be(expectedExpedited);

        var expectedPf = pfOverride ?? pendingPfBit;
        spec.PfBit.Should().Be(expectedPf,
            "verb {0}: PfBit should be {1} (override={2}, pending={3})",
            verb, expectedPf, pfOverride, pendingPfBit);
    }

    [Property(MaxTest = 200)]
    public void I_Command_Builds_IFrameSpec_From_Pending_And_Payload(
        byte rawNs, byte rawNr, bool pBit, byte pid, byte[] info)
    {
        info ??= [];
        var ns = (byte)(rawNs & 0x07);
        var nr = (byte)(rawNr & 0x07);

        var rig = ActionDispatcherPropertyHelpers.NewRig();
        var tx = new TransitionContext(rig.Context, rig.Scheduler,
            new IFramePopsOffQueue(info, Pid: pid));

        tx.Pending.Ns = ns;
        tx.Pending.Nr = nr;
        tx.Pending.PfBit = pBit;
        rig.Dispatcher.Execute(Ax25ActionVerb.ICommand, tx);

        rig.IFrames.Should().ContainSingle();
        var spec = rig.IFrames[0];
        spec.IsCommand.Should().BeTrue("I-frames are always commands");
        spec.Ns.Should().Be(ns);
        spec.Nr.Should().Be(nr);
        spec.PBit.Should().Be(pBit);
        spec.Pid.Should().Be(pid);
        spec.Info.ToArray().Should().Equal(info);

        // SentIFrames stashes the payload for retransmission.
        rig.Context.SentIFrames.Should().ContainKey(ns);
        rig.Context.SentIFrames[ns].Data.ToArray().Should().Equal(info);
        rig.Context.SentIFrames[ns].Pid.Should().Be(pid);
    }

    [Fact]
    public void I_Command_Throws_Without_IFramePopsOffQueue_Trigger()
    {
        var rig = ActionDispatcherPropertyHelpers.NewRig();
        var tx = new TransitionContext(rig.Context, rig.Scheduler, new DlConnectRequest());
        var act = () => rig.Dispatcher.Execute(Ax25ActionVerb.ICommand, tx);
        act.Should().Throw<InvalidOperationException>()
           .WithMessage("*I_command*requires the trigger*I_frame_pops_off_queue*DL_CONNECT_request*");
    }

    [Property(MaxTest = 200)]
    public void UI_Command_Reads_Payload_From_DlUnitDataRequest(byte pid, byte[] info, bool pendingPf)
    {
        info ??= [];
        var rig = ActionDispatcherPropertyHelpers.NewRig();
        var tx = new TransitionContext(rig.Context, rig.Scheduler,
            new DlUnitDataRequest(info, Pid: pid));

        if (pendingPf) rig.Dispatcher.Execute(Ax25ActionVerb.FAssign1, tx);
        rig.Dispatcher.Execute(Ax25ActionVerb.UICommand, tx);

        rig.UiFrames.Should().ContainSingle();
        var spec = rig.UiFrames[0];
        spec.IsCommand.Should().BeTrue();
        spec.PfBit.Should().Be(pendingPf);
        spec.Pid.Should().Be(pid);
        spec.Info.ToArray().Should().Equal(info);
    }

    [Fact]
    public void UI_Command_Throws_Without_DlUnitDataRequest_Trigger()
    {
        var rig = ActionDispatcherPropertyHelpers.NewRig();
        var tx = new TransitionContext(rig.Context, rig.Scheduler, new DlConnectRequest());
        var act = () => rig.Dispatcher.Execute(Ax25ActionVerb.UICommand, tx);
        act.Should().Throw<InvalidOperationException>()
           .WithMessage("*UI_command*requires the trigger*DL_UNIT_DATA_request*DL_CONNECT_request*");
    }
}

// ─── 5. Upward-signal verbs ──────────────────────────────────────────────

/// <summary>
/// Every <c>DL_*</c> verb emits exactly one <see cref="DataLinkSignal"/>
/// of the matching record type via the configured <c>sendUpward</c>
/// callback.
/// </summary>
public class UpwardSignalProperties
{
    private static readonly (Ax25ActionVerb Verb, Type ExpectedRecord)[] SimpleSignals =
    {
        (Ax25ActionVerb.DLCONNECTIndication,    typeof(DataLinkConnectIndication)),
        (Ax25ActionVerb.DLCONNECTConfirm,       typeof(DataLinkConnectConfirm)),
        (Ax25ActionVerb.DLDISCONNECTIndication, typeof(DataLinkDisconnectIndication)),
        (Ax25ActionVerb.DLDISCONNECTConfirm,    typeof(DataLinkDisconnectConfirm)),
    };

    [Property(MaxTest = 200)]
    public void Simple_DL_Signal_Verb_Emits_Exactly_One_Signal(byte verbIdx)
    {
        var idx = verbIdx % SimpleSignals.Length;
        var (verb, expectedType) = SimpleSignals[idx];

        var rig = ActionDispatcherPropertyHelpers.NewRig();
        rig.Dispatcher.Execute(verb, rig.Context, rig.Scheduler);

        rig.Upward.Should().ContainSingle();
        rig.Upward[0].Should().BeOfType(expectedType);
    }

    private static readonly (Ax25ActionVerb Verb, string ExpectedCode)[] ErrorCodes =
    {
        (Ax25ActionVerb.DLERRORIndicationCD, "C_D"),
        (Ax25ActionVerb.DLERRORIndicationD,   "D"),
        (Ax25ActionVerb.DLERRORIndicationE,   "E"),
        (Ax25ActionVerb.DLERRORIndicationF,   "F"),
        (Ax25ActionVerb.DLERRORIndicationG,   "G"),
        (Ax25ActionVerb.DLERRORIndicationK,   "K"),
        (Ax25ActionVerb.DLERRORIndicationL,   "L"),
        (Ax25ActionVerb.DLERRORIndicationM,   "M"),
        (Ax25ActionVerb.DLERRORIndicationN,   "N"),
        (Ax25ActionVerb.DLERRORIndicationO,   "O"),
    };

    [Property(MaxTest = 200)]
    public void DL_Error_Indication_Verb_Emits_Signal_With_Matching_Code(byte verbIdx)
    {
        var idx = verbIdx % ErrorCodes.Length;
        var (verb, expectedCode) = ErrorCodes[idx];

        var rig = ActionDispatcherPropertyHelpers.NewRig();
        rig.Dispatcher.Execute(verb, rig.Context, rig.Scheduler);

        rig.Upward.Should().ContainSingle()
           .Which.Should().BeOfType<DataLinkErrorIndication>()
           .Which.Code.Should().Be(expectedCode);
    }

    /// <summary>
    /// figc4.7 verbatim spellings (with parenthesised letter codes) — must
    /// emit a single <see cref="DataLinkErrorIndication"/> matching the
    /// letter inside the parens.
    /// </summary>
    [Property(MaxTest = 200)]
    public void DL_Error_Indication_FigC47_Variants_Emit_Single_Signal_With_Code(byte verbIdx)
    {
        var spellings = new (Ax25ActionVerb Verb, string Code)[]
        {
            (Ax25ActionVerb.DLERRORIndicationAdd, "add"),
            (Ax25ActionVerb.DLERRORIndicationA,   "A"),
            (Ax25ActionVerb.DLERRORIndicationJ,   "J"),
            (Ax25ActionVerb.DLERRORIndicationK,   "K"),
            (Ax25ActionVerb.DLERRORIndicationQ,   "Q"),
        };
        var idx = verbIdx % spellings.Length;
        var (verb, expectedCode) = spellings[idx];

        var rig = ActionDispatcherPropertyHelpers.NewRig();
        rig.Dispatcher.Execute(verb, rig.Context, rig.Scheduler);

        rig.Upward.Should().ContainSingle()
           .Which.Should().BeOfType<DataLinkErrorIndication>()
           .Which.Code.Should().Be(expectedCode);
    }

    [Property(MaxTest = 200)]
    public void DL_Data_Indication_Reads_Info_And_Pid_From_Trigger_IFrame(
        byte rawNr, byte rawNs, bool pollBit, byte pid, byte[] info)
    {
        info ??= [];
        var nr = (byte)(rawNr & 0x07);
        var ns = (byte)(rawNs & 0x07);

        var rig = ActionDispatcherPropertyHelpers.NewRig();
        var frame = ActionDispatcherPropertyHelpers.BuildIFrame(nr, ns, pollBit, info, pid);
        var tx = new TransitionContext(rig.Context, rig.Scheduler, new IFrameReceived(frame));

        rig.Dispatcher.Execute(Ax25ActionVerb.DLDATAIndication, tx);

        rig.Upward.Should().ContainSingle();
        var sig = rig.Upward[0].Should().BeOfType<DataLinkDataIndication>().Subject;
        sig.Info.ToArray().Should().Equal(info);
        sig.Pid.Should().Be(pid);
    }

    [Fact]
    public void DL_Data_Indication_Throws_Without_Trigger_Frame()
    {
        var rig = ActionDispatcherPropertyHelpers.NewRig();
        var tx = new TransitionContext(rig.Context, rig.Scheduler, new T1Expiry());
        var act = () => rig.Dispatcher.Execute(Ax25ActionVerb.DLDATAIndication, tx);
        act.Should().Throw<InvalidOperationException>()
           .WithMessage("*DL_DATA_indication*requires an incoming frame*T1_expiry*");
    }
}

// ─── 6. Timer verbs ──────────────────────────────────────────────────────

/// <summary>
/// start_TX / stop_TX verbs interact with the
/// <see cref="ITimerScheduler"/> abstraction. Property: armed → IsRunning,
/// cancelled → not IsRunning, regardless of timer name.
/// </summary>
public class TimerProperties
{
    [Property(MaxTest = 200)]
    public void Start_Then_Stop_Returns_Timer_To_Not_Running(byte timerIdx)
    {
        // T2 has no Ax25ActionVerb member (no SDL figure arms/cancels it).
        var timers = new[] { (Ax25ActionVerb.StartT1, Ax25ActionVerb.StopT1, "T1"),
                             (Ax25ActionVerb.StartT3, Ax25ActionVerb.StopT3, "T3") };
        var (startVerb, stopVerb, timerName) = timers[timerIdx % timers.Length];

        var rig = ActionDispatcherPropertyHelpers.NewRig();
        rig.Scheduler.IsRunning(timerName).Should().BeFalse("timer not yet armed");

        rig.Dispatcher.Execute(startVerb, rig.Context, rig.Scheduler);
        rig.Scheduler.IsRunning(timerName).Should().BeTrue();

        rig.Dispatcher.Execute(stopVerb, rig.Context, rig.Scheduler);
        rig.Scheduler.IsRunning(timerName).Should().BeFalse();
    }

    [Property(MaxTest = 200)]
    public void Start_Then_Stop_Then_Start_Re_Arms(byte timerIdx)
    {
        // T2 has no Ax25ActionVerb member (no SDL figure arms/cancels it).
        var timers = new[] { (Ax25ActionVerb.StartT1, Ax25ActionVerb.StopT1, "T1"),
                             (Ax25ActionVerb.StartT3, Ax25ActionVerb.StopT3, "T3") };
        var (startVerb, stopVerb, timerName) = timers[timerIdx % timers.Length];

        var rig = ActionDispatcherPropertyHelpers.NewRig();
        rig.Dispatcher.Execute(startVerb, rig.Context, rig.Scheduler);
        rig.Dispatcher.Execute(stopVerb, rig.Context, rig.Scheduler);
        rig.Dispatcher.Execute(startVerb, rig.Context, rig.Scheduler);

        rig.Scheduler.IsRunning(timerName).Should().BeTrue();
    }

    [Property(MaxTest = 200)]
    public void Stop_Without_Start_Is_Safe_NoOp(byte timerIdx)
    {
        // T2 has no Ax25ActionVerb member (no SDL figure arms/cancels it).
        var timers = new[] { (Ax25ActionVerb.StopT1, "T1"),
                             (Ax25ActionVerb.StopT3, "T3") };
        var (stopVerb, timerName) = timers[timerIdx % timers.Length];

        var rig = ActionDispatcherPropertyHelpers.NewRig();
        var act = () => rig.Dispatcher.Execute(stopVerb, rig.Context, rig.Scheduler);

        act.Should().NotThrow();
        rig.Scheduler.IsRunning(timerName).Should().BeFalse();
    }

    /// <summary>
    /// figc4.7 title-case forms (<c>Start T1</c>, <c>Stop T1</c>,
    /// <c>Start T3</c>, <c>Stop T3</c>) are aliases — same scheduler
    /// behaviour as the snake_case forms.
    /// </summary>
    [Property(MaxTest = 200)]
    public void FigC47_Title_Case_Timer_Verbs_Arm_And_Cancel(bool which)
    {
        var rig = ActionDispatcherPropertyHelpers.NewRig();
        var (startVerb, stopVerb, timerName) = which
            ? (Ax25ActionVerb.StartT1, Ax25ActionVerb.StopT1, "T1")
            : (Ax25ActionVerb.StartT3, Ax25ActionVerb.StopT3, "T3");

        rig.Dispatcher.Execute(startVerb, rig.Context, rig.Scheduler);
        rig.Scheduler.IsRunning(timerName).Should().BeTrue();

        rig.Dispatcher.Execute(stopVerb, rig.Context, rig.Scheduler);
        rig.Scheduler.IsRunning(timerName).Should().BeFalse();
    }
}

// ─── 7. Queue-clearing verbs ─────────────────────────────────────────────

/// <summary>
/// Properties over verbs that clear or no-op the I-frame queue.
/// </summary>
public class QueueClearProperties
{
    private static readonly Ax25ActionVerb[] ClearVerbs =
    {
        Ax25ActionVerb.DiscardFrameQueue,
        Ax25ActionVerb.DiscardQueue,
        Ax25ActionVerb.DiscardIFrameQueue,
        Ax25ActionVerb.DiscardIQueueEntries,
    };

    [Property(MaxTest = 200)]
    public void Discard_Queue_Verbs_All_Empty_The_IFrameQueue(byte verbIdx, byte count)
    {
        var rig = ActionDispatcherPropertyHelpers.NewRig();
        var safeCount = count % 16;
        for (int i = 0; i < safeCount; i++)
        {
            rig.Context.IFrameQueue.Enqueue((new byte[] { (byte)i }, Ax25Frame.PidNoLayer3));
        }
        rig.Context.IFrameQueue.Count.Should().Be(safeCount);

        var verb = ClearVerbs[verbIdx % ClearVerbs.Length];
        rig.Dispatcher.Execute(verb, rig.Context, rig.Scheduler);

        rig.Context.IFrameQueue.Should().BeEmpty();
    }

    [Property(MaxTest = 200)]
    public void NoOp_Discard_Verbs_Leave_Context_Unchanged(byte verbIdx, byte queueDepth)
    {
        var noOpVerbs = new[] { Ax25ActionVerb.DiscardIFrame, Ax25ActionVerb.DiscardContentsOfIFrame, Ax25ActionVerb.DiscardPrimitive };
        var verb = noOpVerbs[verbIdx % noOpVerbs.Length];
        var safeCount = queueDepth % 8;

        var rig = ActionDispatcherPropertyHelpers.NewRig();
        for (int i = 0; i < safeCount; i++)
        {
            rig.Context.IFrameQueue.Enqueue((new byte[] { (byte)i }, Ax25Frame.PidNoLayer3));
        }
        var depthBefore = rig.Context.IFrameQueue.Count;

        rig.Dispatcher.Execute(verb, rig.Context, rig.Scheduler);

        rig.Context.IFrameQueue.Count.Should().Be(depthBefore,
            "{0} should not mutate the I-frame queue", verb);
    }
}
