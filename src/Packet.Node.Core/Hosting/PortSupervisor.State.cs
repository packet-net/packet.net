using Microsoft.Extensions.Logging;
using Packet.Ax25.Transport;
using Packet.Node.Core.Configuration;

namespace Packet.Node.Core.Hosting;

/// <summary>
/// The port state model + running-state supervision half of the supervisor
/// (packet-net/packet.net#722): one <see cref="PortEntry"/> per <b>configured</b> port
/// holding its <see cref="PortState"/>, a watchdog that notices a port dying on the air,
/// and one bounded-backoff retry policy for every transport kind.
/// </summary>
public sealed partial class PortSupervisor : IPortHealthView
{
    /// <summary>How often the running-state watchdog checks each serving port's listener
    /// (and its transport link state). Driven off the supervisor's <c>TimeProvider</c>.</summary>
    public static readonly TimeSpan SupervisionInterval = TimeSpan.FromSeconds(5);

    /// <summary>The first bring-up retry runs this long after the failure.</summary>
    public static readonly TimeSpan RetryInitialDelay = TimeSpan.FromSeconds(5);

    /// <summary>The retry backoff doubles up to this ceiling and then keeps trying there
    /// forever - a port that can come back must not need an operator to notice.</summary>
    public static readonly TimeSpan RetryMaxDelay = TimeSpan.FromSeconds(60);

    /// <summary>How long one retry attempt's transport open may take before it is abandoned
    /// and re-queued (a blackholing head-end: DROP firewall, dead Pi).</summary>
    public static readonly TimeSpan RetryAttemptTimeout = TimeSpan.FromSeconds(30);

    // After the first failure (logged Warning), only every Nth attempt warns; the rest are
    // Debug. A head-end that is down for a week must not fill the journal.
    private const int RetryWarnEvery = 10;

    /// <summary>
    /// Raised after every committed port state transition. Observation-only: the supervisor
    /// swallows subscriber exceptions (a buggy telemetry hook must not break a reconcile) and
    /// raises outside its own lock, so a handler may call back into the read surface.
    /// </summary>
    public event Action<PortStateChange>? PortStateChanged;

    /// <summary>
    /// One configured port. Owns the port's state, its config baseline, its degraded set, its
    /// armed retry and (while serving) the <see cref="RunningPort"/> that is the runtime half.
    /// Every member is read and written under the supervisor's <c>ports</c> lock.
    /// </summary>
    private sealed class PortEntry
    {
        public required string Id { get; init; }

        /// <summary>The config this port is running on (or would next come up on). THE per-port
        /// config baseline - <see cref="RunningPort"/> deliberately holds none, so there is one
        /// answer to "what is this port configured as" (#722). The node-wide reconcile baseline
        /// is a different thing and stays on <see cref="NodeHostedService"/>.</summary>
        public required PortConfig Config { get; set; }

        public PortState State { get; set; } = PortState.Configured;

        public DateTimeOffset Since { get; set; }

        public string? LastError { get; set; }

        public List<string> Degraded { get; } = [];

        public int RetryAttempt { get; set; }

        /// <summary>The runtime half while the port is serving (and during its teardown).</summary>
        public RunningPort? Running { get; set; }

        /// <summary>The armed bring-up retry loop's cancellation, or null when none is armed.</summary>
        public CancellationTokenSource? Retry { get; set; }

        /// <summary>Set while something deliberately stopped this port's listener (a tuning
        /// session pause), so the watchdog does not read the stopped listener as a death and
        /// restart the port underneath it. Cleared by the next teardown / bring-up.</summary>
        public bool SupervisionSuspended { get; set; }

        public PortHealth ToHealth() => new()
        {
            Id = Id,
            State = State,
            Since = Since,
            LastError = LastError,
            Degraded = Degraded.Count == 0 ? [] : [.. Degraded],
            RetryAttempt = RetryAttempt,
        };
    }

    // ── the read surface (IPortHealthView) ───────────────────────────────────────────

    /// <inheritdoc/>
    public PortHealth? GetHealth(string id)
    {
        ArgumentNullException.ThrowIfNull(id);
        lock (ports)
        {
            if (ports.TryGetValue(id, out var entry))
            {
                return entry.ToHealth();
            }
        }

        // Not reconciled yet (a port added to config since the last reconcile, or a read that
        // beat StartAsync): answer from config so the surface is total, never a lie about a
        // port that exists.
        var configured = config.Current.Ports.FirstOrDefault(p => string.Equals(p.Id, id, StringComparison.Ordinal));
        return configured is null ? null : Unattempted(configured);
    }

    /// <inheritdoc/>
    public IReadOnlyList<PortHealth> Snapshot()
    {
        var configured = config.Current.Ports;
        var result = new List<PortHealth>(configured.Count);
        lock (ports)
        {
            // Canonical order: config order (the same 1-indexed order the console PORTS listing
            // and `C <n> <call>` use).
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var port in configured)
            {
                seen.Add(port.Id);
                result.Add(ports.TryGetValue(port.Id, out var entry) ? entry.ToHealth() : Unattempted(port));
            }

            // Defensive: an entry with no config line (a removal that raced this read) still
            // shows, rather than vanishing silently.
            foreach (var entry in ports.Values.Where(e => !seen.Contains(e.Id)).OrderBy(e => e.Id, StringComparer.Ordinal))
            {
                result.Add(entry.ToHealth());
            }
        }

        return result;
    }

    private PortHealth Unattempted(PortConfig port) => new()
    {
        Id = port.Id,
        State = port.Enabled ? PortState.Configured : PortState.Disabled,
        Since = timeProvider.GetUtcNow(),
    };

    /// <summary>The port's live config baseline (what it is running on), or null when the id is
    /// unknown. The single answer to "what is this port configured as" - the runtime half
    /// (<see cref="RunningPort"/>) deliberately carries no config of its own (#722).</summary>
    public PortConfig? GetPortConfig(string id)
    {
        ArgumentNullException.ThrowIfNull(id);
        lock (ports)
        {
            if (ports.TryGetValue(id, out var entry))
            {
                return entry.Config;
            }
        }

        return config.Current.Ports.FirstOrDefault(p => string.Equals(p.Id, id, StringComparison.Ordinal));
    }

    /// <summary>
    /// Tell the watchdog that this port's listener is stopped <b>deliberately</b> (the tuning
    /// session pause), so it is not read as a death and restarted mid-session. Cleared by the
    /// next teardown or bring-up of the port - which is exactly how tuning restores it
    /// (<c>RestartPortAsync</c>).
    /// </summary>
    public void SuspendSupervision(string id)
    {
        ArgumentNullException.ThrowIfNull(id);
        lock (ports)
        {
            if (ports.TryGetValue(id, out var entry))
            {
                entry.SupervisionSuspended = true;
            }
        }
    }

    // ── transitions ──────────────────────────────────────────────────────────────────

    // The ONE place a port's state moves. Illegal moves are logged as an error (the model has
    // a hole) but still applied: bookkeeping must never leave the real port set and the model
    // disagreeing. The tests assert every transition a real path produces is legal, so the hole
    // fails CI rather than only the journal.
    private void SetState(
        string id, PortState to, string reason,
        string? lastError = null, IReadOnlyList<string>? degraded = null, int? retryAttempt = null)
    {
        PortStateChange? change = null;
        lock (ports)
        {
            if (!ports.TryGetValue(id, out var entry))
            {
                return;
            }

            var from = entry.State;
            if (!PortStateMachine.IsLegal(from, to))
            {
                LogIllegalTransition(id, PortStates.Name(from), PortStates.Name(to), reason);
            }

            entry.State = to;
            entry.Since = timeProvider.GetUtcNow();
            if (lastError is not null)
            {
                entry.LastError = lastError;
            }

            if (degraded is not null)
            {
                entry.Degraded.Clear();
                entry.Degraded.AddRange(degraded);
            }

            if (retryAttempt is { } attempt)
            {
                entry.RetryAttempt = attempt;
            }

            if (from != to || lastError is not null || degraded is not null)
            {
                change = new PortStateChange(id, from, to, entry.ToHealth());
            }
        }

        if (change is null)
        {
            return;
        }

        if (logger.IsEnabled(LogLevel.Debug))
        {
            // Hoisted (CA1873): keep the name lookups out of the log call site.
            var fromName = PortStates.Name(change.From);
            var toName = PortStates.Name(change.To);
            LogPortStateChanged(id, fromName, toName, reason);
        }

        var handler = PortStateChanged;
        if (handler is null)
        {
            return;
        }

        try
        {
            handler(change);
        }
        catch (Exception ex)
        {
            LogPortStateHandlerFaulted(ex, id);
        }
    }

    // Make sure every configured port has an entry (and that each entry's config baseline is
    // the current one), and drop entries for ports that left the config and are not running -
    // a port the plan is about to tear down keeps its entry until the teardown removes it, or
    // its RunningPort would leak. Called at the top of every mutation path so the model covers
    // the config even before the ports come up.
    private void SyncEntries(NodeConfig current)
    {
        lock (ports)
        {
            var configured = current.Ports.Select(p => p.Id).ToHashSet(StringComparer.Ordinal);
            foreach (var stale in ports.Values.Where(e => e.Running is null && !configured.Contains(e.Id)).ToArray())
            {
                CancelRetry(stale);
                ports.Remove(stale.Id);
            }

            foreach (var port in current.Ports)
            {
                if (ports.TryGetValue(port.Id, out var entry))
                {
                    entry.Config = port;
                }
                else
                {
                    ports[port.Id] = new PortEntry
                    {
                        Id = port.Id,
                        Config = port,
                        State = port.Enabled ? PortState.Configured : PortState.Disabled,
                        Since = timeProvider.GetUtcNow(),
                    };
                }
            }
        }
    }

    // The entry for a port, created on demand (a restart of a port the config carries, a
    // bring-up during a node-wide reset).
    private PortEntry EnsureEntry(PortConfig port)
    {
        lock (ports)
        {
            if (ports.TryGetValue(port.Id, out var entry))
            {
                entry.Config = port;
                return entry;
            }

            var created = new PortEntry
            {
                Id = port.Id,
                Config = port,
                State = port.Enabled ? PortState.Configured : PortState.Disabled,
                Since = timeProvider.GetUtcNow(),
            };
            ports[port.Id] = created;
            return created;
        }
    }

    private bool IsServing(string id)
    {
        lock (ports)
        {
            return ports.TryGetValue(id, out var entry) && entry.State is PortState.Up or PortState.Degraded;
        }
    }

    // Record a component this bring-up could not attach. The port keeps going (none of these
    // is on the data path); the final transition turns a non-empty set into Degraded.
    private void NoteDegraded(string id, string component, string reason)
    {
        lock (ports)
        {
            if (ports.TryGetValue(id, out var entry) && !entry.Degraded.Contains(component, StringComparer.Ordinal))
            {
                entry.Degraded.Add(component);
                entry.LastError = reason;
            }
        }
    }

    // ── the running-state watchdog ───────────────────────────────────────────────────

    /// <summary>
    /// Watch the ports that are actually serving: the AX.25 listener marks itself not-running
    /// when its inbound pump faults (a USB TNC unplugged, a serial pump that died), and until
    /// #722 nothing observed that - the port stayed green on the dashboard, in metrics and in
    /// PORTS while being deaf and dumb on the air. Also folds the reconnect decorator's link
    /// state in as a degraded component, so a networked port mid-reconnect says so.
    /// </summary>
    private async Task SuperviseLoopAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(SupervisionInterval, timeProvider, ct).ConfigureAwait(false);
                await SuperviseOnceAsync(ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // supervisor shutting down
        }
        catch (Exception ex)
        {
            // A watchdog that dies silently is worse than none: say so, loudly, once.
            LogSupervisionLoopFaulted(ex);
        }
    }

    // Exposed (internal) so a test can drive one supervision pass deterministically instead of
    // racing the loop's timer.
    internal async Task SuperviseOnceAsync(CancellationToken ct)
    {
        (string Id, RunningPort Running, bool Reconnecting)[] serving;
        lock (ports)
        {
            serving =
            [
                .. ports.Values
                    .Where(e => e.State is PortState.Up or PortState.Degraded
                        && e.Running is not null && !e.SupervisionSuspended)
                    .Select(e => (e.Id, Running: e.Running!, Reconnecting: e.Running!.LinkState?.IsReconnecting == true)),
            ];
        }

        foreach (var (id, running, reconnecting) in serving)
        {
            if (ct.IsCancellationRequested)
            {
                return;
            }

            if (!running.Listener.IsRunning)
            {
                await FaultDeadPortAsync(id, running, ct).ConfigureAwait(false);
                continue;
            }

            // The transport chain is self-healing on networked ports, so a reconnect is a
            // degradation, not a fault: the listener is alive and the port recovers on its own.
            UpdateTransportDegradation(id, reconnecting);
        }
    }

    private void UpdateTransportDegradation(string id, bool reconnecting)
    {
        bool degradedNow;
        bool changed;
        lock (ports)
        {
            if (!ports.TryGetValue(id, out var entry) || entry.State is not (PortState.Up or PortState.Degraded))
            {
                return;
            }

            bool had = entry.Degraded.Contains(PortComponents.Transport, StringComparer.Ordinal);
            changed = had != reconnecting;
            if (!changed)
            {
                return;
            }

            if (reconnecting)
            {
                entry.Degraded.Add(PortComponents.Transport);
            }
            else
            {
                entry.Degraded.Remove(PortComponents.Transport);
            }

            degradedNow = entry.Degraded.Count > 0;
        }

        SetState(id, degradedNow ? PortState.Degraded : PortState.Up,
            reconnecting ? "transport reconnecting" : "transport reconnected",
            lastError: reconnecting ? "the port's transport lost its link and is re-dialling" : null);
    }

    // A serving port whose listener died: fault it, tear DOWN THAT PORT ONLY, and arm the retry.
    private async Task FaultDeadPortAsync(string id, RunningPort running, CancellationToken ct)
    {
        // The listener's own fault log carries the exception; all we can honestly say from here
        // is that it stopped. (The listener deliberately does not widen its surface with a
        // last-error member - it is parity-tracked against ax25-ts.)
        const string Reason = "listener stopped";

        await mutationGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            // Re-check under the gate: a reconcile may have restarted the port meanwhile, in
            // which case this RunningPort is already gone and there is nothing to fault.
            lock (ports)
            {
                if (!ports.TryGetValue(id, out var entry)
                    || !ReferenceEquals(entry.Running, running)
                    || entry.State is not (PortState.Up or PortState.Degraded))
                {
                    return;
                }
            }

            LogPortDied(id, Reason);
            await TearDownAsync(id, TeardownReason.Fault).ConfigureAwait(false);
            SetState(id, PortState.Faulted, "listener died", lastError: Reason, degraded: []);
        }
        finally
        {
            mutationGate.Release();
        }

        ArmRetry(id);
    }

    // ── the bring-up retry (one policy, every transport kind) ────────────────────────

    /// <summary>
    /// Arm the bounded-backoff bring-up retry for a port that just failed. One loop per port
    /// (re-arming while armed is a no-op). Replaces the old head-end-only special case: whether
    /// a port can come back is decided by observing it, not by its transport kind - a serial TNC
    /// that enumerates seconds after the node, or a kiss-tcp softmodem not yet listening, is
    /// every bit as recoverable as a head-end that is still booting.
    /// </summary>
    private void ArmRetry(string id)
    {
        if (Volatile.Read(ref disposed) != 0 || lifecycle.IsCancellationRequested)
        {
            return;
        }

        CancellationTokenSource cts;
        lock (ports)
        {
            if (!ports.TryGetValue(id, out var entry) || entry.Retry is not null || entry.State != PortState.Faulted)
            {
                return;
            }

            if (!entry.Config.Enabled)
            {
                return;   // a disabled port is down by design; nothing to retry
            }

            cts = CancellationTokenSource.CreateLinkedTokenSource(lifecycle.Token);
            entry.Retry = cts;
        }

        SetState(id, PortState.Retrying, "retry armed", retryAttempt: 0);
        LogRetryArmed(id, (int)RetryInitialDelay.TotalSeconds, (int)RetryMaxDelay.TotalSeconds);
        // Deliberately NOT the reconcile's token: the loop outlives the reconcile that armed it
        // and is cancelled by its own linked CTS (lifecycle / teardown / success).
        _ = Task.Run(() => RetryLoopAsync(id, cts), CancellationToken.None);
    }

    // Cancel a port's armed retry (a teardown, a successful bring-up, an entry removal).
    private static void CancelRetry(PortEntry entry)
    {
        var cts = entry.Retry;
        entry.Retry = null;
        entry.RetryAttempt = 0;
        if (cts is not null)
        {
            try
            {
                cts.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // the loop already finished and disposed it
            }
        }
    }

    private async Task RetryLoopAsync(string id, CancellationTokenSource cts)
    {
        var ct = cts.Token;
        var delay = RetryInitialDelay;
        int attempt = 0;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(delay, timeProvider, ct).ConfigureAwait(false);
                attempt++;

                // Always read LIVE config: an edit between attempts must win, and a reconcile
                // that removed or disabled the port ends the loop.
                var current = config.Current;
                var port = current.Ports.FirstOrDefault(p => string.Equals(p.Id, id, StringComparison.Ordinal));
                if (port is null || !port.Enabled)
                {
                    LogRetryAbandoned(id);
                    return;
                }

                if (IsServing(id))
                {
                    return;   // a reconcile/restart brought it up meanwhile
                }

                SetState(id, PortState.Retrying, "retry attempt", retryAttempt: attempt);

                // PHASE 1, NO GATE HELD. Opening the transport is the step that blackholes
                // against a dead head-end (DROP firewall, unplugged Pi), and the old loop held
                // the supervisor's mutation gate across the whole attempt - so every reconcile
                // and every web action queued behind a port that was not even up (#722). Bound
                // it and do it outside the gate.
                IAx25Transport? opened;
                using (var timeout = new CancellationTokenSource(RetryAttemptTimeout, timeProvider))
                using (var attemptCts = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token))
                {
                    try
                    {
                        opened = await transportFactory
                            .CreateAsync(port.Transport, timeProvider, BuildHeadEndResolver(), attemptCts.Token)
                            .ConfigureAwait(false);
                    }
                    catch (Exception ex) when (!ct.IsCancellationRequested)
                    {
                        RecordRetryFailure(id, attempt, ex is OperationCanceledException
                            ? $"the transport did not open within {(int)RetryAttemptTimeout.TotalSeconds}s"
                            : ex.Message);
                        delay = NextDelay(delay);
                        continue;
                    }
                }

                // PHASE 2, GATE HELD, and only for the fast tail: the pipe is already open, so
                // this is listener construction + the port-set mutation, no dial.
                bool up;
                await mutationGate.WaitAsync(ct).ConfigureAwait(false);
                try
                {
                    var live = config.Current;
                    var livePort = live.Ports.FirstOrDefault(p => string.Equals(p.Id, id, StringComparison.Ordinal));
                    if (livePort is null || !livePort.Enabled || IsServing(id))
                    {
                        await opened.DisposeAsync().ConfigureAwait(false);
                        LogRetryAbandoned(id);
                        return;
                    }
                    if (!Equals(livePort.Transport, port.Transport))
                    {
                        // The config moved under us between the ungated open and this gated
                        // adopt: the pipe we hold was opened for a transport block the port no
                        // longer has. Never attach it; discard and let the next attempt open
                        // the live one.
                        await opened.DisposeAsync().ConfigureAwait(false);
                        RecordRetryFailure(id, attempt, "the port's transport changed while the retry was opening it");
                        delay = NextDelay(delay);
                        continue;
                    }

                    // BringUpAsync owns the pipe from here, success or failure.
                    await BringUpAsync(livePort, live.Identity, ct, quiet: true, preOpened: opened)
                        .ConfigureAwait(false);
                    up = IsServing(id);
                }
                finally
                {
                    mutationGate.Release();
                }

                if (up)
                {
                    LogRetrySucceeded(id, attempt);
                    return;
                }

                delay = NextDelay(delay);
            }
        }
        catch (OperationCanceledException)
        {
            // torn down, brought up by a reconcile, or the supervisor is shutting down
        }
        catch (Exception) when (ct.IsCancellationRequested)
        {
            // A teardown cancelled us mid-attempt and the transport reported the cancellation
            // as its own failure kind (a socket abort, say) rather than an OperationCanceled.
        }
        catch (Exception ex)
        {
            LogRetryLoopFaulted(ex, id);
        }
        finally
        {
            lock (ports)
            {
                if (ports.TryGetValue(id, out var entry) && ReferenceEquals(entry.Retry, cts))
                {
                    entry.Retry = null;
                }
            }

            cts.Dispose();
        }
    }

    private static TimeSpan NextDelay(TimeSpan current)
    {
        var doubled = current + current;
        return doubled > RetryMaxDelay ? RetryMaxDelay : doubled;
    }

    // A failed attempt: keep the port in Retrying with the reason, warn on the first and then
    // every Nth attempt (a head-end down for a week must not fill the journal), Debug otherwise.
    private void RecordRetryFailure(string id, int attempt, string reason)
    {
        SetState(id, PortState.Retrying, "retry failed", lastError: reason, retryAttempt: attempt);
        if (attempt == 1 || attempt % RetryWarnEvery == 0)
        {
            LogRetryStillFailing(id, attempt, reason);
        }
        else
        {
            LogPortRetryStillDown(id, reason);
        }
    }

    [LoggerMessage(Level = LogLevel.Debug, Message = "Port {Id}: {From} -> {To} ({Reason}).")]
    private partial void LogPortStateChanged(string id, string from, string to, string reason);

    [LoggerMessage(Level = LogLevel.Error, Message = "Port {Id}: ILLEGAL state transition {From} -> {To} ({Reason}) - the port model has a hole; the move was applied anyway.")]
    private partial void LogIllegalTransition(string id, string from, string to, string reason);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Port {Id}: a state-change handler faulted; the port set is unaffected.")]
    private partial void LogPortStateHandlerFaulted(Exception ex, string id);

    [LoggerMessage(Level = LogLevel.Error, Message = "Port {Id} died while running ({Reason}); tearing it down and retrying.")]
    private partial void LogPortDied(string id, string reason);

    [LoggerMessage(Level = LogLevel.Error, Message = "The port running-state watchdog faulted and has stopped; ports that die will no longer be noticed until the node restarts.")]
    private partial void LogSupervisionLoopFaulted(Exception ex);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Port {Id}: bring-up failed; retrying in {FirstSeconds}s, backing off to every {MaxSeconds}s until it comes up.")]
    private partial void LogRetryArmed(string id, int firstSeconds, int maxSeconds);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Port {Id}: bring-up retry attempt {Attempt} failed ({Reason}); still retrying.")]
    private partial void LogRetryStillFailing(string id, int attempt, string reason);

    [LoggerMessage(Level = LogLevel.Information, Message = "Port {Id}: bring-up retry succeeded on attempt {Attempt}; the port is up.")]
    private partial void LogRetrySucceeded(string id, int attempt);

    [LoggerMessage(Level = LogLevel.Information, Message = "Port {Id}: bring-up retry abandoned (port removed or disabled).")]
    private partial void LogRetryAbandoned(string id);

    [LoggerMessage(Level = LogLevel.Error, Message = "Port {Id}: the bring-up retry loop faulted and has stopped; the port stays down until the next config change.")]
    private partial void LogRetryLoopFaulted(Exception ex, string id);
}
