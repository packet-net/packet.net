namespace Packet.Node.Core.Radios;

/// <summary>
/// The node-wide single-flight gate for head-end <b>probe actions</b> — the fleet scan
/// (<see cref="HeadEndRadioScanner.ScanAsync"/>) and keyup pairing
/// (<see cref="HeadEndKeyupPairer"/>) — mirroring the local bus scanner's semaphore (#581). The
/// head-end bridge QUEUES a second raw-pipe client in the accept backlog rather than rejecting it,
/// and <c>POST /ports/{id}/line</c> re-clocks the UART regardless of who is connected: a scan's
/// Tait baud sweep racing an in-flight keyup pairing re-clocks the line under the pairer's PTT
/// watcher (it goes deaf → radios wrongly unpaired), and the scan's own probes sit in the backlog
/// (devices misclassified Unknown). One probe action at a time; waiters wait — bounded — rather
/// than failing immediately.
/// </summary>
/// <remarks>Global (process-wide) by design: one node process drives one head-end fleet, and the
/// simplest gate that both classes (whatever their DI lifetimes) share is a static one — the
/// option #581 explicitly endorses.</remarks>
internal static class HeadEndProbeGate
{
    private static readonly SemaphoreSlim Gate = new(1, 1);

    /// <summary>How long a waiter queues behind an in-flight probe action before giving up. A
    /// scan is bounded to seconds; keyup pairing to (devices × observation window) — 60 s covers
    /// both with headroom.</summary>
    public static readonly TimeSpan MaxWait = TimeSpan.FromSeconds(60);

    /// <summary>
    /// Enter the gate, waiting (bounded by <see cref="MaxWait"/>) behind any in-flight probe
    /// action. Throws <see cref="TimeoutException"/> — naming <paramref name="action"/> — if the
    /// gate never frees; pair every successful return with <see cref="Exit"/> in a finally.
    /// </summary>
    public static async Task EnterAsync(string action, CancellationToken cancellationToken)
    {
        if (!await Gate.WaitAsync(MaxWait, cancellationToken).ConfigureAwait(false))
        {
            throw new TimeoutException(
                $"the {action} waited {MaxWait.TotalSeconds:0}s for another head-end probe action " +
                "(scan or keyup pairing) to finish — one is still running.");
        }
    }

    /// <summary>Release the gate (call from a finally paired with <see cref="EnterAsync"/>).</summary>
    public static void Exit() => Gate.Release();
}
