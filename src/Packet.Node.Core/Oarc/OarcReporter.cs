using System.Globalization;
using System.Threading.Channels;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Packet.Node.Core.Api;
using Packet.Node.Core.Configuration;
using Packet.Node.Core.Telemetry;

namespace Packet.Node.Core.Oarc;

/// <summary>
/// The OARC network-map reporter (#459): a background service that pushes this node's telemetry to the
/// OARC collector so it appears on the map. <b>Outbound only, default-off</b>, self-gating on
/// <see cref="OarcConfig.Enabled"/> + the per-category toggles (so registration is unconditional and a
/// hot-enable needs no restart). See <c>docs/oarc-reporting-design.md</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Policy, not plumbing.</b> The reporter owns <i>when/what</i>: a poll loop captures an
/// <see cref="IOarcStateSource"/> snapshot each tick, diffs links/circuits (by stable id) into
/// up/down/status events, emits node up/status/down on the master edge + the status cadence, and maps
/// the frame trace stream into l2traces (opt-in). The <i>how</i> (HTTP) is the
/// <see cref="IOarcIngestClient"/>. Everything is <see cref="TimeProvider"/>-driven and never throws
/// out of the loop.
/// </para>
/// <para>
/// <b>Best-effort, never load-bearing.</b> Events go through a bounded drop-oldest queue (a dead
/// collector never backpressures the node or grows memory; drops are counted, not silent), drained by a
/// sender that retries transport errors with capped backoff and drops a synchronous rejection (our
/// payload bug). Periodic status re-asserts state, so a dropped event self-heals on the next interval.
/// </para>
/// </remarks>
public sealed partial class OarcReporter : BackgroundService
{
    private const string Software = "pdn";
    private const int QueueCapacity = 1000;

    // The retry backoff schedule for a transport error (429/5xx/network). Length bounds the attempts.
    private static readonly TimeSpan[] Backoff =
    [
        TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(4), TimeSpan.FromSeconds(8),
    ];

    private readonly IConfigProvider config;
    private readonly IOarcStateSource state;
    private readonly IOarcIngestClient client;
    private readonly NodeTelemetry telemetry;
    private readonly TimeProvider clock;
    private readonly string nodeVersion;
    private readonly ILogger<OarcReporter> logger;

    private readonly Channel<OarcEvent> queue;
    private long dropped;
    private long droppedReported;

    // Diff + cadence state — touched only by the single poll loop, so no locking needed.
    private readonly Dictionary<int, OarcLinkState> lastLinks = new();
    private readonly Dictionary<int, OarcCircuitState> lastCircuits = new();
    private bool wasEnabled;
    private bool nodeUpSent;
    private bool warnedNoLocator;
    private DateTimeOffset lastNodeStatus = DateTimeOffset.MinValue;
    private DateTimeOffset lastLinkStatus = DateTimeOffset.MinValue;
    private DateTimeOffset lastCircuitStatus = DateTimeOffset.MinValue;

    public OarcReporter(
        IConfigProvider config,
        IOarcStateSource state,
        IOarcIngestClient client,
        NodeTelemetry telemetry,
        TimeProvider clock,
        string nodeVersion,
        ILogger<OarcReporter>? logger = null)
    {
        this.config = config ?? throw new ArgumentNullException(nameof(config));
        this.state = state ?? throw new ArgumentNullException(nameof(state));
        this.client = client ?? throw new ArgumentNullException(nameof(client));
        this.telemetry = telemetry ?? throw new ArgumentNullException(nameof(telemetry));
        this.clock = clock ?? TimeProvider.System;
        this.nodeVersion = string.IsNullOrWhiteSpace(nodeVersion) ? "dev" : nodeVersion;
        this.logger = logger ?? NullLogger<OarcReporter>.Instance;

        queue = Channel.CreateBounded<OarcEvent>(
            new BoundedChannelOptions(QueueCapacity)
            {
                FullMode = BoundedChannelFullMode.DropOldest,
                SingleReader = true,
                SingleWriter = false,
            },
            itemDropped: _ => Interlocked.Increment(ref dropped));
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var traceSub = telemetry.Subscribe(out var traceReader);
        var traceTask = ConsumeTracesAsync(traceReader, stoppingToken);
        var sendTask = SendLoopAsync(stoppingToken);
        try
        {
            await PollLoopAsync(stoppingToken).ConfigureAwait(false);
        }
        finally
        {
            await TryFinalNodeDownAsync().ConfigureAwait(false);
            queue.Writer.TryComplete();
            try { await Task.WhenAll(traceTask, sendTask).ConfigureAwait(false); }
            catch (OperationCanceledException) { /* expected on shutdown */ }
        }
    }

    // ── Poll loop ──────────────────────────────────────────────────────────

    private async Task PollLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                TickOnce();
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                LogTickFault(ex.Message);
            }

            var o = config.Current.Oarc;
            var tick = TimeSpan.FromSeconds(Math.Clamp(Math.Min(o.StatusIntervalSecs, o.SessionStatusIntervalSecs), 1, 15));
            try { await Task.Delay(tick, clock, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
        }
    }

    private void TickOnce()
    {
        var cfg = config.Current.Oarc;
        var identity = config.Current.Identity;
        var now = clock.GetUtcNow();

        if (cfg.Enabled && !wasEnabled)
        {
            OnEnabled(cfg, identity, now);
        }
        else if (!cfg.Enabled && wasEnabled)
        {
            OnDisabled(cfg, identity, now);
        }
        wasEnabled = cfg.Enabled;

        if (!cfg.Enabled)
        {
            return;
        }

        var snap = state.Capture();

        if (cfg.ReportLinks)
        {
            DiffLinks(identity, snap, cfg, now);
        }
        else if (lastLinks.Count > 0)
        {
            lastLinks.Clear();   // toggling links back on later re-emits up for what's live then
        }

        if (cfg.ReportCircuits)
        {
            DiffCircuits(identity, snap, cfg, now);
        }
        else if (lastCircuits.Count > 0)
        {
            lastCircuits.Clear();
        }

        if (cfg.ReportNodeStatus)
        {
            var loc = Locator(identity);
            if (loc is null)
            {
                WarnNoLocatorOnce();
            }
            else if (now - lastNodeStatus >= TimeSpan.FromSeconds(cfg.StatusIntervalSecs))
            {
                Enqueue(BuildNodeStatus(cfg, identity, loc, snap, now));
                lastNodeStatus = now;
                ReportDropsIfAny();
            }
        }
    }

    private void OnEnabled(OarcConfig cfg, Identity identity, DateTimeOffset now)
    {
        // Fresh start: everything live is "new" so it re-emits up; defer the first periodic status a
        // full interval (the node-up below is the immediate node report).
        lastLinks.Clear();
        lastCircuits.Clear();
        lastLinkStatus = now;
        lastCircuitStatus = now;
        lastNodeStatus = now;
        LogEnabled();

        if (!cfg.ReportNodeStatus)
        {
            return;
        }

        var loc = Locator(identity);
        if (loc is null)
        {
            WarnNoLocatorOnce();
            return;
        }

        Enqueue(BuildNodeUp(cfg, identity, loc, now));
        nodeUpSent = true;
    }

    private void OnDisabled(OarcConfig cfg, Identity identity, DateTimeOffset now)
    {
        LogDisabled();
        // A clean node-down (if we had announced up); do NOT spam link-downs — the collector ages
        // links out, and the node-down marks us off the map.
        if (nodeUpSent && cfg.ReportNodeStatus && Locator(identity) is { } loc)
        {
            Enqueue(BuildNodeDown(identity, BuildCounts(SnapshotOrEmpty()), now, "reporting disabled"));
        }
        nodeUpSent = false;
        lastLinks.Clear();
        lastCircuits.Clear();
    }

    private void DiffLinks(Identity identity, OarcNodeSnapshot snap, OarcConfig cfg, DateTimeOffset now)
    {
        var current = new Dictionary<int, OarcLinkState>(snap.Links.Count);
        foreach (var link in snap.Links)
        {
            current[link.Id] = link;
        }

        foreach (var (id, link) in current)
        {
            if (!lastLinks.ContainsKey(id))
            {
                Enqueue(BuildLinkUp(identity, link, now));
            }
        }
        foreach (var (id, link) in lastLinks)
        {
            if (!current.ContainsKey(id))
            {
                Enqueue(BuildLinkDown(identity, link, now));
            }
        }

        lastLinks.Clear();
        foreach (var (id, link) in current)
        {
            lastLinks[id] = link;
        }

        if (now - lastLinkStatus >= TimeSpan.FromSeconds(cfg.SessionStatusIntervalSecs))
        {
            foreach (var link in current.Values)
            {
                Enqueue(BuildLinkStatus(identity, link, now));
            }
            lastLinkStatus = now;
        }
    }

    private void DiffCircuits(Identity identity, OarcNodeSnapshot snap, OarcConfig cfg, DateTimeOffset now)
    {
        var current = new Dictionary<int, OarcCircuitState>(snap.Circuits.Count);
        foreach (var ckt in snap.Circuits)
        {
            current[ckt.Id] = ckt;
        }

        foreach (var (id, ckt) in current)
        {
            if (!lastCircuits.ContainsKey(id))
            {
                Enqueue(BuildCircuitUp(identity, ckt, now));
            }
        }
        foreach (var (id, ckt) in lastCircuits)
        {
            if (!current.ContainsKey(id))
            {
                Enqueue(BuildCircuitDown(identity, ckt, now));
            }
        }

        lastCircuits.Clear();
        foreach (var (id, ckt) in current)
        {
            lastCircuits[id] = ckt;
        }

        if (now - lastCircuitStatus >= TimeSpan.FromSeconds(cfg.SessionStatusIntervalSecs))
        {
            foreach (var ckt in current.Values)
            {
                Enqueue(BuildCircuitStatus(identity, ckt, now));
            }
            lastCircuitStatus = now;
        }
    }

    // ── Trace stream ───────────────────────────────────────────────────────

    private async Task ConsumeTracesAsync(ChannelReader<MonitorEvent> reader, CancellationToken ct)
    {
        try
        {
            await foreach (var frame in reader.ReadAllAsync(ct).ConfigureAwait(false))
            {
                var cfg = config.Current.Oarc;
                if (!cfg.Enabled || !cfg.ReportTraces)
                {
                    continue;   // drain + discard so the telemetry channel never backs up
                }

                var trace = MapTrace(frame, config.Current.Identity, cfg);
                if (trace is not null)
                {
                    Enqueue(trace);
                }
            }
        }
        catch (OperationCanceledException) { /* shutdown */ }
    }

    private OarcL2TraceEvent? MapTrace(MonitorEvent f, Identity identity, OarcConfig cfg)
    {
        // All current pdn transports are RF (the telemetry tap is per real AX.25 port), so isRF is
        // always true and the RF-only filter is effectively pass-through until a non-RF transport
        // exists (design §7). Honour the filter anyway so it's correct the day one is added.
        const bool isRf = true;
        if (cfg.TracesRfOnly && !isRf)
        {
            return null;
        }

        var l2Type = MapL2Type(f.Type);
        var isIorUi = l2Type is "I" or "UI";
        return new OarcL2TraceEvent
        {
            ReportFrom = identity.Callsign,
            Time = Epoch(clock.GetUtcNow()),
            Port = f.PortId,
            Direction = f.Direction == "out" ? "sent" : "rcvd",
            IsRf = isRf,
            Source = f.Source,
            Destination = f.Dest,
            Control = f.Control,
            L2Type = l2Type,
            CommandResponse = f.Command ? "C" : "R",
            ReceiveSequence = f.Nr,
            TransmitSequence = f.Ns,   // Ns is null for non-I frames → tseq omitted (no supervisory tseq)
            PollFinal = f.Pf == 1 ? (f.Command ? "P" : "F") : null,
            Pid = ParsePid(f.Pid),
            IFieldLength = isIorUi ? f.InfoLength : null,
        };
    }

    // Map pdn's frame-type names to the collector's L2 trace vocabulary (design §3.2): SABM→C (connect),
    // DISC→D (disconnect); the rest pass through when in the valid set, else "?".
    private static string MapL2Type(string type) => type switch
    {
        "SABM" => "C",
        "DISC" => "D",
        "SABME" or "DM" or "UA" or "UI" or "I" or "FRMR" or "RR" or "RNR" or "REJ" or "SREJ" or "XID" or "TEST" => type,
        _ => "?",
    };

    private static int? ParsePid(string? pid)
    {
        if (string.IsNullOrEmpty(pid))
        {
            return null;
        }
        var s = pid.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? pid[2..] : pid;
        return int.TryParse(s, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var v) ? v : null;
    }

    // ── Sender ─────────────────────────────────────────────────────────────

    private async Task SendLoopAsync(CancellationToken ct)
    {
        try
        {
            await foreach (var ev in queue.Reader.ReadAllAsync(ct).ConfigureAwait(false))
            {
                await SendWithRetryAsync(ev, ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) { /* shutdown */ }
    }

    private async Task SendWithRetryAsync(OarcEvent ev, CancellationToken ct)
    {
        var baseUrl = config.Current.Oarc.BaseUrl;
        for (var attempt = 0; ; attempt++)
        {
            var result = await client.ReportAsync(ev, baseUrl, ct).ConfigureAwait(false);
            if (!result.ShouldRetry)
            {
                return;   // accepted, or a non-retryable rejection (the client already logged it)
            }
            if (attempt >= Backoff.Length)
            {
                LogGaveUp(ev.EndpointPath, attempt + 1);
                return;
            }
            await Task.Delay(Backoff[attempt], clock, ct).ConfigureAwait(false);
        }
    }

    private async Task TryFinalNodeDownAsync()
    {
        if (!nodeUpSent)
        {
            return;
        }
        var cfg = config.Current.Oarc;
        var identity = config.Current.Identity;
        if (!cfg.ReportNodeStatus || Locator(identity) is null)
        {
            return;
        }

        // Sent DIRECTLY (not via the queue) on a short, independent deadline: the stopping token has
        // already fired, so the queued sender is unwinding and a queued node-down would never go out.
        var down = BuildNodeDown(identity, BuildCounts(SnapshotOrEmpty()), clock.GetUtcNow(), "node stopping");
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        try { await client.ReportAsync(down, cfg.BaseUrl, cts.Token).ConfigureAwait(false); }
        catch { /* shutdown best-effort */ }
        nodeUpSent = false;
    }

    // ── Builders ───────────────────────────────────────────────────────────

    private OarcNodeUpEvent BuildNodeUp(OarcConfig cfg, Identity identity, string locator, DateTimeOffset now)
    {
        var (lat, lon) = Position(cfg, locator);
        return new OarcNodeUpEvent
        {
            Time = Epoch(now),
            NodeCall = identity.Callsign,
            NodeAlias = Alias(identity),
            Locator = locator,
            Latitude = lat,
            Longitude = lon,
            Software = Software,
            Version = nodeVersion,
        };
    }

    private OarcNodeStatusEvent BuildNodeStatus(OarcConfig cfg, Identity identity, string locator, OarcNodeSnapshot snap, DateTimeOffset now)
    {
        var (lat, lon) = Position(cfg, locator);
        var counts = BuildCounts(snap);
        return new OarcNodeStatusEvent
        {
            Time = Epoch(now),
            NodeCall = identity.Callsign,
            NodeAlias = Alias(identity),
            Locator = locator,
            Latitude = lat,
            Longitude = lon,
            Software = Software,
            Version = nodeVersion,
            UptimeSecs = snap.UptimeSeconds,
            LinksIn = counts.LinksIn,
            LinksOut = counts.LinksOut,
            CircuitsIn = counts.CircuitsIn,
            CircuitsOut = counts.CircuitsOut,
            L3Relayed = snap.L3Relayed,
        };
    }

    private static OarcNodeDownEvent BuildNodeDown(Identity identity, Counts counts, DateTimeOffset now, string reason) => new()
    {
        Time = Epoch(now),
        NodeCall = identity.Callsign,
        NodeAlias = Alias(identity),
        Reason = reason,
        LinksIn = counts.LinksIn,
        LinksOut = counts.LinksOut,
        CircuitsIn = counts.CircuitsIn,
        CircuitsOut = counts.CircuitsOut,
        L3Relayed = counts.L3Relayed,
    };

    private static OarcLinkUpEvent BuildLinkUp(Identity identity, OarcLinkState l, DateTimeOffset now) => new()
    {
        Time = Epoch(now), Node = identity.Callsign, Id = l.Id, Direction = Direction(l.Inbound),
        Port = l.Port, Remote = l.Remote, Local = l.Local,
    };

    private static OarcLinkStatusEvent BuildLinkStatus(Identity identity, OarcLinkState l, DateTimeOffset now) => new()
    {
        Time = Epoch(now), Node = identity.Callsign, Id = l.Id, Direction = Direction(l.Inbound),
        Port = l.Port, Remote = l.Remote, Local = l.Local,
        UpForSecs = l.UpForSeconds,
        FramesSent = l.FramesSent, FramesReceived = l.FramesReceived, FramesResent = l.FramesResent,
        FramesQueued = 0,
        BytesSent = l.BytesSent, BytesReceived = l.BytesReceived, L2RttMs = l.L2RttMs,
    };

    private static OarcLinkDownEvent BuildLinkDown(Identity identity, OarcLinkState l, DateTimeOffset now) => new()
    {
        Time = Epoch(now), Node = identity.Callsign, Id = l.Id, Direction = Direction(l.Inbound),
        Port = l.Port, Remote = l.Remote, Local = l.Local,
        UpForSecs = l.UpForSeconds,
        FramesSent = l.FramesSent, FramesReceived = l.FramesReceived, FramesResent = l.FramesResent,
        FramesQueued = 0,
        BytesSent = l.BytesSent, BytesReceived = l.BytesReceived,
        Reason = "disconnected",
    };

    private static OarcCircuitUpEvent BuildCircuitUp(Identity identity, OarcCircuitState c, DateTimeOffset now) => new()
    {
        Time = Epoch(now), Node = identity.Callsign, Id = c.Id, Direction = Direction(c.Inbound),
        Service = c.Service, Remote = c.Remote, Local = c.Local,
    };

    private static OarcCircuitStatusEvent BuildCircuitStatus(Identity identity, OarcCircuitState c, DateTimeOffset now) => new()
    {
        Time = Epoch(now), Node = identity.Callsign, Id = c.Id, Direction = Direction(c.Inbound),
        Service = c.Service, Remote = c.Remote, Local = c.Local,
        SegmentsSent = c.SegmentsSent, SegmentsReceived = c.SegmentsReceived,
        SegmentsResent = c.SegmentsResent, SegmentsQueued = c.SegmentsQueued,
        BytesSent = c.BytesSent, BytesReceived = c.BytesReceived, UpForSecs = c.UpForSeconds,
    };

    private static OarcCircuitDownEvent BuildCircuitDown(Identity identity, OarcCircuitState c, DateTimeOffset now) => new()
    {
        Time = Epoch(now), Node = identity.Callsign, Id = c.Id, Direction = Direction(c.Inbound),
        Service = c.Service, Remote = c.Remote, Local = c.Local,
        SegmentsSent = c.SegmentsSent, SegmentsReceived = c.SegmentsReceived,
        SegmentsResent = c.SegmentsResent, SegmentsQueued = c.SegmentsQueued,
        BytesSent = c.BytesSent, BytesReceived = c.BytesReceived, UpForSecs = c.UpForSeconds,
        Reason = "disconnected",
    };

    // ── Helpers ────────────────────────────────────────────────────────────

    private readonly record struct Counts(int LinksIn, int LinksOut, int CircuitsIn, int CircuitsOut, long L3Relayed);

    private static Counts BuildCounts(OarcNodeSnapshot snap)
    {
        var linksIn = 0;
        var linksOut = 0;
        foreach (var l in snap.Links)
        {
            if (l.Inbound) { linksIn++; } else { linksOut++; }
        }
        var cktIn = 0;
        var cktOut = 0;
        foreach (var c in snap.Circuits)
        {
            if (c.Inbound) { cktIn++; } else { cktOut++; }
        }
        return new Counts(linksIn, linksOut, cktIn, cktOut, snap.L3Relayed);
    }

    private OarcNodeSnapshot SnapshotOrEmpty()
    {
        try { return state.Capture(); }
        catch { return new OarcNodeSnapshot { UptimeSeconds = 0 }; }
    }

    private static (double? Lat, double? Lon) Position(OarcConfig cfg, string locator)
    {
        if (cfg.PublishExactPosition && MaidenheadLocator.TryToLatLon(locator, out var lat, out var lon))
        {
            return (lat, lon);
        }
        return (null, null);
    }

    private static string Direction(bool inbound) => inbound ? "incoming" : "outgoing";

    private static string Alias(Identity identity) =>
        string.IsNullOrWhiteSpace(identity.Alias) ? identity.Callsign : identity.Alias!;

    /// <summary>The node's locator if <see cref="Identity.Grid"/> is a valid 6-char Maidenhead grid,
    /// else null. Resets the "no locator" warning latch when it becomes valid again.</summary>
    private string? Locator(Identity identity)
    {
        var grid = identity.Grid?.Trim();
        if (MaidenheadLocator.IsValid(grid))
        {
            warnedNoLocator = false;
            return grid;
        }
        return null;
    }

    private void WarnNoLocatorOnce()
    {
        if (!warnedNoLocator)
        {
            LogNoLocator();
            warnedNoLocator = true;
        }
    }

    private void Enqueue(OarcEvent ev) => queue.Writer.TryWrite(ev);

    private void ReportDropsIfAny()
    {
        var total = Interlocked.Read(ref dropped);
        if (total > droppedReported)
        {
            LogDropped(total - droppedReported, total);
            droppedReported = total;
        }
    }

    private static long Epoch(DateTimeOffset t) => t.ToUnixTimeSeconds();

    // ── Logging ────────────────────────────────────────────────────────────

    [LoggerMessage(Level = LogLevel.Information, Message = "OARC reporting enabled; announcing this node to the map.")]
    private partial void LogEnabled();

    [LoggerMessage(Level = LogLevel.Information, Message = "OARC reporting disabled; marking this node down on the map.")]
    private partial void LogDisabled();

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "OARC reporting is on but the node has no valid Maidenhead grid (Identity.Grid) — node will not appear on the map until a valid grid is set.")]
    private partial void LogNoLocator();

    [LoggerMessage(Level = LogLevel.Debug,
        Message = "OARC ingest: gave up on {Endpoint} after {Attempts} attempts; dropped (periodic status will re-assert state).")]
    private partial void LogGaveUp(string endpoint, int attempts);

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "OARC reporter: {New} events dropped under backpressure since last report ({Total} total) — the collector is slow or unreachable.")]
    private partial void LogDropped(long @new, long total);

    [LoggerMessage(Level = LogLevel.Debug, Message = "OARC reporter: a poll tick faulted ({Reason}); skipped this tick.")]
    private partial void LogTickFault(string reason);
}
