using Packet.Ax25.Sdl;

namespace Packet.Ax25.Session;

/// <summary>
/// Executes an SDL action chain, expanding any loops
/// (<see cref="LoopRange"/>, from Packet.Ax25.Sdl 0.7.0+). Shared by the
/// state-machine transition path (<see cref="Ax25Session"/>) and the
/// subroutine path (<see cref="ISubroutineRegistry"/>) so loop semantics are
/// identical wherever a loop appears - including
/// <c>Invoke_Retransmission</c>, which is a subroutine.
/// </summary>
/// <remarks>
/// Each <see cref="LoopRange"/> marks a body slice over the flat
/// <c>actions</c> list to re-run while its continue predicate
/// holds. Loops are non-overlapping and non-nested (the codegen guarantees
/// it), so we walk the actions once, running each loop's body slice in
/// place. Test-at-head loops (<c>while</c>) check the predicate before each
/// iteration (zero-or-more runs); test-at-tail loops (<c>do-while</c>) check
/// after (one-or-more). The flat body slice is owned by the loop.
/// </remarks>
internal static class SdlLoopExecutor
{
    /// <summary>
    /// Upper bound on loop iterations. The legitimate maximum is the send
    /// window (k ≤ 128); beyond this the body isn't advancing the state its
    /// continue predicate reads, so we fail loudly rather than hang.
    /// </summary>
    private const int MaxLoopIterations = 1024;

    public static void Execute(
        IReadOnlyList<ActionStep> actions,
        IReadOnlyList<LoopRange> loops,
        IActionDispatcher dispatcher,
        GuardEvaluator guards,
        TransitionContext tx)
    {
        if (loops.Count == 0)
        {
            dispatcher.Execute(actions, tx);
            return;
        }

        var ordered = loops.OrderBy(l => l.Start).ToList();
        int idx = 0;
        foreach (var loop in ordered)
        {
            if (idx < loop.Start)
            {
                dispatcher.Execute(Slice(actions, idx, loop.Start - idx), tx);
            }

            RunLoop(loop, actions, dispatcher, guards, tx);
            idx = loop.Start + loop.Length;
        }
        if (idx < actions.Count)
        {
            dispatcher.Execute(Slice(actions, idx, actions.Count - idx), tx);
        }
    }

    private static void RunLoop(
        LoopRange loop,
        IReadOnlyList<ActionStep> actions,
        IActionDispatcher dispatcher,
        GuardEvaluator guards,
        TransitionContext tx)
    {
        var body = Slice(actions, loop.Start, loop.Length);
        int iterations = 0;
        if (loop.TestAtEnd)
        {
            do
            {
                dispatcher.Execute(body, tx);
                GuardIterations(ref iterations, loop);
            }
            while (guards.Evaluate(loop.Predicate));
        }
        else
        {
            while (guards.Evaluate(loop.Predicate))
            {
                dispatcher.Execute(body, tx);
                GuardIterations(ref iterations, loop);
            }
        }
    }

    private static void GuardIterations(ref int iterations, LoopRange loop)
    {
        if (++iterations > MaxLoopIterations)
        {
            throw new InvalidOperationException(
                $"SDL loop (predicate '{loop.Predicate}', body [{loop.Start}..{loop.Start + loop.Length})) " +
                $"exceeded {MaxLoopIterations} iterations without its continue predicate clearing — " +
                "the loop body is not advancing the state the predicate reads.");
        }
    }

    private static ActionStep[] Slice(IReadOnlyList<ActionStep> actions, int start, int length)
    {
        var slice = new ActionStep[length];
        for (int i = 0; i < length; i++)
        {
            slice[i] = actions[start + i];
        }

        return slice;
    }
}
