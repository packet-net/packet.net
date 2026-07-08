using System.Globalization;
using System.Threading.Channels;

namespace Packet.Tune.Core;

/// <summary>
/// The driving end of the TXDELAY-minimisation protocol: sweep this station's own KISS
/// TXDELAY <b>down</b> from its configured value over an <see cref="ITuningLink"/>
/// side channel (canonically <see cref="SdmTuningLink"/> — mode/TXDELAY-agnostic, so
/// the coordination keeps working while the parameter under it is being changed), with
/// the far station (<see cref="TxDelayMinResponder"/>) counting decodes per step.
/// </summary>
/// <remarks>
/// <para>Choreography of one sweep (all <c>TXD</c> telegrams, seq-numbered and deduped
/// by the link):</para>
/// <list type="number">
///   <item>C→M <c>propose|k|start|step</c>; the meter answers <c>confirm</c> (or
///     <c>reject</c> — nothing has been transmitted yet).</item>
///   <item>The coordinator pins its channel-access params (persistence 255 /
///     slottime 0 — the TNC's random CSMA deferral would otherwise swamp the
///     measurement). From here every exit path restores.</item>
///   <item>Per step (descending from <c>start</c> by <c>step</c>, floored at the
///     option minimum): C→M <c>step|ms|k</c> (its sequence number is the tag every
///     probe keying carries; the meter opens its counter on receipt) → wedge-guard
///     delay → set TXDELAY + settle frame + unkey gap → k probes as k SEPARATE
///     keyings with gaps → C→M <c>sent|ms|k</c> → meter replies
///     <c>report|ms|dec/k[|preMs]</c>.</item>
///   <item>Stepping stops at the first non-full decode (the drop step) or the floor.
///     Knee = the lowest full-decode step; recommendation = knee + margin
///     (<see cref="TxDelayMinReport.Recommend"/>).</item>
///   <item>The sweep NEVER leaves the reduced TXDELAY in place: the original value +
///     polite channel access are restored on every path. APPLY is a separate explicit
///     call (<see cref="ApplyAsync"/>).</item>
/// </list>
/// <para>Only this station's TXDELAY changes — the meter is purely passive, so unlike
/// mode coordination there is no commit/revert choreography and no watchdog re-home:
/// abort safety is entirely local (restore in <c>finally</c>).</para>
/// </remarks>
public sealed class TxDelayMinimizer : IAsyncDisposable
{
    private readonly ITuningLink link;
    private readonly ITxDelayMinStation station;
    private readonly TxDelayMinOptions options;
    private readonly Channel<TuningTelegram> inbox = Channel.CreateUnbounded<TuningTelegram>();
    private readonly CancellationTokenSource pumpCts = new();
    private readonly Task pumpLoop;
    private int sequence;

    /// <summary>Create over a link + station pair. The link's and station's lifetimes
    /// stay the caller's.</summary>
    public TxDelayMinimizer(ITuningLink link, ITxDelayMinStation station, TxDelayMinOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(link);
        ArgumentNullException.ThrowIfNull(station);
        this.link = link;
        this.station = station;
        this.options = options ?? new TxDelayMinOptions();
        pumpLoop = Task.Run(() => PumpAsync(pumpCts.Token));
    }

    /// <summary>Diagnostic sink. Null = silent.</summary>
    public Action<string>? Log { get; set; }

    /// <summary>Observer invoked as each sweep step's report lands (drives live UIs /
    /// SSE feeds). Null = none.</summary>
    public Action<TxDelaySweepStep>? StepCompleted { get; set; }

    /// <summary>
    /// Run one full sweep. Returns the sweep table + knee + recommendation; the modem
    /// is left at the sweep's starting TXDELAY with polite channel access restored —
    /// applying the recommendation is the separate, explicit <see cref="ApplyAsync"/>.
    /// </summary>
    public async Task<TxDelaySweepResult> RunSweepAsync(CancellationToken cancellationToken = default)
    {
        if (options.StepMs <= 0 || options.ProbesPerStep <= 0 ||
            options.StartTxDelayMs < options.MinTxDelayMs || options.MinTxDelayMs < 0)
        {
            throw new InvalidOperationException(
                "sweep options must satisfy stepMs > 0, probes > 0, 0 <= minMs <= startMs");
        }
        int k = options.ProbesPerStep;
        var steps = new List<TxDelaySweepStep>();

        TxDelaySweepResult Fail(TxDelaySweepOutcome outcome, string detail, bool restored = false) => new()
        {
            Outcome = outcome,
            Steps = steps,
            Restored = restored,
            RestoredToMs = options.StartTxDelayMs,
            Detail = detail,
        };

        // ── propose (doubles as the handshake) ─────────────────────────────
        Log?.Invoke(string.Create(CultureInfo.InvariantCulture,
            $"txdmin: proposing sweep from {options.StartTxDelayMs} ms down in {options.StepMs} ms steps, {k} probes/step"));
        try
        {
            await SendAsync(
                new TxDelayMinMessage
                {
                    Action = TxDelayMinAction.Propose,
                    Count = k,
                    StartTxDelayMs = options.StartTxDelayMs,
                    StepMs = options.StepMs,
                },
                cancellationToken).ConfigureAwait(false);
        }
        catch (TuningLinkException ex)
        {
            return Fail(TxDelaySweepOutcome.LinkFailed, $"propose undelivered: {ex.Message}");
        }

        var reply = await WaitForAsync(
            m => m.Action is TxDelayMinAction.Confirm or TxDelayMinAction.Reject,
            options.ConfirmTimeout, cancellationToken).ConfigureAwait(false);
        if (reply is null)
        {
            return Fail(TxDelaySweepOutcome.ConfirmTimeout,
                $"no confirm within {options.ConfirmTimeout.TotalSeconds:0}s");
        }
        if (reply.Value.Message.Action == TxDelayMinAction.Reject)
        {
            return Fail(TxDelaySweepOutcome.Rejected, reply.Value.Message.Reason ?? "no reason given");
        }
        Log?.Invoke("txdmin: meter confirmed — pinning channel access and sweeping");

        // ── pinned region: every exit path below restores ──────────────────
        try
        {
            await station.PinChannelAccessAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (TxDelayMinException ex)
        {
            return Fail(TxDelaySweepOutcome.StationFailed, $"channel-access pin failed: {ex.Message}");
        }

        bool restored = false;
        try
        {
            for (int ms = options.StartTxDelayMs; ms >= options.MinTxDelayMs; ms -= options.StepMs)
            {
                var step = await RunProbePassAsync(TxDelayMinAction.Step, ms, k, cancellationToken)
                    .ConfigureAwait(false);
                steps.Add(step);
                StepCompleted?.Invoke(step);
                Log?.Invoke(string.Create(CultureInfo.InvariantCulture,
                    $"txdmin: {ms} ms → {step.Decoded}/{step.Probes} decoded" +
                    $"{(step.MedianPreDataCarrierMs is { } pre ? $", heard pre-data ~{pre:0} ms" : string.Empty)}"));
                if (step.Decoded < k)
                {
                    break; // the drop step — the knee is the step above it
                }
            }
        }
        catch (TuningLinkException ex)
        {
            await TrySendAbortAsync("linkfail", cancellationToken).ConfigureAwait(false);
            restored = await RestoreAsync().ConfigureAwait(false);
            return Fail(TxDelaySweepOutcome.LinkFailed, ex.Message, restored);
        }
        catch (TxDelayMinException ex)
        {
            await TrySendAbortAsync("stnfail", cancellationToken).ConfigureAwait(false);
            restored = await RestoreAsync().ConfigureAwait(false);
            return Fail(TxDelaySweepOutcome.StationFailed, ex.Message, restored);
        }
        catch (OperationCanceledException)
        {
            await TrySendAbortAsync("cancel", CancellationToken.None).ConfigureAwait(false);
            restored = await RestoreAsync().ConfigureAwait(false);
            throw;
        }

        restored = await RestoreAsync().ConfigureAwait(false);

        // ── verdict ────────────────────────────────────────────────────────
        if (steps[0].Decoded < k)
        {
            return Fail(TxDelaySweepOutcome.NotSolidAtStart,
                string.Create(CultureInfo.InvariantCulture,
                    $"only {steps[0].Decoded}/{k} decoded at the starting {steps[0].TxDelayMs} ms"),
                restored);
        }

        bool floorReached = steps[^1].Decoded == k;
        int knee = floorReached ? steps[^1].TxDelayMs : steps[^2].TxDelayMs;
        int recommended = TxDelayMinReport.Recommend(knee, options);
        Log?.Invoke(string.Create(CultureInfo.InvariantCulture,
            $"txdmin: knee {knee} ms{(floorReached ? " (floor — true knee may be lower)" : string.Empty)}, " +
            $"recommend {recommended} ms"));
        return new TxDelaySweepResult
        {
            Outcome = TxDelaySweepOutcome.Complete,
            Steps = steps,
            KneeMs = knee,
            RecommendedMs = recommended,
            FloorReached = floorReached,
            Restored = restored,
            RestoredToMs = options.StartTxDelayMs,
        };
    }

    /// <summary>
    /// The explicit APPLY: set <paramref name="txDelayMs"/>, settle, verify with
    /// <see cref="TxDelayMinOptions.ProbesPerStep"/> more separately-keyed probes
    /// counted by the meter, and — only when every probe decoded — LEAVE the modem at
    /// the applied value (persisting it into configuration is the caller's step, e.g.
    /// the port's KISS-params config on the node). A failed verify restores the sweep's
    /// starting TXDELAY. Channel access is pinned for the verify and restored either
    /// way. The meter loop must still be running.
    /// </summary>
    public async Task<TxDelayApplyResult> ApplyAsync(int txDelayMs, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(txDelayMs);
        int k = options.ProbesPerStep;
        await station.PinChannelAccessAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            TxDelaySweepStep verify;
            try
            {
                verify = await RunProbePassAsync(TxDelayMinAction.Apply, txDelayMs, k, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is TuningLinkException or TxDelayMinException)
            {
                await TrySendAbortAsync("applyfail", CancellationToken.None).ConfigureAwait(false);
                await TryRestoreTxDelayAsync().ConfigureAwait(false);
                return new TxDelayApplyResult(txDelayMs, 0, k, null, Verified: false,
                    $"verify could not run ({ex.Message}) — TXDELAY restored to {options.StartTxDelayMs} ms");
            }

            if (verify.Decoded == k)
            {
                Log?.Invoke(string.Create(CultureInfo.InvariantCulture,
                    $"txdmin: APPLY verified — {verify.Decoded}/{k} at {txDelayMs} ms; modem left at {txDelayMs} ms"));
                return new TxDelayApplyResult(
                    txDelayMs, verify.Decoded, k, verify.MedianPreDataCarrierMs, Verified: true);
            }

            Log?.Invoke(string.Create(CultureInfo.InvariantCulture,
                $"txdmin: APPLY NOT VERIFIED — {verify.Decoded}/{k} at {txDelayMs} ms; restoring {options.StartTxDelayMs} ms"));
            await TryRestoreTxDelayAsync().ConfigureAwait(false);
            return new TxDelayApplyResult(
                txDelayMs, verify.Decoded, k, verify.MedianPreDataCarrierMs, Verified: false,
                $"only {verify.Decoded}/{k} decoded — TXDELAY restored to {options.StartTxDelayMs} ms");
        }
        finally
        {
            try
            {
                await station.RestoreChannelAccessAsync(CancellationToken.None).ConfigureAwait(false);
            }
            catch (TxDelayMinException ex)
            {
                Log?.Invoke($"txdmin: channel-access restore failed ({ex.Message}) — check the rig");
            }
        }
    }

    /// <summary>Best-effort <c>done</c> — the meter exits its loop. Carries the
    /// recommendation when one exists (for the meter operator's log).</summary>
    public async Task EndAsync(int? recommendedMs = null, CancellationToken cancellationToken = default)
    {
        try
        {
            await SendAsync(
                new TxDelayMinMessage { Action = TxDelayMinAction.Done, RecommendedTxDelayMs = recommendedMs },
                cancellationToken).ConfigureAwait(false);
        }
        catch (TuningLinkException ex)
        {
            Log?.Invoke($"txdmin: done undelivered ({ex.Message}) — the meter's idle timeout covers it");
        }
    }

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        await pumpCts.CancelAsync().ConfigureAwait(false);
        try
        {
            await pumpLoop.ConfigureAwait(false);
        }
        catch
        {
        }
        pumpCts.Dispose();
    }

    /// <summary>One announce→set→probe→report pass, shared by the sweep steps and the
    /// APPLY verify. The announce telegram's sequence number is the probe tag.</summary>
    private async Task<TxDelaySweepStep> RunProbePassAsync(
        TxDelayMinAction announce, int txDelayMs, int k, CancellationToken cancellationToken)
    {
        int tag = NextSequence();
        await link.SendAsync(
            new TxDelayMinMessage { Action = announce, TxDelayMs = txDelayMs, Count = k }.ToTelegram(tag),
            cancellationToken).ConfigureAwait(false);

        // Wedge guard: the meter radio's auto-ack of the announce is in flight — the
        // settle frame the TXDELAY set transmits must not race our own radio's RX ack.
        await Task.Delay(options.PreKeyDelay, cancellationToken).ConfigureAwait(false);

        // Set + settle FIRST (the settle keying still pays the OLD preamble; it carries
        // no probe marker so the meter never counts it), then the tagged probes.
        await station.SetTxDelayAsync(txDelayMs, cancellationToken).ConfigureAwait(false);
        var tx = await station.TransmitProbesAsync(tag, k, cancellationToken).ConfigureAwait(false);

        await SendAsync(
            new TxDelayMinMessage { Action = TxDelayMinAction.ProbesSent, TxDelayMs = txDelayMs, Count = k },
            cancellationToken).ConfigureAwait(false);

        var report = await WaitForAsync(
            m => m.Action == TxDelayMinAction.StepReport && m.TxDelayMs == txDelayMs,
            options.ReportTimeout, cancellationToken).ConfigureAwait(false);
        if (report is null)
        {
            throw new TuningLinkException(
                string.Create(CultureInfo.InvariantCulture,
                    $"no report for the {txDelayMs} ms step within {options.ReportTimeout.TotalSeconds:0}s"));
        }
        var m = report.Value.Message;
        return new TxDelaySweepStep(
            txDelayMs,
            Math.Min(m.Decoded ?? 0, k),
            k,
            tx.MeanTxMs,
            m.MedianPreDataMs);
    }

    /// <summary>Restore the original TXDELAY (set + settle) and polite channel access.
    /// Never throws; returns whether both restores took.</summary>
    private async Task<bool> RestoreAsync()
    {
        bool ok = await TryRestoreTxDelayAsync().ConfigureAwait(false);
        try
        {
            await station.RestoreChannelAccessAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (TxDelayMinException ex)
        {
            Log?.Invoke($"txdmin: channel-access restore failed ({ex.Message}) — check the rig");
            ok = false;
        }
        return ok;
    }

    private async Task<bool> TryRestoreTxDelayAsync()
    {
        try
        {
            await station.SetTxDelayAsync(options.StartTxDelayMs, CancellationToken.None).ConfigureAwait(false);
            return true;
        }
        catch (TxDelayMinException ex)
        {
            Log?.Invoke($"txdmin: TXDELAY restore failed ({ex.Message}) — check the rig");
            return false;
        }
    }

    private async Task TrySendAbortAsync(string reason, CancellationToken cancellationToken)
    {
        try
        {
            await SendAsync(
                new TxDelayMinMessage { Action = TxDelayMinAction.Abort, Reason = reason },
                cancellationToken).ConfigureAwait(false);
        }
        catch (TuningLinkException ex)
        {
            Log?.Invoke($"txdmin: abort notification undelivered ({ex.Message}) — the meter's idle timeout covers it");
        }
    }

    private Task SendAsync(TxDelayMinMessage message, CancellationToken cancellationToken) =>
        link.SendAsync(message.ToTelegram(NextSequence()), cancellationToken);

    private async Task<(int Sequence, TxDelayMinMessage Message)?> WaitForAsync(
        Func<TxDelayMinMessage, bool> match, TimeSpan timeout, CancellationToken cancellationToken)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(timeout);
        try
        {
            while (true)
            {
                var telegram = await inbox.Reader.ReadAsync(timeoutCts.Token).ConfigureAwait(false);
                if (TxDelayMinMessage.TryFromTelegram(telegram, out var message) && message is not null && match(message))
                {
                    return (telegram.Sequence, message);
                }
                Log?.Invoke($"txdmin: ignoring out-of-phase telegram {telegram}");
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return null;
        }
        catch (ChannelClosedException)
        {
            return null;
        }
    }

    private async Task PumpAsync(CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var telegram in link.ReceiveAsync(cancellationToken).ConfigureAwait(false))
            {
                inbox.Writer.TryWrite(telegram);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        finally
        {
            inbox.Writer.TryComplete();
        }
    }

    private int NextSequence() => Interlocked.Increment(ref sequence);
}
