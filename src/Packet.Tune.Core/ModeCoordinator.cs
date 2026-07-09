using System.Globalization;
using System.Threading.Channels;
using Packet.Kiss.NinoTnc;

namespace Packet.Tune.Core;

/// <summary>
/// The driving end of the mode-coordination protocol: propose → confirm → commit a
/// TNC-mode (and optionally radio-channel) switch over an <see cref="ITuningLink"/>
/// whose transport is mode/channel-agnostic (canonically <see cref="SdmTuningLink"/>
/// over the radios' own FFSK side channel), then verify the switched link with tagged
/// probe frames in both directions. On any failure after the commit, both ends revert
/// to the session's home mode/channel — coordinated over the side channel when it
/// still reaches the peer, and backstopped by the responder's idle watchdog when it
/// does not (e.g. a split channel).
/// </summary>
/// <remarks>
/// Choreography of one attempt (all <c>MODE</c> telegrams, seq-numbered and deduped by
/// the link):
/// <list type="number">
///   <item>C→R <c>propose|mode[|channel]</c>; responder answers <c>confirm</c> (or
///     <c>reject</c> — nothing has changed anywhere yet).</item>
///   <item>C→R <c>commit|mode[|channel]</c>. The commit telegram's sequence number is
///     the attempt tag carried by every probe frame. The responder switches on
///     receipt (after its auto-ack guard); the coordinator switches once the link
///     confirms delivery — so nobody moves until both ends hold the commit.</item>
///   <item>Coordinator probes C→R (<see cref="ModeCoordOptions.ProbeFrames"/> tagged
///     frames), announces <c>sent|n</c>; responder reports <c>report|rx/n</c>, then
///     probes R→C and announces its own <c>sent</c>; coordinator snapshots its
///     counter and sends the final <c>report</c> back.</item>
///   <item>Both directions decoded ≥ 1 frame → the switch stands. Otherwise
///     <c>revert</c> goes out, both ends return home, and the home link is verified
///     with a short probe burst (which always works — home is the mode the session
///     started on).</item>
/// </list>
/// </remarks>
public sealed class ModeCoordinator : IAsyncDisposable
{
    private readonly ITuningLink link;
    private readonly IModeCoordStation station;
    private readonly ModeCoordOptions options;
    private readonly Channel<TuningTelegram> inbox = Channel.CreateUnbounded<TuningTelegram>();
    private readonly CancellationTokenSource pumpCts = new();
    private readonly Task pumpLoop;
    private int sequence = TuningTelegram.NewSessionSequenceBase();

    /// <summary>Create over a link + station pair. The link's and station's lifetimes
    /// stay the caller's.</summary>
    public ModeCoordinator(ITuningLink link, IModeCoordStation station, ModeCoordOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(link);
        ArgumentNullException.ThrowIfNull(station);
        this.link = link;
        this.station = station;
        this.options = options ?? new ModeCoordOptions();
        CurrentMode = this.options.HomeMode;
        CurrentChannel = this.options.HomeChannel;
        pumpLoop = Task.Run(() => PumpAsync(pumpCts.Token));
    }

    /// <summary>Diagnostic sink. Null = silent.</summary>
    public Action<string>? Log { get; set; }

    /// <summary>The mode this end believes both TNCs are running.</summary>
    public byte CurrentMode { get; private set; }

    /// <summary>The channel this end believes both radios are on.</summary>
    public int CurrentChannel { get; private set; }

    /// <summary>
    /// Session handshake: <c>HI|coord</c>, answered by the responder's
    /// <c>HI|responder</c>. Confirms the peer <em>application</em> is alive — a
    /// delivery receipt alone only proves the peer radio is on.
    /// </summary>
    /// <returns><c>true</c> when the responder answered.</returns>
    public async Task<bool> HelloAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            await link.SendAsync(
                new TuningTelegram(NextSequence(), TuningVerb.Hello, ModeResponder.CoordinatorRole),
                cancellationToken).ConfigureAwait(false);
        }
        catch (TuningLinkException ex)
        {
            Log?.Invoke($"coord: hello undelivered ({ex.Message})");
            return false;
        }
        var reply = await WaitAsync(
            t => t.Verb == TuningVerb.Hello && t.Args == ModeResponder.ResponderRole,
            options.ConfirmTimeout, cancellationToken).ConfigureAwait(false);
        return reply is not null;
    }

    /// <summary>
    /// Run one full coordination attempt: switch both ends to
    /// <paramref name="mode"/> (and <paramref name="channel"/> when given and
    /// different from the current one), verify with probes both ways, and revert both
    /// ends to home on any failure past the commit.
    /// </summary>
    public async Task<ModeCoordAttempt> CoordinateAsync(
        byte mode, int? channel = null, CancellationToken cancellationToken = default)
    {
        int? wireChannel = channel is { } c && c != CurrentChannel ? c : null;
        int channelInEffect = wireChannel ?? CurrentChannel;
        string modeName = NinoTncCatalog.TryGetByMode(mode)?.Name
            ?? string.Create(CultureInfo.InvariantCulture, $"mode {mode}");
        ModeCoordAttempt Fail(ModeCoordOutcome outcome, string detail, bool reverted = false, bool? homeAlive = null,
            ModeProbeCell? cToR = null, ModeProbeCell? rToC = null) =>
            new()
            {
                Mode = mode,
                ModeName = modeName,
                Channel = wireChannel,
                ChannelInEffect = channelInEffect,
                Outcome = outcome,
                Detail = detail,
                Reverted = reverted,
                HomeLinkAlive = homeAlive,
                CoordinatorToResponder = cToR,
                ResponderToCoordinator = rToC,
            };

        // ── propose (reply-driven: the SDM receipt is unreliable for this close bidirectional
        //    traffic, so recover a lost propose/confirm by re-proposing with a fresh sequence;
        //    nothing is committed yet, and the responder re-confirms idempotently) ───────────
        Log?.Invoke($"coord: proposing mode {mode} ({modeName})" +
                    (wireChannel is { } wc0 ? $" on channel {wc0}" : string.Empty));
        (int Sequence, ModeCoordMessage Message)? reply = null;
        for (int attempt = 1; attempt <= options.LinkRetryAttempts; attempt++)
        {
            try
            {
                await SendModeAsync(
                    new ModeCoordMessage { Action = ModeCoordAction.Propose, Mode = mode, Channel = wireChannel },
                    cancellationToken).ConfigureAwait(false);
            }
            catch (TuningLinkException ex)
            {
                if (attempt >= options.LinkRetryAttempts)
                {
                    return Fail(ModeCoordOutcome.LinkFailed, $"propose undelivered: {ex.Message}");
                }
                Log?.Invoke($"coord: propose send rejected ({ex.Message}) — retrying {attempt + 1}/{options.LinkRetryAttempts}");
                continue;
            }

            reply = await WaitForModeAsync(
                m => (m.Action is ModeCoordAction.Confirm or ModeCoordAction.Reject) && m.Mode == mode,
                options.ConfirmTimeout, cancellationToken).ConfigureAwait(false);
            if (reply is not null)
            {
                break;
            }
            if (attempt < options.LinkRetryAttempts)
            {
                Log?.Invoke($"coord: no confirm within {options.ConfirmTimeout.TotalSeconds:0}s — re-proposing {attempt + 1}/{options.LinkRetryAttempts}");
            }
        }
        if (reply is null)
        {
            return Fail(ModeCoordOutcome.ConfirmTimeout,
                $"no confirm within {options.ConfirmTimeout.TotalSeconds:0}s after {options.LinkRetryAttempts} attempts");
        }
        if (reply.Value.Message.Action == ModeCoordAction.Reject)
        {
            return Fail(ModeCoordOutcome.Rejected, reply.Value.Message.Reason ?? "no reason given");
        }
        Log?.Invoke("coord: responder confirmed — committing");

        // ── commit (its seq = the attempt tag) ─────────────────────────────
        int attemptTag = NextSequence();
        try
        {
            await link.SendAsync(
                new ModeCoordMessage { Action = ModeCoordAction.Commit, Mode = mode, Channel = wireChannel }
                    .ToTelegram(attemptTag),
                cancellationToken).ConfigureAwait(false);
        }
        catch (TuningLinkException ex)
        {
            // Ambiguous: the responder may hold the commit with only the receipt
            // lost. Stay home, tell it to revert (best effort — its watchdog is
            // the backstop).
            Log?.Invoke($"coord: commit delivery unconfirmed ({ex.Message}) — staying home, sending revert");
            await TrySendRevertAsync("nocommit", cancellationToken).ConfigureAwait(false);
            return Fail(ModeCoordOutcome.CommitUndelivered,
                "commit delivery unconfirmed — stayed home, revert sent (responder watchdog backstops)");
        }

        // ── both ends switch ───────────────────────────────────────────────
        try
        {
            await station.ApplyModeAsync(mode, cancellationToken).ConfigureAwait(false);
            if (wireChannel is { } wc)
            {
                await station.ApplyChannelAsync(wc, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (ModeCoordException ex)
        {
            Log?.Invoke($"coord: local switch failed ({ex.Message}) — reverting both ends");
            bool? alive = await RevertBothAsync("swfail", cancellationToken).ConfigureAwait(false);
            return Fail(ModeCoordOutcome.SwitchFailed, ex.Message, reverted: true, homeAlive: alive);
        }
        CurrentMode = mode;
        if (wireChannel is { } wcNow)
        {
            CurrentChannel = wcNow;
        }

        // ── probe both directions ──────────────────────────────────────────
        ModeProbeCell? cToR = null;
        ModeProbeCell? rToC = null;
        try
        {
            await Task.Delay(options.SwitchSettle, cancellationToken).ConfigureAwait(false);
            using var counter = station.BeginProbeCount(attemptTag);

            Log?.Invoke($"coord: transmitting {options.ProbeFrames} probe frames (tag a{attemptTag})");
            var tx = await station.TransmitProbesAsync(attemptTag, options.ProbeFrames, cancellationToken)
                .ConfigureAwait(false);
            await SendModeAsync(
                new ModeCoordMessage
                {
                    Action = ModeCoordAction.ProbesSent,
                    Count = options.ProbeFrames,
                    MeanTxMs = tx.MeanTxMs is { } mean ? (int)Math.Round(mean) : null,
                },
                cancellationToken).ConfigureAwait(false);

            var report = await WaitForModeAsync(
                m => m.Action is ModeCoordAction.ProbeReport or ModeCoordAction.Revert,
                options.ReportTimeout, cancellationToken).ConfigureAwait(false);
            if (report is null || report.Value.Message.Action == ModeCoordAction.Revert)
            {
                string why = report is null ? "no probe report from responder" : "responder reverted";
                bool? alive = await RevertBothAsync("probelost", cancellationToken,
                    notifyPeer: report is null).ConfigureAwait(false);
                return Fail(ModeCoordOutcome.LinkFailed, why, reverted: true, homeAlive: alive);
            }
            cToR = new ModeProbeCell(
                report.Value.Message.Decoded ?? 0, options.ProbeFrames, tx.MeanTxMs);
            Log?.Invoke($"coord: C→R {cToR.Decoded}/{cToR.Attempts} decoded at the responder");

            var peerSent = await WaitForModeAsync(
                m => m.Action is ModeCoordAction.ProbesSent or ModeCoordAction.Revert,
                options.PeerProbeTimeout, cancellationToken).ConfigureAwait(false);
            if (peerSent is null || peerSent.Value.Message.Action == ModeCoordAction.Revert)
            {
                string why = peerSent is null ? "responder's probes never announced" : "responder reverted";
                bool? alive = await RevertBothAsync("probelost", cancellationToken,
                    notifyPeer: peerSent is null).ConfigureAwait(false);
                return Fail(ModeCoordOutcome.LinkFailed, why, reverted: true, homeAlive: alive,
                    cToR: cToR);
            }
            await Task.Delay(options.ArrivalGrace, cancellationToken).ConfigureAwait(false);
            int peerCount = peerSent.Value.Message.Count ?? options.ProbeFrames;
            rToC = new ModeProbeCell(counter.Count, peerCount,
                peerSent.Value.Message.MeanTxMs);
            Log?.Invoke($"coord: R→C {rToC.Decoded}/{rToC.Attempts} decoded here");

            // Close the loop: the responder learns the R→C outcome and that the
            // attempt concluded.
            await SendModeAsync(
                new ModeCoordMessage
                {
                    Action = ModeCoordAction.ProbeReport,
                    Decoded = rToC.Decoded,
                    Count = peerCount,
                },
                cancellationToken).ConfigureAwait(false);
        }
        catch (TuningLinkException ex)
        {
            Log?.Invoke($"coord: coordination lost mid-probe ({ex.Message}) — reverting both ends");
            bool? alive = await RevertBothAsync("linkfail", cancellationToken).ConfigureAwait(false);
            return Fail(ModeCoordOutcome.LinkFailed, ex.Message, reverted: true, homeAlive: alive,
                cToR: cToR, rToC: rToC);
        }

        // ── verdict ────────────────────────────────────────────────────────
        if (cToR.Decoded == 0 || rToC.Decoded == 0)
        {
            Log?.Invoke("coord: probe verdict DEAD in at least one direction — reverting both ends");
            bool? alive = await RevertBothAsync("probedead", cancellationToken).ConfigureAwait(false);
            return Fail(ModeCoordOutcome.ProbeDead,
                $"C→R {cToR.Decoded}/{cToR.Attempts}, R→C {rToC.Decoded}/{rToC.Attempts}",
                reverted: true, homeAlive: alive, cToR: cToR, rToC: rToC);
        }

        Log?.Invoke($"coord: mode {mode} verified — link stands");
        return new ModeCoordAttempt
        {
            Mode = mode,
            ModeName = modeName,
            Channel = wireChannel,
            ChannelInEffect = channelInEffect,
            Outcome = ModeCoordOutcome.Switched,
            CoordinatorToResponder = cToR,
            ResponderToCoordinator = rToC,
        };
    }

    /// <summary>
    /// <see cref="CoordinateAsync"/> with reply-driven retry across the commit/probe phase: a
    /// transient side-channel loss there reverts both ends safely to home (the SDM receipt is
    /// unreliable — see <see cref="SdmTuningLink"/>), and the commit is a state change rather than
    /// a re-runnable telegram, so the revert-safe unit to retry is the whole attempt, re-run from a
    /// known home state. Only outcomes that left both ends confirmed-home retry —
    /// <see cref="ModeCoordOutcome.CommitUndelivered"/> (never switched) and
    /// <see cref="ModeCoordOutcome.LinkFailed"/> with the home link verified alive; real verdicts
    /// (rejected / switch-failed / probe-dead) and the pre-commit confirm timeout (already retried
    /// inside the handshake) return as-is. Up to <see cref="ModeCoordOptions.CommitRetryAttempts"/>.
    /// </summary>
    public async Task<ModeCoordAttempt> CoordinateWithRetryAsync(
        byte mode, int? channel = null, CancellationToken cancellationToken = default)
    {
        for (int attemptNo = 1; ; attemptNo++)
        {
            var attempt = await CoordinateAsync(mode, channel, cancellationToken).ConfigureAwait(false);

            bool recoverableAndHome =
                attempt.Outcome == ModeCoordOutcome.CommitUndelivered             // never switched — still home
                || (attempt.Outcome == ModeCoordOutcome.LinkFailed && attempt.HomeLinkAlive == true); // reverted, home confirmed alive
            if (!recoverableAndHome || attemptNo >= options.CommitRetryAttempts)
            {
                return attempt;
            }
            Log?.Invoke(
                $"coord: attempt {attemptNo} {attempt.Outcome} (both ends home) — re-running {attemptNo + 1}/{options.CommitRetryAttempts}");
        }
    }

    /// <summary>Coordinate a switch back to the session's home mode/channel (a normal
    /// propose/confirm/commit attempt — the raw revert path is only for failures).</summary>
    public Task<ModeCoordAttempt> ReturnHomeAsync(CancellationToken cancellationToken = default) =>
        CoordinateAsync(options.HomeMode, options.HomeChannel, cancellationToken);

    /// <summary>Best-effort <c>BY</c> — the responder exits its loop (reverting to
    /// home first if it is not there).</summary>
    public async Task EndAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            await link.SendAsync(new TuningTelegram(NextSequence(), TuningVerb.Bye, string.Empty), cancellationToken)
                .ConfigureAwait(false);
        }
        catch (TuningLinkException ex)
        {
            Log?.Invoke($"coord: BY undelivered ({ex.Message})");
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

    /// <summary>
    /// Revert both ends to home: tell the peer (best effort), apply home locally, and —
    /// when the peer was reachable — verify the home link with a short one-way probe
    /// burst. Returns whether the home link verified (<c>null</c> = unverifiable).
    /// </summary>
    private async Task<bool?> RevertBothAsync(
        string reason, CancellationToken cancellationToken, bool notifyPeer = true)
    {
        int revertTag = 0;
        bool notified = false;
        if (notifyPeer)
        {
            revertTag = await TrySendRevertAsync(reason, cancellationToken).ConfigureAwait(false) ?? 0;
            notified = revertTag != 0;
        }

        try
        {
            await station.ApplyModeAsync(options.HomeMode, cancellationToken).ConfigureAwait(false);
            if (CurrentChannel != options.HomeChannel)
            {
                await station.ApplyChannelAsync(options.HomeChannel, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (ModeCoordException ex)
        {
            Log?.Invoke($"coord: REVERT FAILED locally ({ex.Message}) — check the rig by hand");
            return false;
        }
        CurrentMode = options.HomeMode;
        CurrentChannel = options.HomeChannel;
        Log?.Invoke($"coord: reverted to home (mode {options.HomeMode}, channel {options.HomeChannel})");

        if (!notified)
        {
            // The peer could not be told (or we chose not to — it reverted first);
            // its watchdog brings it home. Nothing to verify against yet.
            return null;
        }

        // Home verify: one-way probe burst tagged with the revert telegram's seq.
        try
        {
            await Task.Delay(options.SwitchSettle, cancellationToken).ConfigureAwait(false);
            var tx = await station.TransmitProbesAsync(revertTag, options.HomeVerifyProbeFrames, cancellationToken)
                .ConfigureAwait(false);
            await SendModeAsync(
                new ModeCoordMessage
                {
                    Action = ModeCoordAction.ProbesSent,
                    Count = options.HomeVerifyProbeFrames,
                    MeanTxMs = tx.MeanTxMs is { } mean ? (int)Math.Round(mean) : null,
                },
                cancellationToken).ConfigureAwait(false);
            var report = await WaitForModeAsync(
                m => m.Action == ModeCoordAction.ProbeReport,
                options.ReportTimeout, cancellationToken).ConfigureAwait(false);
            bool alive = report is not null && (report.Value.Message.Decoded ?? 0) > 0;
            Log?.Invoke(alive
                ? $"coord: home link alive ({report!.Value.Message.Decoded}/{options.HomeVerifyProbeFrames} decoded at the responder)"
                : "coord: home link NOT confirmed — check the rig");
            return alive;
        }
        catch (TuningLinkException ex)
        {
            Log?.Invoke($"coord: home verify could not run ({ex.Message})");
            return null;
        }
    }

    private async Task<int?> TrySendRevertAsync(string reason, CancellationToken cancellationToken)
    {
        int seq = NextSequence();
        try
        {
            await link.SendAsync(
                new ModeCoordMessage { Action = ModeCoordAction.Revert, Reason = reason }.ToTelegram(seq),
                cancellationToken).ConfigureAwait(false);
            return seq;
        }
        catch (TuningLinkException ex)
        {
            Log?.Invoke($"coord: revert notification undelivered ({ex.Message}) — responder watchdog backstops");
            return null;
        }
    }

    private Task SendModeAsync(ModeCoordMessage message, CancellationToken cancellationToken) =>
        link.SendAsync(message.ToTelegram(NextSequence()), cancellationToken);

    private async Task<(int Sequence, ModeCoordMessage Message)?> WaitForModeAsync(
        Func<ModeCoordMessage, bool> match, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var telegram = await WaitAsync(
            t => ModeCoordMessage.TryFromTelegram(t, out var m) && m is not null && match(m),
            timeout, cancellationToken).ConfigureAwait(false);
        if (telegram is null)
        {
            return null;
        }
        ModeCoordMessage.TryFromTelegram(telegram, out var message);
        return (telegram.Sequence, message!);
    }

    private async Task<TuningTelegram?> WaitAsync(
        Func<TuningTelegram, bool> match, TimeSpan timeout, CancellationToken cancellationToken)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(timeout);
        try
        {
            while (true)
            {
                var telegram = await inbox.Reader.ReadAsync(timeoutCts.Token).ConfigureAwait(false);
                if (match(telegram))
                {
                    return telegram;
                }
                Log?.Invoke($"coord: ignoring out-of-phase telegram {telegram}");
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
