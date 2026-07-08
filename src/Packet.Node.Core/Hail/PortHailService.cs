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
///     peer's <see cref="PortHailStatus"/> — its callsign, current NinoTNC mode/bitrate, channel and
///     capabilities. Because the hail rides the radio's own FFSK modem, it works (and reports the
///     peer's mode) even when the packet path is broken by a mode mismatch.</item>
///   <item><b>Resident responder</b> (opt-in per port via <see cref="PortRadioConfig.HailResponder"/>):
///     listen for a configured neighbour's hails and auto-reply with this node's status.</item>
/// </list>
/// A DI singleton and a hosted service — its background loop reconciles the resident responders
/// against the running ports + config; disposing it tears every responder down.
/// </summary>
/// <remarks>
/// <para><b>One radio, one SDM buffer.</b> A radio's SDM receive buffer is one-deep, so a port has a
/// single side-channel consumer at a time. When a resident responder is armed it owns a shared
/// <see cref="FanOutTuningLink"/> (bound to its configured neighbour), and an on-demand hail borrows
/// that shared link — but only to the same peer (v1 is point-to-point; hailing a different peer
/// while a responder is bound is refused). With no resident responder, a hail opens a transient link
/// to the requested peer for the duration of the call.</para>
/// <para>The SDM-enabled fail-fast preflight (the capability doctor's wildcard-SDM probe) runs on the
/// transient path; on the shared path the responder's link already proved SDM works.</para>
/// </remarks>
public sealed partial class PortHailService : BackgroundService
{
    private static readonly TimeSpan ReconcileInterval = TimeSpan.FromSeconds(10);

    private static readonly StationHailerOptions NodeHailerOptions = new()
    {
        MaxAttempts = 2,
        ReplyTimeout = TimeSpan.FromSeconds(20),
    };

    private readonly NodeHostedService host;
    private readonly IConfigProvider config;
    private readonly ILogger<PortHailService> logger;
    private readonly ConcurrentDictionary<string, ResidentResponder> residents = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, SemaphoreSlim> portLocks = new(StringComparer.Ordinal);

    /// <summary>Create the service.</summary>
    public PortHailService(NodeHostedService host, IConfigProvider config, ILogger<PortHailService> logger)
    {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(logger);
        this.host = host;
        this.config = config;
        this.logger = logger;
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
                "this port has no Tait CCDI radio attached — a hail needs the radio's SDM side channel");
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
                await Task.Delay(ReconcileInterval, stoppingToken).ConfigureAwait(false);
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
    /// and project the reply — the hail → <see cref="PortHailStatus"/> / <see cref="HailException"/>
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
        var desired = new Dictionary<string, RunningPort>(StringComparer.Ordinal);
        foreach (string portId in supervisor.RunningPortIds)
        {
            var running = supervisor.GetPort(portId);
            if (running is not null && RadioControls.LiveTait(running.Radio) is not null && running.NinoTnc is not null &&
                running.Config.Radio is { HailResponder: true, HailResponderPeer.Length: TaitSdmSideChannel.IdentityLength })
            {
                desired[portId] = running;
            }
        }

        // Stop responders no longer wanted — port gone / disabled, peer changed, or the radio handle
        // was replaced (a port restart reopens the radio — and a head-end-bound radio's reconnect
        // facade swaps its inner driver on a fault (#576) — so a same-peer resident bound to the OLD
        // handle is now dead and must be rebuilt against the new one). Compare against the LIVE
        // driver behind the stable facade, not the facade itself.
        foreach (var (portId, resident) in residents.ToArray())
        {
            bool stillWanted = desired.TryGetValue(portId, out var running)
                && string.Equals(running!.Config.Radio!.HailResponderPeer, resident.Peer, StringComparison.Ordinal)
                && ReferenceEquals(RadioControls.LiveTait(running.Radio), resident.Radio);
            if (!stillWanted)
            {
                await StopResidentAsync(portId, force: false).ConfigureAwait(false);
            }
        }

        // Start responders newly wanted.
        foreach (var (portId, running) in desired)
        {
            if (residents.ContainsKey(portId))
            {
                continue;
            }
            if (RadioControls.LiveTait(running.Radio) is { } tait && running.NinoTnc is not null)
            {
                await StartResidentAsync(portId, running.Config.Radio!.HailResponderPeer, tait, running.NinoTnc, cancellationToken)
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
            return; // busy (a hail is in flight) — try again next cycle
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
                LogResidentSkipped(portId, ex.Message);
                return; // a responder that cannot reply is pointless — try again next cycle
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
            LogResidentArmed(portId, peer);
        }
        catch (Exception ex) when (ex is TaitCcdiException or IOException or InvalidOperationException)
        {
            LogResidentStartFailed(ex, portId);
        }
        finally
        {
            gate.Release();
        }
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
            return; // a hail is in flight on this port — leave the responder; retry next cycle
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
                "SDM is disabled in the radio's programming — enable SDM + auto-acknowledgements with the Tait programming app");
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

        /// <summary>The radio handle this responder is bound to — compared on reconcile so a port
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

    [LoggerMessage(Level = LogLevel.Warning, Message = "hail[{Port}] resident responder not armed: {Reason}")]
    private partial void LogResidentSkipped(string port, string reason);

    [LoggerMessage(Level = LogLevel.Error, Message = "hail[{Port}] resident responder failed to start")]
    private partial void LogResidentStartFailed(Exception ex, string port);

    [LoggerMessage(Level = LogLevel.Debug, Message = "hail[{Port}] sdm-link: {Line}")]
    private partial void LogSdm(string port, string line);

    [LoggerMessage(Level = LogLevel.Error, Message = "hail responder reconcile failed")]
    private partial void LogReconcileFailed(Exception ex);
}
