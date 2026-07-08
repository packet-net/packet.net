using Microsoft.Extensions.Logging;
using Packet.Core;
using Packet.Node.Core.Api;
using Packet.Node.Core.Configuration;
using Packet.Node.Core.Hosting;
using Packet.Node.Core.Radios;
using Packet.Radio.Tait;
using Packet.Tune.Core;

namespace Packet.Node.Core.Tuning;

/// <summary>Why a tuning-session start was refused.</summary>
public enum TuningStartError
{
    /// <summary>The port is unknown or not running (→ HTTP 404).</summary>
    NotFound,

    /// <summary>The request is malformed or the port cannot host a session (→ HTTP 400).</summary>
    BadRequest,

    /// <summary>A tuning session is already active on the port (→ HTTP 409).</summary>
    Conflict,
}

/// <summary>A tuning-session start was refused; <see cref="Error"/> classifies it for the API.</summary>
public sealed class TuningStartException : Exception
{
    /// <summary>Create with a classification and an operator-facing reason.</summary>
    public TuningStartException(TuningStartError error, string message)
        : base(message)
    {
        Error = error;
    }

    /// <summary>Parameterless form (framework convention).</summary>
    public TuningStartException()
    {
    }

    /// <summary>Create with a message only (defaults to <see cref="TuningStartError.BadRequest"/>).</summary>
    public TuningStartException(string message)
        : base(message)
    {
    }

    /// <summary>Create with a message and inner exception (framework convention).</summary>
    public TuningStartException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    /// <summary>The refusal classification.</summary>
    public TuningStartError Error { get; }
}

/// <summary>
/// The node's guided deviation-tuning service: at most one live <see cref="PortTuningSession"/> per
/// port (tracked by <see cref="PortTuningRegistry"/>). It validates the port (fail-fast, reusing the
/// capability doctor's SDM-enabled probe), <b>pauses the port's normal AX.25 traffic</b>, opens an
/// SDM coordination link to the peer, and starts the session — with a restore callback that
/// un-pauses the port (a full in-place port rebuild) on every session exit path. A DI singleton;
/// disposing it stops and restores every live session (node shutdown safety).
/// </summary>
/// <remarks>
/// <para><b>Pause/restore.</b> Pause is <c>Ax25Listener.StopAsync()</c> (the listener stops
/// transmitting/accepting; the modem serial port stays open so the session can key bursts and
/// meter). Restore is <c>PortSupervisor.RestartPortAsync</c> under the host's supervisor gate — a
/// full teardown + fresh bring-up of the port, the definitive guarantee that nothing is left paused,
/// wedged or keyed. Both run under <c>NodeHostedService.RunExclusiveAsync</c> so they serialise
/// against config reconciles.</para>
/// <para>The port stays claimed (a second start → 409) from the moment a session is registered until
/// its restore has fully completed and it auto-removes itself, so a new session can never race an
/// old one's port rebuild.</para>
/// </remarks>
public sealed partial class PortTuningService : IAsyncDisposable
{
    private readonly NodeHostedService host;
    private readonly IConfigProvider config;
    private readonly TimeProvider clock;
    private readonly ILogger<PortTuningService> logger;
    private readonly PortTuningRegistry registry = new();
    private readonly SemaphoreSlim startGate = new(1, 1);
    private int disposed;

    /// <summary>Create the service.</summary>
    /// <param name="host">The node host (supervisor access + the exclusive gate for pause/restore).</param>
    /// <param name="config">Live config (the node callsign for burst frames).</param>
    /// <param name="logger">Logger for restore failures / diagnostics.</param>
    /// <param name="clock">Time source for session timestamps; null = system.</param>
    public PortTuningService(
        NodeHostedService host,
        IConfigProvider config,
        ILogger<PortTuningService> logger,
        TimeProvider? clock = null)
    {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(logger);
        this.host = host;
        this.config = config;
        this.logger = logger;
        this.clock = clock ?? TimeProvider.System;
    }

    /// <summary>The live session on a port (deviation or txdelay flavour), or <c>null</c>
    /// when none is active.</summary>
    public IPortTuningSession? Get(string portId) => registry.Get(portId);

    /// <summary>
    /// Arm a tuning session on a port: validate, pause normal traffic, open the SDM link, and start.
    /// </summary>
    /// <param name="portId">The running port to tune on.</param>
    /// <param name="role">This port's role.</param>
    /// <param name="peerSdmId">The peer radio's 8-character SDM data identity.</param>
    /// <param name="burstFrames">Frames per measurement burst (clamped 1..50).</param>
    /// <param name="cancellationToken">Cancels the start (before the session is registered).</param>
    /// <returns>The armed session's projection.</returns>
    /// <exception cref="TuningStartException">The start was refused; <see cref="TuningStartException.Error"/>
    /// says whether that is a 404, 400 or 409.</exception>
    public Task<TuningSessionInfo> StartAsync(
        string portId, TuningRole role, string peerSdmId, int burstFrames, CancellationToken cancellationToken = default)
    {
        int frames = Math.Clamp(burstFrames, 1, 50);
        return ArmSessionAsync(portId, peerSdmId, async (running, tait, link, restore) =>
        {
            var options = new TuningSessionOptions { BurstFrames = frames };
            var source = ParseCallsign(config.Current.Identity.Callsign);

            IBurstStimulus? stimulus = null;
            IBurstMeter? meter = null;
            if (role == TuningRole.Tuned)
            {
                stimulus = new NinoTncBurstStimulus(running.NinoTnc!, source);
            }
            else
            {
                var burstMeter = new NinoTncBurstMeter(running.NinoTnc!, tait);
                // Probe the (firmware-3.41-era) GETRSSI level fast path once; captures the idle
                // baseline. A no-op query on newer firmware — never fatal.
                try
                {
                    await burstMeter.ProbeAudioLevelMeterAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    LogGetRssiProbeFailed(ex, portId);
                }
                meter = burstMeter;
            }

            PortTuningSession session = null!;
            session = new PortTuningSession(
                Guid.NewGuid().ToString("N"), portId, peerSdmId, role, link, stimulus, meter, options,
                restore: c => restore(session, c),
                clock: clock)
            {
                Log = line => LogSessionNote(portId, line),
            };
            return session;
        }, s => ((PortTuningSession)s).Start(), cancellationToken);
    }

    /// <summary>
    /// Arm a TXDELAY-minimisation session on a port (layer 2 of
    /// docs/research/txdelay-optimisation.md): coordinator (sweeps this port's OWN
    /// TXDELAY down, keying probes) or meter (purely passive counting for a remote
    /// coordinator). Same validate/pause/SDM plumbing — and the same
    /// port-always-restored guarantee — as <see cref="StartAsync"/>.
    /// </summary>
    /// <param name="portId">The running port.</param>
    /// <param name="coordinator"><c>true</c> = coordinator (transmits!), <c>false</c> = meter.</param>
    /// <param name="peerSdmId">The peer radio's 8-character SDM data identity.</param>
    /// <param name="options">Sweep options. For the coordinator, a null
    /// <c>StartTxDelayMs</c> caller should pre-seed from the port's configured KISS
    /// txDelay — the API layer does.</param>
    /// <param name="cancellationToken">Cancels the start (before the session is registered).</param>
    /// <exception cref="TuningStartException">The start was refused (404/400/409).</exception>
    public Task<TuningSessionInfo> StartTxDelayMinAsync(
        string portId, bool coordinator, string peerSdmId, TxDelayMinOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        return ArmSessionAsync(portId, peerSdmId, (running, tait, link, restore) =>
        {
            var session = BuildTxDelaySession(
                running, tait, link, restore, portId, peerSdmId,
                coordinator ? TxDelayMinMode.SweepCoordinator : TxDelayMinMode.Meter,
                options);
            return Task.FromResult<IPortTuningSession>(session);
        }, s => ((TxDelayMinPortSession)s).Start(), cancellationToken);
    }

    /// <summary>
    /// Arm the explicit APPLY: set + settle + verify <paramref name="txDelayMs"/> with
    /// the far meter counting, then persist via <paramref name="persist"/> when
    /// verified. On the node an apply only outlives the session when persisted — the
    /// port restore is a full rebuild that re-applies the configured KISS params.
    /// </summary>
    /// <param name="portId">The running port.</param>
    /// <param name="peerSdmId">The peer radio's 8-character SDM data identity.</param>
    /// <param name="txDelayMs">The TXDELAY to apply and verify, in ms.</param>
    /// <param name="options">Verify options (probes, timeouts; StartTxDelayMs = the fallback
    /// restored on a failed verify).</param>
    /// <param name="persist">Persist a verified value into the port's KISS-params config
    /// (the API wires this to the config store); null = transient apply.</param>
    /// <param name="cancellationToken">Cancels the start (before the session is registered).</param>
    /// <exception cref="TuningStartException">The start was refused (404/400/409).</exception>
    public Task<TuningSessionInfo> StartTxDelayApplyAsync(
        string portId, string peerSdmId, int txDelayMs, TxDelayMinOptions options,
        Func<int, CancellationToken, Task<bool>>? persist,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(txDelayMs);
        return ArmSessionAsync(portId, peerSdmId, (running, tait, link, restore) =>
        {
            var session = BuildTxDelaySession(
                running, tait, link, restore, portId, peerSdmId, TxDelayMinMode.Apply, options,
                txDelayMs, persist);
            return Task.FromResult<IPortTuningSession>(session);
        }, s => ((TxDelayMinPortSession)s).Start(), cancellationToken);
    }

    private TxDelayMinPortSession BuildTxDelaySession(
        RunningPort running, TaitCcdiRadio tait, SdmTuningLink link,
        Func<IPortTuningSession, CancellationToken, ValueTask> restore,
        string portId, string peerSdmId, TxDelayMinMode mode, TxDelayMinOptions options,
        int applyTxDelayMs = 0, Func<int, CancellationToken, Task<bool>>? persist = null)
    {
        var source = ParseCallsign(config.Current.Identity.Callsign);
        var station = new NinoTncTxDelayMinStation(running.NinoTnc!, source, tait)
        {
            Log = line => LogSessionNote(portId, line),
        };
        TxDelayMinPortSession session = null!;
        session = new TxDelayMinPortSession(
            Guid.NewGuid().ToString("N"), portId, peerSdmId, mode, link, station, options,
            restore: c => restore(session, c),
            applyTxDelayMs: applyTxDelayMs,
            persist: persist,
            clock: clock)
        {
            Log = line => LogSessionNote(portId, line),
        };
        return session;
    }

    /// <summary>
    /// The shared arm plumbing every session flavour uses: validate the port (running,
    /// NinoTNC + live Tait + peer id), claim the one-session-per-port slot, pause normal
    /// AX.25 traffic, enable PROGRESS + fail-fast SDM check, open the SDM link, build +
    /// register + start the session — and restore the port if anything fails before the
    /// session takes ownership of restore.
    /// </summary>
    private async Task<TuningSessionInfo> ArmSessionAsync(
        string portId,
        string peerSdmId,
        Func<RunningPort, TaitCcdiRadio, SdmTuningLink, Func<IPortTuningSession, CancellationToken, ValueTask>, Task<IPortTuningSession>> factory,
        Action<IPortTuningSession> start,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(portId);
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);

        var running = host.Supervisor?.GetPort(portId)
            ?? throw new TuningStartException(TuningStartError.NotFound, $"port '{portId}' is not running");
        // Resolve the LIVE driver: a head-end-bound radio sits behind the reconnect facade
        // (#576), so the concrete Tait handle must be re-resolved per operation, never cached.
        var tait = RadioControls.LiveTait(running.Radio);
        if (!TuningPreflight.CanArm(running.NinoTnc is not null, tait is not null, peerSdmId, out var reason))
        {
            throw new TuningStartException(TuningStartError.BadRequest, reason!);
        }

        await startGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (registry.IsActive(portId))
            {
                throw new TuningStartException(
                    TuningStartError.Conflict, $"a tuning session is already active on port '{portId}'");
            }

            // Pause normal AX.25 traffic (serialised against reconciles by the supervisor gate).
            await host.RunExclusiveAsync(
                async () => await running.Listener.StopAsync().ConfigureAwait(false),
                cancellationToken).ConfigureAwait(false);

            bool startedOk = false;
            try
            {
                // PROGRESS carries DCD / delivery receipts the SDM link and the SDM check both need.
                await tait!.SetProgressMessagesAsync(true, cancellationToken).ConfigureAwait(false);

                // Fail-fast SDM-enabled check (the capability doctor's probe): a wildcard SDM the
                // radio rejects with 0/06 when its programming disables short data messages.
                await CheckSdmEnabledAsync(tait, cancellationToken).ConfigureAwait(false);

                var link = SdmTuningLink.Create(tait, peerSdmId);
                link.Log = line => LogSdmLink(portId, line);

                var session = await factory(running, tait, link, (s, c) => RestoreAsync(s, c))
                    .ConfigureAwait(false);
                registry.TryAdd(session);
                start(session);
                startedOk = true;
                var info = session.Info;
                LogSessionArmed(portId, info.Role, peerSdmId, info.BurstFrames);
                return info;
            }
            finally
            {
                // Paused but failed before a session took ownership of restore → restore now.
                if (!startedOk)
                {
                    await RestorePortAsync(portId, CancellationToken.None).ConfigureAwait(false);
                }
            }
        }
        finally
        {
            startGate.Release();
        }
    }

    /// <summary>
    /// Operator "I've adjusted the pot" signal for a port's tuned session.
    /// </summary>
    /// <returns><c>true</c> when a waiting round was advanced.</returns>
    /// <exception cref="TuningStartException"><see cref="TuningStartError.NotFound"/> when no session
    /// is active; <see cref="TuningStartError.Conflict"/> when it does not apply (meter role or no
    /// round awaiting input).</exception>
    public bool SignalNext(string portId)
    {
        var session = registry.Get(portId)
            ?? throw new TuningStartException(TuningStartError.NotFound, $"no tuning session on port '{portId}'");
        if (session is not PortTuningSession deviation || !deviation.SignalNext())
        {
            throw new TuningStartException(
                TuningStartError.Conflict,
                "no measurement round is awaiting the operator (meter role, a txdelay session, or a round is still running)");
        }
        return true;
    }

    /// <summary>
    /// Stop the port's session and restore the port. Returns once the port is back to normal
    /// service. No-op when no session is active.
    /// </summary>
    /// <returns><c>true</c> when a session was stopped.</returns>
    public async Task<bool> StopAsync(string portId, CancellationToken cancellationToken = default)
    {
        _ = cancellationToken; // stop always drives to completion (restore must run)
        var session = registry.Get(portId);
        if (session is null)
        {
            return false;
        }
        await session.StopAsync().ConfigureAwait(false);
        return true;
    }

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0)
        {
            return;
        }
        await registry.DisposeAsync().ConfigureAwait(false);
        startGate.Dispose();
    }

    private async ValueTask RestoreAsync(IPortTuningSession session, CancellationToken cancellationToken)
    {
        try
        {
            await RestorePortAsync(session.PortId, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            // Free the slot only after the port has been restored — never leave it claimed.
            registry.Remove(session);
        }
    }

    private async ValueTask RestorePortAsync(string portId, CancellationToken cancellationToken)
    {
        try
        {
            await host.RunExclusiveAsync(
                () => host.Supervisor is { } sup ? sup.RestartPortAsync(portId, cancellationToken) : Task.FromResult(false),
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            LogRestoreFailed(ex, portId);
        }
    }

    private static async Task CheckSdmEnabledAsync(TaitCcdiRadio radio, CancellationToken cancellationToken)
    {
        try
        {
            await radio.SendSdmAsync("********", "PDNTUNE", leadInDelay: null, cancellationToken).ConfigureAwait(false);
        }
        catch (TaitCcdiException ex) when (ex.Error is { Category: '0', ErrorNumber: 0x06 })
        {
            throw new TuningStartException(
                TuningStartError.BadRequest,
                "SDM is disabled in the radio's programming — enable SDM + auto-acks with the Tait programming app");
        }
    }

    private static Callsign ParseCallsign(string? callsign) =>
        !string.IsNullOrWhiteSpace(callsign) && Callsign.TryParse(callsign, out var parsed)
            ? parsed
            : Callsign.Parse("N0CALL");

    [LoggerMessage(Level = LogLevel.Information, Message = "tuning[{Port}] session armed — role={Role} peer={Peer} burst={Burst}")]
    private partial void LogSessionArmed(string port, string role, string peer, int burst);

    [LoggerMessage(Level = LogLevel.Information, Message = "tuning[{Port}] {Note}")]
    private partial void LogSessionNote(string port, string note);

    [LoggerMessage(Level = LogLevel.Debug, Message = "tuning[{Port}] sdm-link: {Line}")]
    private partial void LogSdmLink(string port, string line);

    [LoggerMessage(Level = LogLevel.Debug, Message = "tuning[{Port}] GETRSSI probe failed (non-fatal)")]
    private partial void LogGetRssiProbeFailed(Exception ex, string port);

    [LoggerMessage(Level = LogLevel.Error, Message = "tuning[{Port}] PORT RESTORE FAILED")]
    private partial void LogRestoreFailed(Exception ex, string port);
}
