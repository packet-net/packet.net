using System.Threading.Channels;
using Packet.Node.Core.Api;
using Packet.Tune.Core;

namespace Packet.Node.Core.Tuning;

/// <summary>
/// One live guided deviation-tuning session on a port — the node-side state machine that drives the
/// <see cref="TuningSession"/> protocol loop, projects each round and lifecycle transition into a
/// structured <see cref="TuningEvent"/> feed for the web UI, gates the operator's "next round"
/// signal, and — above all — <b>guarantees the port is restored to normal service on every exit
/// path</b> (clean finish, link error, operator stop, an exception, or node shutdown).
/// </summary>
/// <remarks>
/// <para>
/// The class does not re-implement the tuning protocol: it runs the unmodified
/// <see cref="TuningSession.RunTunedAsync"/> / <see cref="TuningSession.RunMeterAsync"/> loop over an
/// <see cref="ObservingTuningLink"/> and decodes the same <c>HI</c>/<c>MS</c>/<c>AD</c> telegrams off
/// the wire into events. The tuned role's between-rounds pause (its <see cref="ITuningPrompt"/>) is
/// wired to the operator's <see cref="SignalNext"/> instead of a console read.
/// </para>
/// <para>
/// <b>Restore discipline.</b> The restore callback (un-pause the port) is invoked exactly once, from
/// a single-flight cleanup that runs in the background loop's <c>finally</c>. Every stop path funnels
/// through that loop (cancellation) or, if the loop never started, through <see cref="DisposeAsync"/>
/// directly — so a session can never leave a port paused or a radio keyed. Mirrors the radio
/// unkey-on-dispose discipline.
/// </para>
/// <para>The session is testable without hardware: construct it over an in-memory
/// <see cref="ITuningLink"/> pair plus fake <see cref="IBurstStimulus"/>/<see cref="IBurstMeter"/> and
/// a recording restore callback.</para>
/// </remarks>
public sealed class PortTuningSession : IAsyncDisposable, IPortTuningSession
{
    private const int MaxHistory = 512;

    private readonly string sessionId;
    private readonly string portId;
    private readonly string peerSdmId;
    private readonly TuningRole role;
    private readonly int burstFrames;
    private readonly ObservingTuningLink observingLink;
    private readonly IBurstStimulus? stimulus;
    private readonly IBurstMeter? meter;
    private readonly ITuningPrompt? prompt;
    private readonly TuningSessionOptions options;
    private readonly Func<CancellationToken, ValueTask> restore;
    private readonly TimeProvider clock;
    private readonly DateTimeOffset startedAt;

    private readonly CancellationTokenSource cts = new();

    private readonly object broadcastGate = new();
    private readonly List<TuningEvent> history = [];
    private readonly Dictionary<Guid, ChannelWriter<TuningEvent>> subscribers = [];

    private readonly object nextGate = new();
    private TaskCompletionSource<bool>? pendingNext;
    private volatile bool stopRequested;

    // Round-tracking state — touched only on the link's single-consumer receive/send path.
    private MeterReport? pendingReport;
    private MeterReport? previousRoundReport;
    private int roundCount;
    private bool peerAnnounced;

    private volatile TuningSessionState state = TuningSessionState.Armed;
    private Task? runTask;
    private int started;
    private int cleanedUp;
    private int disposed;

    /// <summary>
    /// Create a session. The caller has already paused the port and built the coordination link and
    /// the burst adapter for the role; <paramref name="restore"/> un-pauses the port and is invoked
    /// exactly once during teardown.
    /// </summary>
    /// <param name="sessionId">Opaque unique id for this session.</param>
    /// <param name="portId">The port the session runs on.</param>
    /// <param name="peerSdmId">The peer radio's SDM data identity (reported in <see cref="Info"/>).</param>
    /// <param name="role">This port's role.</param>
    /// <param name="link">The coordination link (owned — disposed during teardown, before restore).</param>
    /// <param name="stimulus">The burst transmitter — required for the <see cref="TuningRole.Tuned"/> role.</param>
    /// <param name="meter">The burst meter — required for the <see cref="TuningRole.Meter"/> role.</param>
    /// <param name="options">Session options (burst size, pre-burst guard).</param>
    /// <param name="restore">Un-pause/restore the port. Invoked exactly once during teardown, after
    /// the link is disposed. Must not throw meaningfully — failures are logged, not propagated.</param>
    /// <param name="clock">Time source for event timestamps; null = system.</param>
    public PortTuningSession(
        string sessionId,
        string portId,
        string peerSdmId,
        TuningRole role,
        ITuningLink link,
        IBurstStimulus? stimulus,
        IBurstMeter? meter,
        TuningSessionOptions options,
        Func<CancellationToken, ValueTask> restore,
        TimeProvider? clock = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(sessionId);
        ArgumentException.ThrowIfNullOrEmpty(portId);
        ArgumentException.ThrowIfNullOrEmpty(peerSdmId);
        ArgumentNullException.ThrowIfNull(link);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(restore);
        if (role == TuningRole.Tuned && stimulus is null)
        {
            throw new ArgumentNullException(nameof(stimulus), "the tuned role needs a burst stimulus");
        }
        if (role == TuningRole.Meter && meter is null)
        {
            throw new ArgumentNullException(nameof(meter), "the meter role needs a burst meter");
        }

        this.sessionId = sessionId;
        this.portId = portId;
        this.peerSdmId = peerSdmId;
        this.role = role;
        this.stimulus = stimulus;
        this.meter = meter;
        this.options = options;
        this.restore = restore;
        this.clock = clock ?? TimeProvider.System;
        burstFrames = options.BurstFrames;
        startedAt = this.clock.GetUtcNow();
        observingLink = new ObservingTuningLink(link, OnTelegram);
        prompt = role == TuningRole.Tuned ? new GatedPrompt(this) : null;
    }

    /// <summary>Diagnostic sink (restore failures, teardown notes). Null = silent.</summary>
    public Action<string>? Log { get; init; }

    /// <summary>The session id.</summary>
    public string SessionId => sessionId;

    /// <summary>The port this session runs on.</summary>
    public string PortId => portId;

    /// <summary>This port's role.</summary>
    public TuningRole Role => role;

    /// <summary>The current lifecycle state.</summary>
    public TuningSessionState State => state;

    /// <summary>The API projection of this session's current state.</summary>
    public TuningSessionInfo Info => new(
        sessionId, portId, TuningPreflight.RoleToWire(role), peerSdmId,
        TuningPreflight.StateToWire(state), burstFrames, startedAt);

    /// <summary>
    /// Arm the session: emit the <c>armed</c> event and launch the background protocol loop. Call
    /// once.
    /// </summary>
    public void Start()
    {
        if (Interlocked.Exchange(ref started, 1) != 0)
        {
            throw new InvalidOperationException("session already started");
        }
        EmitLifecycle(TuningSessionState.Armed, "armed");
        runTask = Task.Run(RunAsync);
    }

    /// <summary>
    /// Subscribe to the live event feed. The new subscriber first receives every event so far (so a
    /// late or reconnecting SSE client sees the full trend), then live events. If the session has
    /// already finished, the reader is completed after the replay.
    /// </summary>
    /// <param name="reader">The channel to read <see cref="TuningEvent"/>s from.</param>
    /// <returns>An <see cref="IDisposable"/> that unsubscribes.</returns>
    public IDisposable Subscribe(out ChannelReader<TuningEvent> reader)
    {
        var channel = Channel.CreateUnbounded<TuningEvent>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false,
        });
        var id = Guid.NewGuid();
        lock (broadcastGate)
        {
            foreach (var e in history)
            {
                channel.Writer.TryWrite(e);
            }
            if (TuningPreflight.IsTerminal(state))
            {
                channel.Writer.TryComplete();
            }
            else
            {
                subscribers[id] = channel.Writer;
            }
        }
        reader = channel.Reader;
        return new Subscription(this, id);
    }

    /// <summary>
    /// Operator signal: "I've adjusted the pot — run the next measurement round." Applies only to the
    /// <see cref="TuningRole.Tuned"/> role while a round is awaiting the operator.
    /// </summary>
    /// <returns><c>true</c> when the signal advanced a waiting round; <c>false</c> when it does not
    /// apply (meter role, session finished, or no round is currently awaiting input).</returns>
    public bool SignalNext()
    {
        if (role != TuningRole.Tuned || TuningPreflight.IsTerminal(state))
        {
            return false;
        }
        lock (nextGate)
        {
            if (pendingNext is not { } p || !p.TrySetResult(true))
            {
                return false;
            }
        }
        SetState(TuningSessionState.PeerConnected);
        return true;
    }

    /// <summary>
    /// Stop the session and restore the port. Returns once the port has been restored. Idempotent.
    /// </summary>
    public ValueTask StopAsync() => DisposeAsync();

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0)
        {
            return;
        }

        RequestStop();
        if (!cts.IsCancellationRequested)
        {
            try
            {
                await cts.CancelAsync().ConfigureAwait(false);
            }
            catch (ObjectDisposedException)
            {
            }
        }

        if (runTask is { } t)
        {
            try
            {
                await t.ConfigureAwait(false);
            }
            catch
            {
                // The loop's finally already ran cleanup/restore; a fault here must not escape dispose.
            }
        }
        else
        {
            // Never started — still guarantee the port is restored.
            await CleanupAsync().ConfigureAwait(false);
        }

        cts.Dispose();
    }

    private async Task RunAsync()
    {
        try
        {
            int result = role == TuningRole.Tuned
                ? await TuningSession.RunTunedAsync(observingLink, stimulus!, prompt!, options, TextWriter.Null, cts.Token)
                    .ConfigureAwait(false)
                : await TuningSession.RunMeterAsync(observingLink, meter!, options, TextWriter.Null, cts.Token)
                    .ConfigureAwait(false);

            if (stopRequested)
            {
                EmitLifecycle(TuningSessionState.Stopped, "ended");
            }
            else if (result == 0)
            {
                EmitLifecycle(TuningSessionState.Ended, "ended");
            }
            else
            {
                EmitLifecycle(
                    TuningSessionState.Error, "error",
                    "the tuning coordination link closed without a goodbye");
            }
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
            EmitLifecycle(TuningSessionState.Stopped, "ended");
        }
        catch (Exception ex)
        {
            EmitLifecycle(TuningSessionState.Error, "error", ex.Message);
        }
        finally
        {
            await CleanupAsync().ConfigureAwait(false);
        }
    }

    private void RequestStop()
    {
        lock (nextGate)
        {
            stopRequested = true;
            pendingNext?.TrySetResult(false);
        }
    }

    private async ValueTask CleanupAsync()
    {
        if (Interlocked.Exchange(ref cleanedUp, 1) != 0)
        {
            return;
        }

        try
        {
            await observingLink.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Log?.Invoke($"tuning: link teardown fault ignored: {ex.Message}");
        }

        lock (broadcastGate)
        {
            foreach (var w in subscribers.Values)
            {
                w.TryComplete();
            }
            subscribers.Clear();
        }

        try
        {
            await restore(CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // Restore is best-effort by contract, but a failure is a genuine incident — surface it.
            Log?.Invoke($"tuning: PORT RESTORE FAILED for '{portId}': {ex.Message}");
        }
    }

    // ── Telegram → event projection (runs on the link's send/receive path) ──

    private void OnTelegram(TuningTelegram telegram, bool sent)
    {
        // The first telegram we *receive* is proof the peer is there — a RQ answering our HELLO on
        // the tuned side, a HELLO ready-beacon on the meter side (the meter never beacons
        // unsolicited, so we can't key off a received HELLO alone). Announce once.
        if (!sent && !peerAnnounced)
        {
            peerAnnounced = true;
            EmitLifecycle(TuningSessionState.PeerConnected, "peer-connected");
        }

        switch (telegram.Verb)
        {
            case TuningVerb.Measurement:
                // MS flows in the direction we observe it (received on the tuned side, sent on the
                // meter side) — buffer it for the AD that immediately follows.
                if (MeterReport.TryParse(telegram.Args, out var report) && report is not null)
                {
                    pendingReport = report;
                }
                break;

            case TuningVerb.Advice:
                EmitRound(pendingReport, DeviationAdvisor.FromWire(telegram.Args));
                break;

            default:
                break;
        }
    }

    private void EmitRound(MeterReport? report, TuningAdvice? advice)
    {
        roundCount++;
        string note = advice is { } a
            ? DeviationAdvisor.Describe(a)
            : "advice unknown — the peer sent an unrecognised token";
        if (report is not null && DeviationAdvisor.DescribeLevel(report, previousRoundReport) is { } levelNote)
        {
            note += " — " + levelNote;
        }

        var evt = new TuningEvent(
            "round",
            clock.GetUtcNow(),
            TuningPreflight.StateToWire(state),
            BurstIndex: roundCount,
            Decoded: report?.DecodedFrames,
            Total: report?.RequestedFrames,
            LevelDb: report?.AudioLevelDb,
            RssiDbm: report?.RssiDbm,
            Advice: AdviceToWire(advice),
            Note: note);
        previousRoundReport = report;
        Publish(evt);
    }

    private void EmitLifecycle(TuningSessionState newState, string kind, string? error = null)
    {
        SetState(newState);
        Publish(new TuningEvent(
            kind, clock.GetUtcNow(), TuningPreflight.StateToWire(newState), Error: error));
    }

    private void SetState(TuningSessionState newState) => state = newState;

    private void Publish(TuningEvent evt)
    {
        lock (broadcastGate)
        {
            history.Add(evt);
            if (history.Count > MaxHistory)
            {
                history.RemoveAt(0);
            }
            foreach (var w in subscribers.Values)
            {
                w.TryWrite(evt);
            }
            // EmitLifecycle sets the new state before publishing, so a terminal event is visible
            // here as a terminal `state` — complete every live reader so its SSE loop ends cleanly.
            if (TuningPreflight.IsTerminal(state))
            {
                foreach (var w in subscribers.Values)
                {
                    w.TryComplete();
                }
                subscribers.Clear();
            }
        }
    }

    private static string? AdviceToWire(TuningAdvice? advice) => advice switch
    {
        TuningAdvice.Up => "up",
        TuningAdvice.Down => "down",
        TuningAdvice.Ok => "ok",
        TuningAdvice.Sweep => "sweep",
        _ => null,
    };

    // ── Operator "next round" gate (tuned role) ──

    private Task<bool> WaitForNextAsync(CancellationToken cancellationToken)
    {
        TaskCompletionSource<bool> tcs;
        lock (nextGate)
        {
            if (stopRequested)
            {
                return Task.FromResult(false);
            }
            tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            pendingNext = tcs;
        }

        var registration = cancellationToken.Register(
            static s => ((TaskCompletionSource<bool>)s!).TrySetCanceled(), tcs);
        _ = tcs.Task.ContinueWith(
            _ => registration.Dispose(),
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);

        EmitLifecycle(TuningSessionState.AwaitingAdjustment, "awaiting-adjustment");
        return tcs.Task;
    }

    private sealed class GatedPrompt(PortTuningSession owner) : ITuningPrompt
    {
        public Task<bool> ContinueAsync(CancellationToken cancellationToken = default) =>
            owner.WaitForNextAsync(cancellationToken);
    }

    private sealed class Subscription(PortTuningSession owner, Guid id) : IDisposable
    {
        private int disposedFlag;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref disposedFlag, 1) != 0)
            {
                return;
            }
            lock (owner.broadcastGate)
            {
                if (owner.subscribers.Remove(id, out var writer))
                {
                    writer.TryComplete();
                }
            }
        }
    }
}
