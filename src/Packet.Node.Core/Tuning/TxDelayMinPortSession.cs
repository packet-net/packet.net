using System.Globalization;
using System.Threading.Channels;
using Packet.Node.Core.Api;
using Packet.Tune.Core;

namespace Packet.Node.Core.Tuning;

/// <summary>What a <see cref="TxDelayMinPortSession"/> is doing on its port.</summary>
public enum TxDelayMinMode
{
    /// <summary>This port is the coordinator: it sweeps its OWN TXDELAY down and keys
    /// the probe traffic. The result is a recommendation — nothing is left applied.</summary>
    SweepCoordinator,

    /// <summary>This port is the meter: purely passive — it counts the peer's probes
    /// per step and reports over the side channel. Nothing local changes.</summary>
    Meter,

    /// <summary>This port is the coordinator running the explicit APPLY: set + settle +
    /// verify at one value, then (optionally) persist it into the port's KISS-params
    /// config.</summary>
    Apply,
}

/// <summary>
/// One live TXDELAY-minimisation session on a port — the node-side wrapper that runs the
/// <see cref="TxDelayMinimizer"/> / <see cref="TxDelayMinResponder"/> protocol loop
/// (layer 2 of docs/research/txdelay-optimisation.md) over the port's SDM link, projects
/// each sweep step into the same <see cref="TuningEvent"/> SSE feed the deviation
/// sessions use, and — like <see cref="PortTuningSession"/> — <b>guarantees the port is
/// restored to normal service on every exit path</b>. The protocol classes already
/// guarantee the modem-level restore (original TXDELAY + polite channel access on any
/// abort); this class adds the port-level restore (the full rebuild that un-pauses
/// normal AX.25 traffic).
/// </summary>
/// <remarks>
/// On the node, restore is a full port rebuild that re-applies the port's configured
/// KISS params — so an APPLY only outlives the session when it also persists
/// (<see cref="TxDelayMinPortSession"/> takes a persist callback the API wires to the
/// config store): apply-verify → persist <c>kiss.txDelay</c> → restore re-applies the
/// persisted value. An unpersisted apply is deliberately transient.
/// </remarks>
public sealed class TxDelayMinPortSession : IAsyncDisposable, IPortTuningSession
{
    private const int MaxHistory = 512;

    private readonly string sessionId;
    private readonly string portId;
    private readonly string peerSdmId;
    private readonly TxDelayMinMode mode;
    private readonly ITuningLink link;
    private readonly ITxDelayMinStation station;
    private readonly TxDelayMinOptions options;
    private readonly int applyTxDelayMs;
    private readonly Func<int, CancellationToken, Task<bool>>? persist;
    private readonly Func<CancellationToken, ValueTask> restore;
    private readonly TimeProvider clock;
    private readonly DateTimeOffset startedAt;

    private readonly CancellationTokenSource cts = new();
    private readonly object broadcastGate = new();
    private readonly List<TuningEvent> history = [];
    private readonly Dictionary<Guid, ChannelWriter<TuningEvent>> subscribers = [];

    private volatile TuningSessionState state = TuningSessionState.Armed;
    private volatile bool stopRequested;
    private Task? runTask;
    private int stepIndex;
    private int started;
    private int cleanedUp;
    private int disposed;

    /// <summary>
    /// Create a session. The caller has already paused the port and built the SDM link
    /// and the station; <paramref name="restore"/> un-pauses the port and is invoked
    /// exactly once during teardown.
    /// </summary>
    /// <param name="sessionId">Opaque unique id for this session.</param>
    /// <param name="portId">The port the session runs on.</param>
    /// <param name="peerSdmId">The peer radio's SDM data identity (reported in <see cref="Info"/>).</param>
    /// <param name="mode">Coordinator sweep, passive meter, or explicit apply.</param>
    /// <param name="link">The coordination link (owned — disposed during teardown, before restore).</param>
    /// <param name="station">The station adapter over the port's TNC (+ radio).</param>
    /// <param name="options">Sweep options (start/step/floor/probes/margins/timeouts).</param>
    /// <param name="restore">Un-pause/restore the port. Invoked exactly once during teardown.</param>
    /// <param name="applyTxDelayMs">The value an <see cref="TxDelayMinMode.Apply"/> session applies.</param>
    /// <param name="persist">Persist a verified TXDELAY (ms) into the port's KISS-params config;
    /// invoked only by a verified apply. Null = don't persist (the applied value then dies with
    /// the port restore — see the class remarks).</param>
    /// <param name="clock">Time source for event timestamps; null = system.</param>
    public TxDelayMinPortSession(
        string sessionId,
        string portId,
        string peerSdmId,
        TxDelayMinMode mode,
        ITuningLink link,
        ITxDelayMinStation station,
        TxDelayMinOptions options,
        Func<CancellationToken, ValueTask> restore,
        int applyTxDelayMs = 0,
        Func<int, CancellationToken, Task<bool>>? persist = null,
        TimeProvider? clock = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(sessionId);
        ArgumentException.ThrowIfNullOrEmpty(portId);
        ArgumentException.ThrowIfNullOrEmpty(peerSdmId);
        ArgumentNullException.ThrowIfNull(link);
        ArgumentNullException.ThrowIfNull(station);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(restore);
        if (mode == TxDelayMinMode.Apply && applyTxDelayMs <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(applyTxDelayMs), "an apply session needs a positive TXDELAY");
        }

        this.sessionId = sessionId;
        this.portId = portId;
        this.peerSdmId = peerSdmId;
        this.mode = mode;
        this.link = link;
        this.station = station;
        this.options = options;
        this.applyTxDelayMs = applyTxDelayMs;
        this.persist = persist;
        this.restore = restore;
        this.clock = clock ?? TimeProvider.System;
        startedAt = this.clock.GetUtcNow();
    }

    /// <summary>Diagnostic sink (restore failures, teardown notes). Null = silent.</summary>
    public Action<string>? Log { get; init; }

    /// <inheritdoc/>
    public string PortId => portId;

    /// <summary>The current lifecycle state.</summary>
    public TuningSessionState State => state;

    /// <inheritdoc/>
    public TuningSessionInfo Info => new(
        sessionId, portId, RoleWire, peerSdmId,
        TuningPreflight.StateToWire(state), options.ProbesPerStep, startedAt);

    private string RoleWire => mode switch
    {
        TxDelayMinMode.SweepCoordinator => "txdelay-coordinator",
        TxDelayMinMode.Meter => "txdelay-meter",
        _ => "txdelay-apply",
    };

    /// <summary>Arm the session: emit the <c>armed</c> event and launch the background
    /// protocol loop. Call once.</summary>
    public void Start()
    {
        if (Interlocked.Exchange(ref started, 1) != 0)
        {
            throw new InvalidOperationException("session already started");
        }
        EmitLifecycle(TuningSessionState.Armed, "armed");
        runTask = Task.Run(RunAsync);
    }

    /// <inheritdoc/>
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

    /// <inheritdoc/>
    public ValueTask StopAsync() => DisposeAsync();

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0)
        {
            return;
        }

        stopRequested = true;
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
            switch (mode)
            {
                case TxDelayMinMode.SweepCoordinator:
                    await RunSweepAsync().ConfigureAwait(false);
                    break;
                case TxDelayMinMode.Meter:
                    await RunMeterAsync().ConfigureAwait(false);
                    break;
                default:
                    await RunApplyAsync().ConfigureAwait(false);
                    break;
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

    private async Task RunSweepAsync()
    {
        await using var minimizer = new TxDelayMinimizer(link, station, options)
        {
            Log = line => Log?.Invoke(line),
            StepCompleted = EmitStep,
        };
        var result = await minimizer.RunSweepAsync(cts.Token).ConfigureAwait(false);
        await minimizer.EndAsync(result.RecommendedMs, cts.Token).ConfigureAwait(false);

        if (result.Success)
        {
            EmitLifecycle(TuningSessionState.Ended, "ended",
                note: TxDelayMinReport.Describe(result), recommendedMs: result.RecommendedMs);
        }
        else
        {
            EmitLifecycle(TuningSessionState.Error, "error", TxDelayMinReport.Describe(result));
        }
    }

    private async Task RunMeterAsync()
    {
        var responder = new TxDelayMinResponder(link, station, options)
        {
            Log = line => Log?.Invoke(line),
            StepReported = EmitStep,
        };
        int result = await responder.RunAsync(cts.Token).ConfigureAwait(false);
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
            EmitLifecycle(TuningSessionState.Error, "error",
                "the coordination link closed (or went idle) without a done");
        }
    }

    private async Task RunApplyAsync()
    {
        await using var minimizer = new TxDelayMinimizer(link, station, options)
        {
            Log = line => Log?.Invoke(line),
        };
        var result = await minimizer.ApplyAsync(applyTxDelayMs, cts.Token).ConfigureAwait(false);
        await minimizer.EndAsync(result.Verified ? result.TxDelayMs : null, cts.Token).ConfigureAwait(false);
        EmitStep(new TxDelaySweepStep(
            result.TxDelayMs, result.Decoded, result.Probes, null, result.MedianPreDataCarrierMs));

        if (!result.Verified)
        {
            EmitLifecycle(TuningSessionState.Error, "error",
                result.Detail ?? "apply did not verify");
            return;
        }

        bool persisted = false;
        if (persist is not null)
        {
            try
            {
                persisted = await persist(result.TxDelayMs, cts.Token).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Log?.Invoke($"txdelay-apply: persist failed: {ex.Message}");
            }
        }
        string note = string.Create(CultureInfo.InvariantCulture,
            $"TXDELAY {result.TxDelayMs} ms verified {result.Decoded}/{result.Probes}");
        note += persist is null
            ? " — not persisted (the port restore re-applies the configured value)"
            : persisted
                ? " — persisted to the port's KISS params"
                : " — PERSIST FAILED (the port restore re-applies the old configured value)";
        EmitLifecycle(
            persisted || persist is null ? TuningSessionState.Ended : TuningSessionState.Error,
            persisted || persist is null ? "ended" : "error",
            persisted || persist is null ? null : note,
            note: note,
            recommendedMs: result.TxDelayMs);
    }

    private void EmitStep(TxDelaySweepStep step)
    {
        int index = Interlocked.Increment(ref stepIndex);
        string note = string.Create(CultureInfo.InvariantCulture,
            $"TXDELAY {step.TxDelayMs} ms — {step.Decoded}/{step.Probes} decoded" +
            $"{(step.MedianPreDataCarrierMs is { } pre ? $", heard pre-data ~{pre:0} ms" : string.Empty)}");
        Publish(new TuningEvent(
            "round",
            clock.GetUtcNow(),
            TuningPreflight.StateToWire(state),
            BurstIndex: index,
            Decoded: step.Decoded,
            Total: step.Probes,
            Note: note,
            TxDelayMs: step.TxDelayMs,
            PreDataCarrierMs: step.MedianPreDataCarrierMs));
    }

    private void EmitLifecycle(
        TuningSessionState newState, string kind, string? error = null, string? note = null, int? recommendedMs = null)
    {
        state = newState;
        Publish(new TuningEvent(
            kind, clock.GetUtcNow(), TuningPreflight.StateToWire(newState),
            Note: note, Error: error, RecommendedTxDelayMs: recommendedMs));
    }

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

    private async ValueTask CleanupAsync()
    {
        if (Interlocked.Exchange(ref cleanedUp, 1) != 0)
        {
            return;
        }

        try
        {
            await link.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Log?.Invoke($"txdelay-min: link teardown fault ignored: {ex.Message}");
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
            Log?.Invoke($"txdelay-min: PORT RESTORE FAILED for '{portId}': {ex.Message}");
        }
    }

    private sealed class Subscription(TxDelayMinPortSession owner, Guid id) : IDisposable
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
