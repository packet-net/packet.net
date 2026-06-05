namespace Packet.Interop.Tests;

/// <summary>
/// Owns the lifetime of one-or-more background "inbound pump" tasks (the
/// <c>Task.Run(() =&gt; InboundPump(rig, ct))</c> loops the connected-mode interop
/// scenarios spin up to feed KISS frames into a session) and <b>guarantees they are
/// cancelled and awaited on EVERY exit path</b> — pass, assertion-failure, throw, or
/// timeout — via <see cref="IAsyncDisposable"/>.
/// </summary>
/// <remarks>
/// <para>
/// The hazard this closes: the scenarios previously cancelled + awaited the pumps with
/// a bare <c>cts.Cancel(); await Task.WhenAll(pumps)</c> at the <em>end of the happy
/// path</em>. When an assertion before that line threw (the common case for a flaky
/// interop test), the pump tasks were abandoned — left running against KISS sockets the
/// test's <c>await using</c> was about to dispose, and racing the test's <c>using var
/// cts</c> disposal. That risks an <see cref="ObjectDisposedException"/> on the disposed
/// CTS surfacing as an <em>unobserved task exception</em> on the finalizer thread, and
/// leaves background work running into the next test on a persistent CI runner — the
/// teardown-robustness gap the §7.2 isolation work otherwise hardens at the docker layer.
/// </para>
/// <para>
/// The scope holds its OWN linked <see cref="CancellationTokenSource"/> so pump lifetime
/// is decoupled from the test's outer timeout CTS: the pumps run on
/// <see cref="Token"/> (linked to the supplied outer token), and
/// <see cref="DisposeAsync"/> cancels that source then awaits all pumps, swallowing the
/// expected cancellation and any residual fault (a genuine pump fault is surfaced to the
/// test promptly by <see cref="ThrowIfAnyFaulted"/> during the wait helpers; on the
/// teardown path we only need every pump <em>observed</em> so the task plumbing doesn't
/// escalate). Declare the scope AFTER the test's <c>using var cts</c> so it disposes
/// first — pumps stop before the outer CTS is torn down.
/// </para>
/// </remarks>
internal sealed class InboundPumpScope : IAsyncDisposable
{
    private readonly CancellationTokenSource cts;
    private readonly Task[] tasks;

    private InboundPumpScope(CancellationTokenSource cts, Task[] tasks)
    {
        this.cts = cts;
        this.tasks = tasks;
    }

    /// <summary>
    /// Start one pump per supplied loop. Each <paramref name="pumps"/> delegate receives
    /// the scope's (linked) cancellation token and is run on the thread pool via
    /// <see cref="Task.Run(Func{Task}, CancellationToken)"/>.
    /// </summary>
    public static InboundPumpScope Start(CancellationToken outer, params Func<CancellationToken, Task>[] pumps)
    {
        var cts = CancellationTokenSource.CreateLinkedTokenSource(outer);
        var tasks = new Task[pumps.Length];
        for (int i = 0; i < pumps.Length; i++)
        {
            var pump = pumps[i];
            tasks[i] = Task.Run(() => pump(cts.Token), cts.Token);
        }
        return new InboundPumpScope(cts, tasks);
    }

    /// <summary>The running pump tasks — pass to the wait helpers (which call their
    /// <c>ThrowIfAnyFaulted</c>) so a backgrounded crash (e.g. an unbound predicate
    /// throwing inside the rx pump) becomes an immediate test failure with the real stack
    /// trace, rather than a budget timeout that hides the cause.</summary>
    public IReadOnlyList<Task> Tasks => tasks;

    /// <summary>Cancel every pump and await its completion — on EVERY exit path. Expected
    /// cancellation and any residual fault are swallowed (a real fault was already
    /// surfaced to the test by <see cref="ThrowIfAnyFaulted"/>); the point here is that no
    /// pump is left running or unobserved.</summary>
    public async ValueTask DisposeAsync()
    {
        try
        {
            await cts.CancelAsync().ConfigureAwait(false);
        }
        catch (ObjectDisposedException) { /* already disposed — pumps are already done */ }

        try
        {
            await Task.WhenAll(tasks).ConfigureAwait(false);
        }
        catch
        {
            // Teardown path: every pump is now completed (faulted, cancelled, or clean).
            // Real faults are surfaced to the test during the run via ThrowIfAnyFaulted;
            // here we only need them observed so the task plumbing can't escalate to an
            // unobserved-exception on the finalizer thread.
        }
        finally
        {
            cts.Dispose();
        }
    }
}
