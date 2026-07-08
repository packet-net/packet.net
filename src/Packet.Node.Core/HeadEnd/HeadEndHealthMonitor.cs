using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Packet.Node.Core.Configuration;

namespace Packet.Node.Core.HeadEnd;

/// <summary>
/// The background head-end fleet health poller (#583): every <see cref="DefaultPollInterval"/> it
/// resolves each configured/referenced head-end instance's address (config wins; else one bounded
/// mDNS browse shared across the cycle — the same config-else-discovery rule as
/// <see cref="HeadEndAddressResolver"/>) and probes its HTTP control plane: <c>GET /statusz</c>
/// (headend-v0.1.4+, #587) for the rich shape, falling back to the bare <c>GET /healthz</c> on a
/// 404 from an older daemon. The rolling per-instance snapshot (reachable, bridge count, per-bridge
/// client-connection, last-seen, failure counters) feeds the <c>pdn_headend_*</c> metrics bucket and
/// the <c>GET /api/v1/radios/headends</c> live-health enrichment — both read the in-memory snapshot,
/// never probing on the request path.
/// </summary>
/// <remarks>
/// <para>
/// <b>Self-gating.</b> The service is registered unconditionally but does nothing on a node with no
/// head-ends: each cycle re-reads <see cref="IConfigProvider.Current"/> and polls the union of
/// declared head-ends (<see cref="NodeConfig.HeadEnds"/>) and head-ends referenced by a port binding
/// (a <c>nino-tnc-tcp</c> transport or a head-end-bound <c>radio:</c>), so a runtime adopt starts
/// health coverage on the next cycle and a purely-local node never browses or dials anything.
/// </para>
/// <para>
/// <b>Quiet by design.</b> State TRANSITIONS are logged (reachable→unreachable as a WARNING,
/// recovery as Information — the <see cref="Transports.ReconnectingKissModem"/> 5101/5102
/// convention), never per-poll results; a fleet that is down stays one warning per outage, not one
/// per 30 s. The poll is pure HTTP GET against the control plane — it never opens device pipes and
/// never touches the scan/keyup probe gate.
/// </para>
/// </remarks>
public sealed partial class HeadEndHealthMonitor : BackgroundService
{
    /// <summary>Default cadence of the fleet poll. ~30 s keeps the reachable badge and metrics
    /// honest without loading a Pi Zero's control plane.</summary>
    public static readonly TimeSpan DefaultPollInterval = TimeSpan.FromSeconds(30);

    private readonly IConfigProvider config;
    private readonly IHeadEndDiscovery discovery;
    private readonly Func<Uri, HeadEndClient> clientFactory;
    private readonly TimeProvider timeProvider;
    private readonly TimeSpan pollInterval;
    private readonly TimeSpan discoveryTimeout;
    private readonly ILogger<HeadEndHealthMonitor> logger;

    // Per-instance rolling state, keyed by instance id. Mutated only by the poll loop; read by
    // Snapshot() (metrics scrape / API request threads) — every access goes through the gate.
    private readonly object gate = new();
    private readonly Dictionary<string, InstanceState> states = new(StringComparer.Ordinal);

    /// <summary>Build the monitor. <paramref name="clientFactory"/> builds a
    /// <see cref="HeadEndClient"/> for a resolved base address (default: the real client); tests
    /// point it at a stub statusz server. Interval/timeout defaults are production values.</summary>
    public HeadEndHealthMonitor(
        IConfigProvider config,
        IHeadEndDiscovery discovery,
        Func<Uri, HeadEndClient>? clientFactory = null,
        TimeProvider? timeProvider = null,
        ILoggerFactory? loggerFactory = null,
        TimeSpan? pollInterval = null,
        TimeSpan? discoveryTimeout = null)
    {
        this.config = config ?? throw new ArgumentNullException(nameof(config));
        this.discovery = discovery ?? throw new ArgumentNullException(nameof(discovery));
        this.clientFactory = clientFactory ?? (uri => new HeadEndClient(uri));
        this.timeProvider = timeProvider ?? TimeProvider.System;
        this.pollInterval = pollInterval is { } p && p > TimeSpan.Zero ? p : DefaultPollInterval;
        this.discoveryTimeout = discoveryTimeout is { } d && d > TimeSpan.Zero
            ? d
            : HeadEndAddressResolver.DefaultDiscoveryTimeout;
        logger = (loggerFactory ?? NullLoggerFactory.Instance).CreateLogger<HeadEndHealthMonitor>();
    }

    /// <summary>
    /// The current fleet health snapshot: one row per monitored instance that has completed at least
    /// one poll, ordered by instance id. Empty on a node with no head-ends (or before the first
    /// cycle finishes). Pure in-memory read — safe on the metrics/API request path.
    /// </summary>
    public IReadOnlyList<HeadEndHealth> Snapshot()
    {
        lock (gate)
        {
            return states
                .Where(kv => kv.Value.Reachable is not null)
                .OrderBy(kv => kv.Key, StringComparer.Ordinal)
                .Select(kv => kv.Value.ToHealth(kv.Key))
                .ToList();
        }
    }

    /// <inheritdoc/>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await PollOnceAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                // Defensive: per-instance failures are absorbed inside the cycle, so this is an
                // unexpected cycle-level fault (e.g. a discovery backend throw). Log and keep
                // polling — fleet observability must not die quietly.
                LogPollCycleFaulted(ex);
            }

            try
            {
                await Task.Delay(pollInterval, timeProvider, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    /// <summary>
    /// Run one full fleet poll cycle. Internal as the deterministic test seam (InternalsVisibleTo
    /// <c>Packet.Node.Tests</c>) — tests drive cycles directly instead of racing the timer loop.
    /// </summary>
    internal async Task PollOnceAsync(CancellationToken cancellationToken)
    {
        var current = config.Current;
        var ids = MonitoredIds(current);

        // Self-gate + prune: keep state only for currently-configured/referenced instances, so a
        // head-end removed from config stops emitting metrics rows instead of going stale-forever.
        lock (gate)
        {
            foreach (var stale in states.Keys.Where(k => !ids.Contains(k)).ToList())
            {
                states.Remove(stale);
            }
        }
        if (ids.Count == 0)
        {
            return;
        }

        // One bounded browse per cycle, and only when some instance actually needs discovery (a
        // blank config address). Config-pinned fleets never multicast.
        IReadOnlyList<DiscoveredHeadEnd> discovered = [];
        if (ids.Any(id => PinnedAddress(current, id) is null))
        {
            discovered = await discovery.DiscoverAsync(discoveryTimeout, cancellationToken).ConfigureAwait(false);
        }

        foreach (var id in ids)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await PollInstanceAsync(current, id, discovered, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task PollInstanceAsync(
        NodeConfig current, string id, IReadOnlyList<DiscoveredHeadEnd> discovered, CancellationToken cancellationToken)
    {
        var baseAddress = ResolveBaseAddress(current, id, discovered);
        if (baseAddress is null)
        {
            RecordFailure(id, "address not resolved (blank config address, not discovered or duplicated)");
            return;
        }

        var client = clientFactory(baseAddress);
        try
        {
            var status = await client.GetStatusAsync(cancellationToken).ConfigureAwait(false);
            RecordSuccess(id, status.BridgeCount,
                status.Bridges.Select(b => new HeadEndBridgeHealth(b.Id, b.TcpPort, b.ClientConnected)).ToList());
            return;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (HttpRequestException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            // Pre-0.1.4 daemon: no /statusz. Fall through to the bare liveness probe below.
        }
        catch (Exception ex)
        {
            RecordFailure(id, ex.Message);
            return;
        }

        // /healthz fallback (older daemon): reachable yes/no, bridge shape unknown.
        try
        {
            if (await client.HealthAsync(cancellationToken).ConfigureAwait(false))
            {
                RecordSuccess(id, bridgeCount: null, bridges: []);
            }
            else
            {
                RecordFailure(id, "healthz answered non-success");
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            RecordFailure(id, ex.Message);
        }
    }

    // The union of declared head-ends and head-ends a port binding references (a nino-tnc-tcp
    // transport or a head-end-bound radio) — normally identical (the validator requires references
    // to be declared), but the union keeps the monitor honest over any config the node runs.
    private static List<string> MonitoredIds(NodeConfig current)
    {
        var ids = new List<string>();
        void Add(string? id)
        {
            if (!string.IsNullOrWhiteSpace(id) && !ids.Contains(id))
            {
                ids.Add(id);
            }
        }

        foreach (var h in current.HeadEnds)
        {
            Add(h.Id);
        }
        foreach (var port in current.Ports)
        {
            if (port.Transport is NinoTncTcpTransport t)
            {
                Add(t.HeadEndId);
            }
            if (port.Radio is { IsHeadEndBound: true } radio)
            {
                Add(radio.HeadEndId);
            }
        }
        return ids;
    }

    // Config address wins (the operator resolved it by hand); else exactly one discovery match.
    // Zero matches or a duplicate-id clash resolves to null — the caller records an unreachable
    // poll, and the loud conflict logging stays with the resolver/scan surfaces that own it.
    private static Uri? ResolveBaseAddress(NodeConfig current, string id, IReadOnlyList<DiscoveredHeadEnd> discovered)
    {
        if (PinnedAddress(current, id) is { } pinned)
        {
            return pinned;
        }

        var matches = discovered
            .Where(d => string.Equals(d.InstanceId, id, StringComparison.Ordinal))
            .GroupBy(d => d.Address, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .ToList();
        return matches.Count == 1
            ? new Uri($"http://{matches[0].Host}:{matches[0].HttpPort}/", UriKind.Absolute)
            : null;
    }

    private static Uri? PinnedAddress(NodeConfig current, string id)
    {
        var configured = current.HeadEnds.FirstOrDefault(h => string.Equals(h.Id, id, StringComparison.Ordinal));
        return configured is not null && !string.IsNullOrWhiteSpace(configured.Address)
            && HeadEndAddress.TryParse(configured.Address, out var host, out var port)
            ? new Uri($"http://{host}:{port}/", UriKind.Absolute)
            : null;
    }

    private void RecordSuccess(string id, int? bridgeCount, IReadOnlyList<HeadEndBridgeHealth> bridges)
    {
        int failureStreak;
        bool recovered;
        lock (gate)
        {
            var state = GetState(id);
            recovered = state.Reachable == false;
            failureStreak = state.ConsecutiveFailures;
            state.Reachable = true;
            state.ConsecutiveFailures = 0;
            state.LastSeen = timeProvider.GetUtcNow();
            state.BridgeCount = bridgeCount;
            state.Bridges = bridges;
        }
        if (recovered)
        {
            LogReachableAgain(id, failureStreak);
        }
    }

    private void RecordFailure(string id, string reason)
    {
        bool wentDown;
        lock (gate)
        {
            var state = GetState(id);
            // A first-ever poll that fails is a transition too (unknown → unreachable): a fleet
            // that is down at node start warrants its warning, once.
            wentDown = state.Reachable != false;
            state.Reachable = false;
            state.ConsecutiveFailures++;
            state.PollFailuresTotal++;
        }
        if (wentDown)
        {
            LogUnreachable(id, reason);
        }
    }

    private InstanceState GetState(string id)
    {
        if (!states.TryGetValue(id, out var state))
        {
            state = new InstanceState();
            states[id] = state;
        }
        return state;
    }

    // The mutable per-instance rolling state behind the gate. Reachable is null only before the
    // first poll completes (such instances are withheld from Snapshot()). Bridge shape is the
    // LAST-KNOWN-GOOD (kept across an outage so the UI can still show what was there); LastSeen
    // says how fresh it is.
    private sealed class InstanceState
    {
        public bool? Reachable;
        public int? BridgeCount;
        public IReadOnlyList<HeadEndBridgeHealth> Bridges = [];
        public DateTimeOffset? LastSeen;
        public int ConsecutiveFailures;
        public long PollFailuresTotal;

        public HeadEndHealth ToHealth(string id) => new(
            id, Reachable ?? false, BridgeCount, Bridges, LastSeen, ConsecutiveFailures, PollFailuresTotal);
    }

    [LoggerMessage(EventId = 5301, Level = LogLevel.Warning,
        Message = "Head-end '{InstanceId}' is unreachable ({Reason}); polling continues.")]
    private partial void LogUnreachable(string instanceId, string reason);

    [LoggerMessage(EventId = 5302, Level = LogLevel.Information,
        Message = "Head-end '{InstanceId}' is reachable again (after {Failures} failed polls).")]
    private partial void LogReachableAgain(string instanceId, int failures);

    [LoggerMessage(EventId = 5303, Level = LogLevel.Warning,
        Message = "Head-end health poll cycle faulted unexpectedly; continuing.")]
    private partial void LogPollCycleFaulted(Exception ex);
}

/// <summary>
/// One head-end instance's rolling health in the <see cref="HeadEndHealthMonitor"/> snapshot: the
/// result of the most recent poll plus the last-known bridge shape and failure counters. Feeds the
/// <c>pdn_headend_*</c> metrics and the <c>GET /api/v1/radios/headends</c> live enrichment.
/// </summary>
/// <param name="InstanceId">The head-end's stable instance id.</param>
/// <param name="Reachable">Whether the most recent poll succeeded.</param>
/// <param name="BridgeCount">The bridge (device) count from the last successful <c>/statusz</c>;
/// null when unknown — the daemon predates <c>/statusz</c> (healthz fallback) or has never answered.</param>
/// <param name="Bridges">Per-bridge rows from the last successful <c>/statusz</c> (last-known-good
/// across an outage; empty when unknown).</param>
/// <param name="LastSeen">When the instance last answered a poll, or null if it never has.</param>
/// <param name="ConsecutiveFailures">Failed polls since the last success (0 while reachable).</param>
/// <param name="PollFailuresTotal">Total failed polls since the monitor started tracking the
/// instance — the <c>pdn_headend_poll_failures_total</c> counter source.</param>
public sealed record HeadEndHealth(
    string InstanceId,
    bool Reachable,
    int? BridgeCount,
    IReadOnlyList<HeadEndBridgeHealth> Bridges,
    DateTimeOffset? LastSeen,
    int ConsecutiveFailures,
    long PollFailuresTotal);

/// <summary>One bridge's health row in a <see cref="HeadEndHealth"/>: the device id, its raw-pipe
/// TCP port, and whether a client (normally this node) currently holds the single-client pipe.</summary>
public sealed record HeadEndBridgeHealth(string DeviceId, int TcpPort, bool ClientConnected);
