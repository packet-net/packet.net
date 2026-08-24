using System.Collections.Concurrent;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Packet.Node.Core.Api;
using Packet.Node.Core.Configuration;
using Packet.Node.Core.Hosting;
using Packet.Node.Core.Radios;
using Packet.Radio.Tait;
using Packet.Tune.Core;

namespace Packet.Node.Core.Hail;

/// <summary>Why a hail was refused (maps to an HTTP status).</summary>
public enum HailError
{
    /// <summary>The port is unknown or not running (→ 404).</summary>
    NotFound,

    /// <summary>The request is malformed or the port cannot hail (→ 400).</summary>
    BadRequest,

    /// <summary>The peer never answered before the timeout (→ 504).</summary>
    Timeout,

    /// <summary>The side channel could not carry the hail (→ 502).</summary>
    LinkFailed,
}

/// <summary>A hail was refused or failed; <see cref="Error"/> classifies it for the API.</summary>
public sealed class HailException : Exception
{
    /// <summary>Create with a classification and an operator-facing reason.</summary>
    public HailException(HailError error, string message)
        : base(message)
    {
        Error = error;
    }

    /// <summary>Parameterless form (framework convention).</summary>
    public HailException()
    {
    }

    /// <summary>Message-only form (defaults to <see cref="HailError.BadRequest"/>).</summary>
    public HailException(string message)
        : base(message)
    {
    }

    /// <summary>Message + inner exception (framework convention).</summary>
    public HailException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    /// <summary>The refusal classification.</summary>
    public HailError Error { get; }
}

/// <summary>
/// The node's <b>SDM station-hail</b> service: two capabilities on a radio-attached port.
/// <list type="bullet">
///   <item><b>Hail</b> (<c>POST /api/v1/ports/{id}/hail</c>): send a hail to a peer and return the
///     peer's <see cref="PortHailStatus"/> - its callsign, current NinoTNC mode/bitrate, channel and
///     capabilities. Because the hail rides the radio's own FFSK modem, it works (and reports the
///     peer's mode) even when the packet path is broken by a mode mismatch.</item>
///   <item><b>Resident responder</b> (opt-in per port via <see cref="PortRadioConfig.HailResponder"/>):
///     listen for a configured neighbour's hails and auto-reply with this node's status.</item>
/// </list>
/// A DI singleton and a hosted service - its background loop reconciles the resident responders
/// against the running ports + config; disposing it tears every responder down.
/// </summary>
/// <remarks>
/// <para><b>One radio, one SDM buffer.</b> A radio's SDM receive buffer is one-deep, so a port has a
/// single side-channel consumer at a time. When a resident responder is armed it owns a shared
/// <see cref="FanOutTuningLink"/> (bound to its configured neighbour), and an on-demand hail borrows
/// that shared link - but only to the same peer (v1 is point-to-point; hailing a different peer
/// while a responder is bound is refused). With no resident responder, a hail opens a transient link
/// to the requested peer for the duration of the call.</para>
/// <para>The SDM-enabled fail-fast preflight (the capability doctor's wildcard-SDM probe) runs on the
/// transient path; on the shared path the responder's link already proved SDM works.</para>
/// <para><b>Quiet, backing-off retries.</b> A responder that cannot start (SDM disabled in the
/// radio's programming, a CCDI/IO fault) is retried on a per-port interval that doubles from the
/// reconcile cadence up to <see cref="MaxRetryInterval"/>, and its reason is logged only when it
/// CHANGES, the same transition-logging convention as <c>HeadEndHealthMonitor</c>. A standing fault
/// costs one line and a slow re-probe, not a warning every reconcile cycle forever.</para>
/// </remarks>
public sealed partial class PortHailService : BackgroundService
{
    private static readonly TimeSpan ReconcileInterval = TimeSpan.FromSeconds(10);

    /// <summary>Ceiling on the per-port resident-start retry interval. A radio with SDM
    /// switched off in its programming never becomes a responder without an operator, so the
    /// probe settles to once every few minutes rather than every reconcile cycle.</summary>
    private static readonly TimeSpan MaxRetryInterval = TimeSpan.FromMinutes(5);

    private static readonly StationHailerOptions NodeHailerOptions = new()
    {
        MaxAttempts = 2,
        ReplyTimeout = TimeSpan.FromSeconds(20),
    };

    private readonly NodeHostedService host;
    private readonly IConfigProvider config;
    private readonly TimeProvider timeProvider;
    private readonly ILogger<PortHailService> logger;
    private readonly ConcurrentDictionary<string, ResidentResponder> residents = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, SemaphoreSlim> portLocks = new(StringComparer.Ordinal);

    // Per-port resident-start failure state: the last reason (so a repeat is silent and only a
    // CHANGED reason logs again) plus the backoff schedule. Written only by the reconcile loop.
    private readonly ConcurrentDictionary<string, ResidentFailure> residentFailures = new(StringComparer.Ordinal);

    /// <summary>Create the service. <paramref name="timeProvider"/> drives the reconcile
    /// cadence and the per-port retry backoff (tests pass a fake clock); null is the system
    /// clock, so existing call sites are unaffected.</summary>
    public PortHailService(
        NodeHostedService host, IConfigProvider config, ILogger<PortHailService> logger, TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(logger);
        this.host = host;
        this.config = config;
        this.logger = logger;
        this.timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <summary>
    /// Hail a peer over the port's SDM side channel and return its status.
    /// </summary>
    /// <param name="portId">The running port to hail from.</param>
    /// <param name="peerSdmId">The peer radio's 8-character SDM data identity.</param>
    /// <param name="cancellationToken">Cancels the hail.</param>
    /// <exception cref="HailException">The hail was refused or failed; <see cref="HailException.Error"/>
    /// classifies it (404 / 400 / 504 / 502).</exception>
    public async Task<PortHailStatus> HailAsync(string portId, string peerSdmId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(portId);

        var running = host.Supervisor?.GetPort(portId)
            ?? throw new HailException(HailError.NotFound, $"port '{portId}' is not running");
        // Resolve the LIVE driver: a head-end-bound radio sits behind the reconnect facade
        // (#576), so the concrete Tait handle is re-resolved per hail, never cached.
        if (RadioControls.LiveTait(running.Radio) is not { } tait)
        {
            throw new HailException(HailError.BadRequest,
                "this port has no Tait CCDI radio attached - a hail needs the radio's SDM side channel." +
                RadioControls.WhyNoRadio(host.Supervisor, portId));
        }
        if (string.IsNullOrEmpty(peerSdmId) || peerSdmId.Length != TaitSdmSideChannel.IdentityLength)
        {
            throw new HailException(HailError.BadRequest,
                $"peerSdmId must be exactly {TaitSdmSideChannel.IdentityLength} characters (the peer radio's SDM data identity)");
        }

        string callsign = ResolveCallsign();
        var gate = portLocks.GetOrAdd(portId, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (residents.TryGetValue(portId, out var resident))
            {
                if (!string.Equals(peerSdmId, resident.Peer, StringComparison.Ordinal))
                {
                    throw new HailException(HailError.BadRequest,
                        $"this port's SDM channel is bound to responder peer {resident.Peer}; " +
                        "hailing a different peer while the responder is armed is not supported (v1 is point-to-point)");
                }
                return await RunHailAsync(resident.Link, callsign, portId, peerSdmId, cancellationToken).ConfigureAwait(false);
            }

            // No resident responder: a transient link, SDM-preflighted first.
            await PreflightSdmEnabledAsync(tait, cancellationToken).ConfigureAwait(false);
            await tait.SetProgressMessagesAsync(true, cancellationToken).ConfigureAwait(false);
            await using var link = SdmTuningLink.Create(tait, peerSdmId, extendedSdm: true);
            link.Log = line => LogSdm(portId, line);
            return await RunHailAsync(link, callsign, portId, peerSdmId, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }

    /// <inheritdoc/>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    await ReconcileResidentsAsync(stoppingToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    LogReconcileFailed(ex);
                }
                await Task.Delay(ReconcileInterval, timeProvider, stoppingToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        finally
        {
            await TearDownAllAsync().ConfigureAwait(false);
        }
    }

    private async Task<PortHailStatus> RunHailAsync(
        ITuningLink link, string callsign, string portId, string peer, CancellationToken cancellationToken)
    {
        LogHailing(portId, peer);
        return await HailOverLinkAsync(
            link, callsign, NodeHailerOptions, line => LogSdm(portId, line), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Test seam (InternalsVisibleTo <c>Packet.Node.Tests</c>): run a hail over an already-built link
    /// and project the reply - the hail → <see cref="PortHailStatus"/> / <see cref="HailException"/>
    /// mapping, without a live port or radio.
    /// </summary>
    internal static async Task<PortHailStatus> HailOverLinkAsync(
        ITuningLink link, string callsign, StationHailerOptions options, Action<string>? log, CancellationToken cancellationToken)
    {
        await using var hailer = new StationHailer(link, callsign, options) { Log = log };
        var result = await hailer.HailAsync(cancellationToken).ConfigureAwait(false);
        return result.Outcome switch
        {
            StationHailOutcome.Answered => Project(result.Status!),
            StationHailOutcome.NoReply => throw new HailException(HailError.Timeout,
                result.Detail ?? "the peer did not answer the hail"),
            _ => throw new HailException(HailError.LinkFailed,
                result.Detail ?? "the hail could not be delivered over the SDM side channel"),
        };
    }

    private static PortHailStatus Project(StationStatus status) => new(
        status.Callsign,
        status.Mode,
        status.ModeName,
        status.BitRateHz,
        status.Channel,
        status.SupportedModes,
        status.Capabilities,
        status.RssiOfHailDbm);

    private async Task ReconcileResidentsAsync(CancellationToken cancellationToken)
    {
        var supervisor = host.Supervisor;
        if (supervisor is null)
        {
            return;
        }

        // Desired resident responders: radio-attached, hail-responder-enabled, NinoTNC + Tait ports.
        var desired = new Dictionary<string, (RunningPort Running, PortRadioConfig Radio)>(StringComparer.Ordinal);
        foreach (string portId in supervisor.RunningPortIds)
        {
            var running = supervisor.GetPort(portId);
            // The port's config baseline lives on the supervisor's port owner, not on the
            // RunningPort - there is exactly one answer to "what is this port running on" (#722).
            var radioConfig = supervisor.GetPortConfig(portId)?.Radio;
            if (running is not null && RadioControls.LiveTait(running.Radio) is not null && running.NinoTnc is not null &&
                radioConfig is { HailResponder: true, HailResponderPeer.Length: TaitSdmSideChannel.IdentityLength })
            {
                desired[portId] = (running, radioConfig);
            }
        }

        // Stop responders no longer wanted - port gone / disabled, peer changed, or the radio handle
        // was replaced (a port restart reopens the radio - and a head-end-bound radio's reconnect
        // facade swaps its inner driver on a fault (#576) - so a same-peer resident bound to the OLD
        // handle is now dead and must be rebuilt against the new one). Compare against the LIVE
        // driver behind the stable facade, not the facade itself.
        foreach (var (portId, resident) in residents.ToArray())
        {
            bool stillWanted = desired.TryGetValue(portId, out var wanted)
                && string.Equals(wanted.Radio.HailResponderPeer, resident.Peer, StringComparison.Ordinal)
                && ReferenceEquals(RadioControls.LiveTait(wanted.Running.Radio), resident.Radio);
            if (!stillWanted)
            {
                await StopResidentAsync(portId, force: false).ConfigureAwait(false);
            }
        }

        // Forget the failure/backoff state of ports that are no longer wanted, so a port that
        // comes back (re-enabled, reconfigured) starts from a clean slate.
        foreach (string portId in residentFailures.Keys)
        {
            if (!desired.ContainsKey(portId))
            {
                residentFailures.TryRemove(portId, out _);
            }
        }

        // Start responders newly wanted.
        foreach (var (portId, wanted) in desired)
        {
            if (residents.ContainsKey(portId) || !ResidentAttemptDue(portId))
            {
                continue;
            }
            if (RadioControls.LiveTait(wanted.Running.Radio) is { } tait && wanted.Running.NinoTnc is not null)
            {
                await StartResidentAsync(portId, wanted.Radio.HailResponderPeer, tait, wanted.Running.NinoTnc, cancellationToken)
                    .ConfigureAwait(false);
            }
        }
    }

    private async Task StartResidentAsync(
        string portId, string peer, TaitCcdiRadio tait, Kiss.NinoTnc.NinoTncSerialPort tnc, CancellationToken cancellationToken)
    {
        var gate = portLocks.GetOrAdd(portId, _ => new SemaphoreSlim(1, 1));
        if (!await gate.WaitAsync(0, cancellationToken).ConfigureAwait(false))
        {
            return; // busy (a hail is in flight) - try again next cycle
        }
        try
        {
            if (residents.ContainsKey(portId))
            {
                return;
            }
            await tait.SetProgressMessagesAsync(true, cancellationToken).ConfigureAwait(false);
            try
            {
                await PreflightSdmEnabledAsync(tait, cancellationToken).ConfigureAwait(false);
            }
            catch (HailException ex)
            {
                // A responder that cannot reply is pointless. Record the reason (logged only
                // when it CHANGES) and back off, so a radio with SDM disabled costs one
                // warning and a widening retry, not a warning every 10 s forever.
                NoteResidentSkipped(portId, ex.Message);
                return;
            }

            var link = new FanOutTuningLink(SdmTuningLink.Create(tait, peer, extendedSdm: true));
            var provider = new NinoTncStationStatusSource(tnc, tait, ResolveCallsign());
            var responder = new StationHailResponder(link, provider)
            {
                Log = line => LogSdm(portId, line),
            };
            var cts = new CancellationTokenSource();
            var task = responder.RunAsync(cts.Token);
            residents[portId] = new ResidentResponder(peer, tait, link, cts, task);
            ClearResidentFailure(portId);
            LogResidentArmed(portId, peer);
        }
        catch (Exception ex) when (ex is TaitCcdiException or IOException or InvalidOperationException)
        {
            NoteResidentStartFailed(portId, ex);
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>
    /// Is this port due another resident-start attempt? A port whose last attempt failed waits
    /// out its backoff instead of re-probing the radio every reconcile cycle. Internal as the
    /// deterministic test seam (InternalsVisibleTo <c>Packet.Node.Tests</c>).
    /// </summary>
    internal bool ResidentAttemptDue(string portId) =>
        !residentFailures.TryGetValue(portId, out var failure) || timeProvider.GetUtcNow() >= failure.NextAttempt;

    /// <summary>
    /// Record a resident-start refusal (the SDM preflight said no) and log it ONLY when the
    /// reason changed, the transition-logging convention <c>HeadEndHealthMonitor</c> uses, so a
    /// standing fault is one warning, not one per cycle. Internal test seam.
    /// </summary>
    internal void NoteResidentSkipped(string portId, string reason)
    {
        if (RecordResidentFailure(portId, reason, out var retry))
        {
            LogResidentSkipped(portId, reason, (int)retry.TotalSeconds);
        }
    }

    /// <summary>
    /// Record a resident-start fault (radio/IO) and log it only when the reason changed.
    /// Internal test seam.
    /// </summary>
    internal void NoteResidentStartFailed(string portId, Exception ex)
    {
        if (RecordResidentFailure(portId, $"{ex.GetType().Name}: {ex.Message}", out var retry))
        {
            LogResidentStartFailed(ex, portId, (int)retry.TotalSeconds);
        }
    }

    /// <summary>Forget a port's failure state: the responder armed, so the next fault starts
    /// from a clean slate (first reason logs, backoff restarts). Internal test seam.</summary>
    internal void ClearResidentFailure(string portId) => residentFailures.TryRemove(portId, out _);

    // Update the port's failure state; returns true when this reason is NEW (i.e. worth a log
    // line). The retry interval doubles per consecutive failure from the reconcile cadence up
    // to MaxRetryInterval. Called only from the reconcile loop (and the tests), so the
    // read-modify-write needs no extra interlocking.
    private bool RecordResidentFailure(string portId, string reason, out TimeSpan retry)
    {
        if (!residentFailures.TryGetValue(portId, out var failure))
        {
            failure = new ResidentFailure();
            residentFailures[portId] = failure;
        }
        bool changed = failure.Count == 0 || !string.Equals(failure.Reason, reason, StringComparison.Ordinal);
        failure.Reason = reason;
        failure.Count++;
        retry = RetryInterval(failure.Count);
        failure.NextAttempt = timeProvider.GetUtcNow() + retry;
        return changed;
    }

    // 10 s, 20 s, 40 s ... capped at MaxRetryInterval. The exponent is clamped too, so a port
    // that has been failing all day cannot push the doubling to infinity.
    private static TimeSpan RetryInterval(int consecutiveFailures)
    {
        double seconds = ReconcileInterval.TotalSeconds * Math.Pow(2, Math.Min(consecutiveFailures - 1, 10));
        return TimeSpan.FromSeconds(Math.Min(seconds, MaxRetryInterval.TotalSeconds));
    }

    /// <summary>One port's standing resident-start failure: the last reason (compared so only a
    /// CHANGE logs), how many consecutive failures (drives the backoff), and when the next
    /// attempt is due.</summary>
    private sealed class ResidentFailure
    {
        public string Reason { get; set; } = string.Empty;

        public int Count { get; set; }

        public DateTimeOffset NextAttempt { get; set; }
    }

    /// <summary>Stop and dispose a port's resident responder under its lock. When
    /// <paramref name="force"/> is false the stop is skipped if a hail holds the lock (retried next
    /// reconcile cycle); on shutdown <paramref name="force"/> waits the lock out.</summary>
    private async Task StopResidentAsync(string portId, bool force)
    {
        var gate = portLocks.GetOrAdd(portId, _ => new SemaphoreSlim(1, 1));
        if (force)
        {
            await gate.WaitAsync().ConfigureAwait(false);
        }
        else if (!await gate.WaitAsync(0).ConfigureAwait(false))
        {
            return; // a hail is in flight on this port - leave the responder; retry next cycle
        }
        try
        {
            if (residents.TryRemove(portId, out var resident))
            {
                await resident.DisposeAsync().ConfigureAwait(false);
                LogResidentStopped(portId);
            }
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task TearDownAllAsync()
    {
        foreach (var (portId, _) in residents.ToArray())
        {
            await StopResidentAsync(portId, force: true).ConfigureAwait(false);
        }
    }

    private static async Task PreflightSdmEnabledAsync(TaitCcdiRadio radio, CancellationToken cancellationToken)
    {
        try
        {
            await radio.SendSdmAsync("********", "PDNHAIL", leadInDelay: null, cancellationToken).ConfigureAwait(false);
        }
        catch (TaitCcdiException ex) when (ex.Error is { Category: '0', ErrorNumber: 0x06 })
        {
            throw new HailException(HailError.BadRequest,
                "SDM is disabled in the radio's programming - enable SDM + auto-acknowledgements with the Tait programming app");
        }
    }

    private string ResolveCallsign()
    {
        string? callsign = config.Current.Identity.Callsign;
        return string.IsNullOrWhiteSpace(callsign) ? "N0CALL" : callsign;
    }

    /// <summary>One armed resident responder: its shared link + running loop.</summary>
    private sealed class ResidentResponder(
        string peer, TaitCcdiRadio radio, FanOutTuningLink link, CancellationTokenSource cts, Task task)
        : IAsyncDisposable
    {
        public string Peer { get; } = peer;

        /// <summary>The radio handle this responder is bound to - compared on reconcile so a port
        /// restart (which reopens the radio) rebuilds the responder against the fresh handle.</summary>
        public TaitCcdiRadio Radio { get; } = radio;

        public FanOutTuningLink Link { get; } = link;

        public async ValueTask DisposeAsync()
        {
            await cts.CancelAsync().ConfigureAwait(false);
            try
            {
                await task.ConfigureAwait(false);
            }
            catch
            {
            }
            await Link.DisposeAsync().ConfigureAwait(false);
            cts.Dispose();
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "hail[{Port}] hailing {Peer} over the SDM side channel")]
    private partial void LogHailing(string port, string peer);

    [LoggerMessage(Level = LogLevel.Information, Message = "hail[{Port}] resident responder armed (answers {Peer})")]
    private partial void LogResidentArmed(string port, string peer);

    [LoggerMessage(Level = LogLevel.Information, Message = "hail[{Port}] resident responder stopped")]
    private partial void LogResidentStopped(string port);

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "hail[{Port}] resident responder not armed: {Reason} (retrying in {RetrySeconds}s)")]
    private partial void LogResidentSkipped(string port, string reason, int retrySeconds);

    [LoggerMessage(Level = LogLevel.Error,
        Message = "hail[{Port}] resident responder failed to start (retrying in {RetrySeconds}s)")]
    private partial void LogResidentStartFailed(Exception ex, string port, int retrySeconds);

    [LoggerMessage(Level = LogLevel.Debug, Message = "hail[{Port}] sdm-link: {Line}")]
    private partial void LogSdm(string port, string line);

    [LoggerMessage(Level = LogLevel.Error, Message = "hail responder reconcile failed")]
    private partial void LogReconcileFailed(Exception ex);
}
