using System.Globalization;
using System.Threading.Channels;
using Packet.Kiss.NinoTnc;

namespace Packet.Tune.Core;

/// <summary>
/// The answering end of the mode-coordination protocol (see
/// <see cref="ModeCoordinator"/> for the choreography). Confirms proposals it can
/// honour, switches on commit, counts/fires the verification probes, and — the part
/// that keeps the rig safe — <b>always finds its way back to the session's home
/// mode/channel</b>: on a <c>revert</c> telegram, on a failed local switch, on
/// <c>BY</c>, on cancellation, and via an idle watchdog when the coordinator goes
/// silent while this end is away from home (the side channel is mode-agnostic, but a
/// failed <em>channel</em> switch can split the radios — the watchdog is the recovery
/// for exactly that).
/// </summary>
public sealed class ModeResponder
{
    /// <summary>Wire token the coordinator's <c>HI</c> carries.</summary>
    public const string CoordinatorRole = "coord";

    /// <summary>Wire token this end answers <c>HI</c> with.</summary>
    public const string ResponderRole = "responder";

    private readonly ITuningLink link;
    private readonly IModeCoordStation station;
    private readonly ModeCoordOptions options;
    private int sequence;

    // Session state.
    private (int Tag, byte Mode, int? Channel)? pendingProposal;
    private IModeProbeCounter? counter;
    private int activeTag;
    private bool homeVerifyOnly;
    private byte currentMode;
    private int currentChannel;
    private DateTimeOffset lastHeard = DateTimeOffset.UtcNow;

    /// <summary>Create over a link + station pair. The link's and station's lifetimes
    /// stay the caller's.</summary>
    public ModeResponder(ITuningLink link, IModeCoordStation station, ModeCoordOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(link);
        ArgumentNullException.ThrowIfNull(station);
        this.link = link;
        this.station = station;
        this.options = options ?? new ModeCoordOptions();
        currentMode = this.options.HomeMode;
        currentChannel = this.options.HomeChannel;
    }

    /// <summary>Diagnostic sink. Null = silent.</summary>
    public Action<string>? Log { get; set; }

    /// <summary>The mode this end is currently running (for status display).</summary>
    public byte CurrentMode => currentMode;

    /// <summary>
    /// Run the responder loop until <c>BY</c> (returns 0) or the link closes
    /// (returns 1). Cancellation reverts to home before returning 0 — the rig is
    /// never abandoned off-home by this end if it can help it.
    /// </summary>
    public async Task<int> RunAsync(CancellationToken cancellationToken = default)
    {
        var inbox = Channel.CreateUnbounded<TuningTelegram>();
        using var pumpCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var pump = Task.Run(
            async () =>
            {
                try
                {
                    await foreach (var telegram in link.ReceiveAsync(pumpCts.Token).ConfigureAwait(false))
                    {
                        inbox.Writer.TryWrite(telegram);
                    }
                }
                catch (OperationCanceledException) when (pumpCts.IsCancellationRequested)
                {
                }
                finally
                {
                    inbox.Writer.TryComplete();
                }
            },
            CancellationToken.None);

        Log?.Invoke("responder: ready — waiting for the coordinator");
        try
        {
            while (true)
            {
                TuningTelegram telegram;
                using (var readCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
                {
                    readCts.CancelAfter(WatchdogPollInterval());
                    try
                    {
                        telegram = await inbox.Reader.ReadAsync(readCts.Token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                    {
                        await CheckWatchdogAsync(cancellationToken).ConfigureAwait(false);
                        continue;
                    }
                    catch (ChannelClosedException)
                    {
                        Log?.Invoke("responder: link closed without BY");
                        await EnsureHomeAsync(cancellationToken).ConfigureAwait(false);
                        return 1;
                    }
                }

                lastHeard = DateTimeOffset.UtcNow;
                if (telegram.Verb == TuningVerb.Bye)
                {
                    Log?.Invoke("responder: coordinator said BY — session over");
                    await EnsureHomeAsync(cancellationToken).ConfigureAwait(false);
                    return 0;
                }
                if (telegram.Verb == TuningVerb.Hello && telegram.Args == CoordinatorRole)
                {
                    Log?.Invoke("responder: coordinator hello — answering");
                    await TrySendAsync(
                        new TuningTelegram(NextSequence(), TuningVerb.Hello, ResponderRole),
                        cancellationToken).ConfigureAwait(false);
                    continue;
                }
                if (!ModeCoordMessage.TryFromTelegram(telegram, out var message) || message is null)
                {
                    continue; // not ours (deviation-tuning verbs, unknown actions…)
                }
                await HandleAsync(telegram.Sequence, message, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            using var homeCts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            await EnsureHomeAsync(homeCts.Token).ConfigureAwait(false);
            return 0;
        }
        finally
        {
            counter?.Dispose();
            counter = null;
            await pumpCts.CancelAsync().ConfigureAwait(false);
            try
            {
                await pump.ConfigureAwait(false);
            }
            catch
            {
            }
        }
    }

    private async Task HandleAsync(int telegramSequence, ModeCoordMessage message, CancellationToken cancellationToken)
    {
        switch (message.Action)
        {
            case ModeCoordAction.Propose:
            {
                byte mode = message.Mode!.Value;
                if (NinoTncCatalog.TryGetByMode(mode) is not { } catalogMode || mode == 15)
                {
                    Log?.Invoke($"responder: rejecting proposal for unknown mode {mode}");
                    await TrySendModeAsync(
                        new ModeCoordMessage { Action = ModeCoordAction.Reject, Mode = mode, Reason = "unkmode" },
                        cancellationToken).ConfigureAwait(false);
                    return;
                }
                pendingProposal = (telegramSequence, mode, message.Channel);
                Log?.Invoke($"responder: confirming mode {mode} ({catalogMode.Name})" +
                            (message.Channel is { } pch ? $" on channel {pch}" : string.Empty));
                await TrySendModeAsync(
                    new ModeCoordMessage { Action = ModeCoordAction.Confirm, Mode = mode, Channel = message.Channel },
                    cancellationToken).ConfigureAwait(false);
                return;
            }

            case ModeCoordAction.Commit:
            {
                if (pendingProposal is not { } pending || pending.Mode != message.Mode)
                {
                    Log?.Invoke($"responder: ignoring commit for mode {message.Mode} without a matching proposal");
                    return;
                }
                pendingProposal = null;
                // The attempt tag every probe frame carries is the COMMIT
                // telegram's sequence number.
                int attemptTag = telegramSequence;
                counter?.Dispose();
                counter = station.BeginProbeCount(attemptTag);
                activeTag = attemptTag;
                homeVerifyOnly = false;

                // Wedge guard: our radio's auto-ack of this very telegram is in
                // flight — the settle frame the mode apply transmits must not
                // race it.
                await Task.Delay(options.PreProbeDelay, cancellationToken).ConfigureAwait(false);
                try
                {
                    await station.ApplyModeAsync(message.Mode!.Value, cancellationToken).ConfigureAwait(false);
                    if (message.Channel is { } channel)
                    {
                        await station.ApplyChannelAsync(channel, cancellationToken).ConfigureAwait(false);
                    }
                }
                catch (ModeCoordException ex)
                {
                    Log?.Invoke($"responder: switch failed ({ex.Message}) — reverting to home");
                    counter.Dispose();
                    counter = null;
                    await SelfRevertAsync("swfail", cancellationToken).ConfigureAwait(false);
                    return;
                }
                currentMode = message.Mode!.Value;
                if (message.Channel is { } chNow)
                {
                    currentChannel = chNow;
                }
                Log?.Invoke($"responder: switched to mode {currentMode}" +
                            (message.Channel is { } c2 ? $", channel {c2}" : string.Empty) +
                            $" — counting probes tagged a{attemptTag}");
                return;
            }

            case ModeCoordAction.ProbesSent:
            {
                if (counter is null)
                {
                    Log?.Invoke("responder: 'sent' with no probe window open — ignoring");
                    return;
                }
                await Task.Delay(options.ArrivalGrace, cancellationToken).ConfigureAwait(false);
                int decoded = counter.Count;
                int announced = message.Count ?? options.ProbeFrames;
                Log?.Invoke($"responder: {decoded}/{announced} probe frames decoded — reporting");
                await TrySendModeAsync(
                    new ModeCoordMessage { Action = ModeCoordAction.ProbeReport, Decoded = decoded, Count = announced },
                    cancellationToken).ConfigureAwait(false);

                if (homeVerifyOnly)
                {
                    // Post-revert home check is one-way; no reverse probes.
                    counter.Dispose();
                    counter = null;
                    return;
                }

                // Reverse probes: same tag, our own burst.
                int attemptTag = activeTag;
                await Task.Delay(options.PreProbeDelay, cancellationToken).ConfigureAwait(false);
                Log?.Invoke($"responder: transmitting {options.ProbeFrames} probe frames back (tag a{attemptTag})");
                var tx = await station.TransmitProbesAsync(attemptTag, options.ProbeFrames, cancellationToken)
                    .ConfigureAwait(false);
                await TrySendModeAsync(
                    new ModeCoordMessage
                    {
                        Action = ModeCoordAction.ProbesSent,
                        Count = options.ProbeFrames,
                        MeanTxMs = tx.MeanTxMs is { } mean ? (int)Math.Round(mean) : null,
                    },
                    cancellationToken).ConfigureAwait(false);
                return;
            }

            case ModeCoordAction.ProbeReport:
            {
                Log?.Invoke(string.Create(
                    CultureInfo.InvariantCulture,
                    $"responder: coordinator decoded {message.Decoded}/{message.Count} of our probes — attempt concluded"));
                counter?.Dispose();
                counter = null;
                return;
            }

            case ModeCoordAction.Revert:
            {
                Log?.Invoke($"responder: revert requested ({message.Reason ?? "no reason"}) — returning to home");
                counter?.Dispose();
                // Open the home-verify window FIRST (counting is passive): the
                // coordinator's verify probes may arrive while our own home
                // apply is still settling (bench-observed: a settle frame whose
                // TX echo goes missing blocks the apply for its full timeout,
                // and the probes sailed past a not-yet-open counter).
                counter = station.BeginProbeCount(telegramSequence);
                activeTag = telegramSequence;
                homeVerifyOnly = true;
                // Guard: the auto-ack of the revert telegram is in flight.
                await Task.Delay(options.PreProbeDelay, cancellationToken).ConfigureAwait(false);
                await ApplyHomeAsync(cancellationToken).ConfigureAwait(false);
                return;
            }

            case ModeCoordAction.Confirm:
            case ModeCoordAction.Reject:
            default:
                return; // coordinator-bound messages echoed back — not ours
        }
    }

    /// <summary>Local failure: go home and tell the coordinator (best effort).</summary>
    private async Task SelfRevertAsync(string reason, CancellationToken cancellationToken)
    {
        await ApplyHomeAsync(cancellationToken).ConfigureAwait(false);
        await TrySendModeAsync(
            new ModeCoordMessage { Action = ModeCoordAction.Revert, Reason = reason },
            cancellationToken).ConfigureAwait(false);
    }

    private async Task ApplyHomeAsync(CancellationToken cancellationToken)
    {
        try
        {
            await station.ApplyModeAsync(options.HomeMode, cancellationToken).ConfigureAwait(false);
            if (currentChannel != options.HomeChannel)
            {
                await station.ApplyChannelAsync(options.HomeChannel, cancellationToken).ConfigureAwait(false);
            }
            currentMode = options.HomeMode;
            currentChannel = options.HomeChannel;
            Log?.Invoke($"responder: at home (mode {options.HomeMode}, channel {options.HomeChannel})");
        }
        catch (ModeCoordException ex)
        {
            Log?.Invoke($"responder: HOME RESTORE FAILED ({ex.Message}) — check the rig by hand");
        }
    }

    private async Task EnsureHomeAsync(CancellationToken cancellationToken)
    {
        if (currentMode != options.HomeMode || currentChannel != options.HomeChannel)
        {
            await ApplyHomeAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private TimeSpan WatchdogPollInterval()
    {
        bool armed = currentMode != options.HomeMode || currentChannel != options.HomeChannel || counter is not null;
        if (!armed)
        {
            return options.ResponderIdleRevert;
        }
        // Check often enough that a short configured revert window still fires
        // promptly (tests), without spinning on the default 150 s window.
        long quarter = options.ResponderIdleRevert.Ticks / 4;
        long floor = TimeSpan.FromMilliseconds(100).Ticks;
        long cap = TimeSpan.FromSeconds(5).Ticks;
        return TimeSpan.FromTicks(Math.Clamp(quarter, floor, cap));
    }

    private async Task CheckWatchdogAsync(CancellationToken cancellationToken)
    {
        bool offHome = currentMode != options.HomeMode || currentChannel != options.HomeChannel;
        if (!offHome && counter is null)
        {
            return;
        }
        if (DateTimeOffset.UtcNow - lastHeard < options.ResponderIdleRevert)
        {
            return;
        }
        Log?.Invoke(string.Create(
            CultureInfo.InvariantCulture,
            $"responder: WATCHDOG — nothing heard for {options.ResponderIdleRevert.TotalSeconds:0}s while away from home; reverting"));
        counter?.Dispose();
        counter = null;
        await ApplyHomeAsync(cancellationToken).ConfigureAwait(false);
        lastHeard = DateTimeOffset.UtcNow; // don't re-fire every poll
    }

    private async Task TrySendModeAsync(ModeCoordMessage message, CancellationToken cancellationToken) =>
        await TrySendAsync(message.ToTelegram(NextSequence()), cancellationToken).ConfigureAwait(false);

    private async Task TrySendAsync(TuningTelegram telegram, CancellationToken cancellationToken)
    {
        try
        {
            await link.SendAsync(telegram, cancellationToken).ConfigureAwait(false);
        }
        catch (TuningLinkException ex)
        {
            Log?.Invoke($"responder: send unconfirmed ({ex.Message}) — the coordinator's timeouts cover it");
        }
    }

    private int NextSequence() => Interlocked.Increment(ref sequence);
}
