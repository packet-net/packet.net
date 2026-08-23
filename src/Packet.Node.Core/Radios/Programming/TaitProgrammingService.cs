using System.Collections.Concurrent;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using Packet.Node.Core.Configuration;
using Packet.Node.Core.Hosting;

namespace Packet.Node.Core.Radios.Programming;

/// <summary>Why a programming run was refused.</summary>
public enum TaitProgramStartError
{
    /// <summary>The port id is unknown (→ HTTP 404).</summary>
    NotFound,

    /// <summary>The request is malformed, or the port cannot host a run - no radio, the wrong kind
    /// of radio, or one this node cannot reach a programming interface on (→ HTTP 400).</summary>
    BadRequest,

    /// <summary>The port is already busy - a run or a tuning session holds it (→ HTTP 409).</summary>
    Conflict,
}

/// <summary>A programming run was refused; <see cref="Error"/> classifies it for the API.</summary>
public sealed class TaitProgramStartException : Exception
{
    /// <summary>Create with a classification and an operator-facing reason.</summary>
    public TaitProgramStartException(TaitProgramStartError error, string message)
        : base(message)
    {
        Error = error;
    }

    /// <summary>Parameterless form (framework convention).</summary>
    public TaitProgramStartException()
    {
    }

    /// <summary>Create with a message only (defaults to <see cref="TaitProgramStartError.BadRequest"/>).</summary>
    public TaitProgramStartException(string message)
        : base(message)
    {
    }

    /// <summary>Create with a message and inner exception (framework convention).</summary>
    public TaitProgramStartException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    /// <summary>The refusal classification.</summary>
    public TaitProgramStartError Error { get; }
}

/// <summary>
/// The node's Tait codeplug-programming service (packet-net/packet.net#779): at most one run per
/// port, each one taking its port out of service, driving the attached radio's programming
/// interface, and putting the port back. A DI singleton; disposing it cancels every live run, which
/// restores every port it holds.
/// </summary>
/// <remarks>
/// <para>
/// <b>Locally-cabled radios only.</b> The programming handshake is a boot-time latch: the radio has
/// to be power-cycled while the node probes for its banner, and the line has to be re-clocked to
/// 19200 for the transfer. A head-end-bound radio (the split-station topology) is refused rather
/// than half-supported - its device is at the far end of a TCP bridge, and nothing here has been
/// exercised against one. A <c>rig</c>-kind radio has no Tait programming interface at all.
/// </para>
/// <para>
/// <b>The finished run stays.</b> A terminal run is kept on its port until the next one replaces it,
/// so the operator can still read the feed - and the failure reason - after it has ended. Starting a
/// new run on a port whose last one has finished simply supersedes it.
/// </para>
/// </remarks>
public sealed partial class TaitProgrammingService : IAsyncDisposable
{
    private readonly ITaitProgrammingGateway gateway;
    private readonly ITaitCodeplugWriter writer;
    private readonly Func<string, bool> portBusy;
    private readonly string? backupDirectory;
    private readonly ILogger<TaitProgrammingService> logger;
    private readonly TimeProvider clock;
    private readonly ConcurrentDictionary<string, TaitProgrammingSession> runs = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim startGate = new(1, 1);
    private int disposed;

    /// <summary>Create the service.</summary>
    /// <param name="host">The node host (supervisor access + the exclusive gate for port down/up).</param>
    /// <param name="logger">Logger for run failures / diagnostics.</param>
    /// <param name="backupDirectory">Where each run snapshots the pre-change codeplug (a
    /// <c>.m8p</c> per run), or null to skip the snapshot.</param>
    /// <param name="portBusy">Optional: whether some other operator-initiated session already holds
    /// a port (the tuning registry). A busy port is refused rather than yanked out from under it.</param>
    /// <param name="clock">Time source for run timestamps; null = system.</param>
    public TaitProgrammingService(
        NodeHostedService host,
        ILogger<TaitProgrammingService> logger,
        string? backupDirectory = null,
        Func<string, bool>? portBusy = null,
        TimeProvider? clock = null)
        : this(NodeHostProgrammingGateway.For(host, logger), TaitCodeplugWriter.Instance, logger, backupDirectory, portBusy, clock)
    {
    }

    /// <summary>
    /// Test seam (InternalsVisibleTo <c>Packet.Node.Tests</c>): the same service over a fake gateway
    /// and a scripted codeplug writer, so the whole preflight / port-down / program / port-back
    /// orchestration is drivable without a node host, a port or a radio.
    /// </summary>
    /// <param name="gateway">Node-host operations (production: the host's supervisor).</param>
    /// <param name="writer">The hardware seam (production: <see cref="TaitCodeplugWriter"/>).</param>
    /// <param name="logger">Logger for run failures / diagnostics.</param>
    /// <param name="backupDirectory">Where each run snapshots the pre-change codeplug, or null.</param>
    /// <param name="portBusy">Whether another session holds a port; null = nothing else does.</param>
    /// <param name="clock">Time source for run timestamps; null = system.</param>
    internal TaitProgrammingService(
        ITaitProgrammingGateway gateway,
        ITaitCodeplugWriter writer,
        ILogger<TaitProgrammingService> logger,
        string? backupDirectory = null,
        Func<string, bool>? portBusy = null,
        TimeProvider? clock = null)
    {
        ArgumentNullException.ThrowIfNull(gateway);
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(logger);
        this.gateway = gateway;
        this.writer = writer;
        this.logger = logger;
        this.backupDirectory = backupDirectory;
        this.portBusy = portBusy ?? (_ => false);
        this.clock = clock ?? TimeProvider.System;
    }

    /// <summary>
    /// Start a run on <paramref name="portId"/>. Returns as soon as the run is accepted and
    /// registered - the work itself is minutes long and is watched on the event feed.
    /// </summary>
    /// <param name="portId">The port whose attached radio to program.</param>
    /// <param name="request">What to write.</param>
    /// <param name="cancellationToken">Abandons the <em>start</em> (not the run it starts).</param>
    /// <exception cref="TaitProgramStartException">The run was refused; see
    /// <see cref="TaitProgramStartException.Error"/>.</exception>
    public async Task<TaitProgramInfo> StartAsync(
        string portId, TaitProgramRequest? request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(portId);
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);

        if (!TaitProgramPlan.TryParse(request, out var plan, out string parseError))
        {
            throw new TaitProgramStartException(TaitProgramStartError.BadRequest, parseError);
        }

        await startGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var radio = Preflight(portId);

            // The device path is free while the port is still up: the live CCDI driver knows exactly
            // which device it is talking to. A port-bound radio's config answers even when the port
            // is down. Only a serial-bound radio on a stopped port is left to a scan, which the run
            // does after the teardown (a scan cannot see a device the node holds open).
            string? devicePath = gateway.LiveRadioDevicePath(portId)
                ?? (string.IsNullOrWhiteSpace(radio.Port) ? null : radio.Port);

            var session = new TaitProgrammingSession(
                portId, plan, radio, devicePath, gateway, writer, backupDirectory, clock);
            // Preflight has already established that anything here is terminal, so superseding it
            // is safe - and releases its cancellation source.
            if (runs.TryRemove(portId, out var previous))
            {
                previous.Dispose();
            }

            runs[portId] = session;
            session.Start();
            string summary = plan.ToString();
            LogRunStarted(portId, devicePath ?? "(to be located)", summary);
            return session.Info;
        }
        finally
        {
            startGate.Release();
        }
    }

    /// <summary>The run on a port - live or the last one that finished - or null when there has been
    /// none since this node started.</summary>
    public TaitProgramInfo? Get(string portId)
    {
        ArgumentNullException.ThrowIfNull(portId);
        return runs.TryGetValue(portId, out var session) ? session.Info : null;
    }

    /// <summary>
    /// Subscribe to a port's run feed: every event so far, then live ones (a finished run replays
    /// its history and completes the reader immediately). Returns null - leaving
    /// <paramref name="reader"/> unset - when there has been no run on this port, which the API maps
    /// to a 404.
    /// </summary>
    /// <param name="portId">The port whose run to watch.</param>
    /// <param name="reader">The channel to read <see cref="TaitProgramEvent"/>s from.</param>
    /// <returns>A subscription to dispose when the client goes away, or null.</returns>
    public IDisposable? Subscribe(string portId, out ChannelReader<TaitProgramEvent> reader)
    {
        ArgumentNullException.ThrowIfNull(portId);
        if (!runs.TryGetValue(portId, out var session))
        {
            reader = Channel.CreateUnbounded<TaitProgramEvent>().Reader;
            return null;
        }

        return session.Subscribe(out reader);
    }

    /// <summary>
    /// Cancel the live run on a port. Returns false when there is none (or it has already finished).
    /// Resolves once the run has ended and the port is back in service.
    /// </summary>
    /// <param name="portId">The port whose run to abandon.</param>
    public async Task<bool> CancelAsync(string portId)
    {
        ArgumentNullException.ThrowIfNull(portId);
        if (!runs.TryGetValue(portId, out var session) || session.IsTerminal)
        {
            return false;
        }

        await session.CancelAsync().ConfigureAwait(false);
        return true;
    }

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0)
        {
            return;
        }

        foreach (var session in runs.Values.ToArray())
        {
            try
            {
                await session.CancelAsync().ConfigureAwait(false);
                session.Dispose();
            }
            catch (Exception ex)
            {
                // Shutdown must not be blocked by a run that will not die; the port restore is in
                // the run's own finally, and the supervisor's shutdown tears everything down anyway.
                LogCancelFailed(session.PortId, ex.Message);
            }
        }

        startGate.Dispose();
    }

    /// <summary>The checks a run has to pass before a port is touched, returning the radio block it
    /// will program.</summary>
    private PortRadioConfig Preflight(string portId)
    {
        var port = gateway.GetPortConfig(portId)
            ?? throw new TaitProgramStartException(
                TaitProgramStartError.NotFound, $"no port '{portId}' in this node's config");

        if (port.Radio is not { } radio)
        {
            throw new TaitProgramStartException(
                TaitProgramStartError.BadRequest,
                $"port '{portId}' has no radio attached - turn Radio control on and save the port first");
        }

        if (!RadioKinds.Is(radio.Kind, RadioKinds.TaitCcdi))
        {
            throw new TaitProgramStartException(
                TaitProgramStartError.BadRequest,
                $"port '{portId}' has a '{radio.Kind}' radio; codeplug programming is a Tait TM8100/TM8200 (tait-ccdi) operation");
        }

        if (radio.IsHeadEndBound)
        {
            throw new TaitProgramStartException(
                TaitProgramStartError.BadRequest,
                $"the radio on port '{portId}' lives on head-end '{radio.HeadEndId}'. Programming latches the radio at " +
                "boot over a directly-cabled serial line, so it has to be done from the machine the radio is plugged " +
                "into - or with the tait-codeplug CLI at the head-end.");
        }

        if (string.IsNullOrWhiteSpace(radio.Serial) && string.IsNullOrWhiteSpace(radio.Port))
        {
            throw new TaitProgramStartException(
                TaitProgramStartError.BadRequest,
                $"the radio on port '{portId}' is bound to neither a CCDI serial nor a device path, so there is " +
                "nothing to program");
        }

        if (runs.TryGetValue(portId, out var existing) && !existing.IsTerminal)
        {
            throw new TaitProgramStartException(
                TaitProgramStartError.Conflict, $"port '{portId}' is already being programmed");
        }

        if (portBusy(portId))
        {
            throw new TaitProgramStartException(
                TaitProgramStartError.Conflict,
                $"port '{portId}' is busy with a tuning session - stop it first");
        }

        return radio;
    }

    [LoggerMessage(EventId = 7790, Level = LogLevel.Information,
        Message = "port {PortId}: programming the attached Tait on {DevicePath} ({Plan})")]
    private partial void LogRunStarted(string portId, string devicePath, string plan);

    [LoggerMessage(EventId = 7791, Level = LogLevel.Warning,
        Message = "port {PortId}: could not cancel the codeplug programming run cleanly: {Reason}")]
    private partial void LogCancelFailed(string portId, string reason);
}
