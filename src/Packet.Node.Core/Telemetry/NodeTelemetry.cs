using System.Collections.Concurrent;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Packet.Ax25.Session;
using Packet.Node.Core.Api;

namespace Packet.Node.Core.Telemetry;

/// <summary>
/// The node's live frame/byte telemetry: a singleton that taps each port's
/// <see cref="Ax25Listener.FrameTraced"/> event and maintains thread-safe per-port
/// and per-(port, peer) counters, plus a fan-out of decoded
/// <see cref="MonitorEvent"/>s for the web monitor's SSE feed
/// (<c>GET /api/v1/events</c>).
/// </summary>
/// <remarks>
/// <para>
/// <b>Concurrency.</b> <see cref="Observe"/> runs on listener <em>pump threads</em>
/// — several at once when multiple ports are up — so every counter mutation uses
/// <see cref="Interlocked"/> over fields on a per-key counter object held in a
/// <see cref="ConcurrentDictionary{TKey,TValue}"/>. The SSE subscriber set is a
/// concurrent dictionary of bounded channel writers; a slow consumer drops its
/// oldest frames (<see cref="BoundedChannelFullMode.DropOldest"/>) rather than
/// blocking a pump thread.
/// </para>
/// <para>
/// <b>Observation only.</b> Like the NET/ROM tap, this can never disturb a session:
/// it only reads the traced frame, and <see cref="Observe"/> swallows + logs any
/// fault so a telemetry bug never propagates onto the engine's pump thread.
/// </para>
/// <para>
/// <b>Lifecycle.</b> <see cref="AttachPort"/> / <see cref="DetachPort"/> mirror the
/// NET/ROM service's attach lifecycle — the supervisor calls them as ports come up
/// and go down, so the tap is subscribed for exactly the lifetime of the port and a
/// torn-down port's counters are dropped.
/// </para>
/// </remarks>
public sealed partial class NodeTelemetry
{
    private readonly ILogger<NodeTelemetry> logger;

    // Per-port frame totals (in/out), and per-(port, peer) link counters. Both are
    // keyed for lookup by the read projections; both are mutated only via Interlocked.
    private readonly ConcurrentDictionary<string, PortCounters> ports = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<LinkKey, LinkCounters> links = new();

    // Live port attachments: the FrameTraced handler we subscribed, so DetachPort can
    // unsubscribe exactly what AttachPort added.
    private readonly ConcurrentDictionary<string, Attachment> attachments = new(StringComparer.Ordinal);

    // SSE subscribers — each /events connection registers a bounded channel writer.
    private readonly ConcurrentDictionary<Guid, ChannelWriter<MonitorEvent>> subscribers = new();

    // Monotonic frame sequence across the whole node (matches the UI's seq semantics).
    private long seq;

    // A bounded ring of the most recent decoded frames so a freshly-opened web
    // monitor bootstraps with recent history (GET /api/v1/monitor/recent) instead of
    // an empty table that only fills as new frames arrive. Guarded by its own lock
    // (the per-pump-thread Observe appends; a request thread snapshots) — kept off the
    // Interlocked counter path so it doesn't touch that hot loop.
    private const int HistoryCapacity = 250;
    private readonly object historyLock = new();
    private readonly Queue<MonitorEvent> history = new(HistoryCapacity);

    // The MHeard log (#454) — an optional collaborator fed from this very tap so the heard log is a
    // derivation of the same frame-trace stream, not a parallel tap. Null ⇒ no heard log on this
    // node (older tests / an embedder that didn't register one); Observe then behaves exactly as
    // before. Every RECEIVED frame's source callsign is recorded as a hearing on its port.
    private readonly Heard.HeardLog? heardLog;

    public NodeTelemetry(ILogger<NodeTelemetry>? logger = null, Heard.HeardLog? heardLog = null)
    {
        this.logger = logger ?? NullLogger<NodeTelemetry>.Instance;
        this.heardLog = heardLog;
    }

    // ─── port lifecycle (mirrors NetRomService.AttachPort/DetachPort) ───────

    /// <summary>Begin tapping a port's frame trace. No-op if already attached.
    /// <paramref name="radioSource"/> is the port's node-owned per-frame radio metadata read (the
    /// <see cref="InboundRadioTap"/> on a radio-attached port), or <c>null</c> when the port has no
    /// radio — it stamps RSSI/SNR onto received frames without the AX.25 listener contract carrying
    /// any of it.</summary>
    public void AttachPort(string portId, Ax25Listener listener, IInboundRadioSource? radioSource = null)
    {
        ArgumentNullException.ThrowIfNull(portId);
        ArgumentNullException.ThrowIfNull(listener);

        void Tap(object? _, Ax25FrameEventArgs e) => Observe(portId, e, radioSource);
        var attachment = new Attachment(listener, Tap);
        if (!attachments.TryAdd(portId, attachment))
        {
            return;
        }
        listener.FrameTraced += Tap;
    }

    /// <summary>Stop tapping a port and drop its counters (the port is gone — a
    /// fresh bring-up of the same id starts counting from zero, which is honest:
    /// the port bounced). A live KISS/AX.25 re-tune does not detach, so counters
    /// survive a hot reconfigure.</summary>
    public void DetachPort(string portId)
    {
        ArgumentNullException.ThrowIfNull(portId);
        if (attachments.TryRemove(portId, out var attachment))
        {
            attachment.Listener.FrameTraced -= attachment.Tap;
        }
        ports.TryRemove(portId, out _);
        foreach (var key in links.Keys)
        {
            if (string.Equals(key.Port, portId, StringComparison.Ordinal))
            {
                links.TryRemove(key, out _);
            }
        }
    }

    // ─── the tap ────────────────────────────────────────────────────────────

    /// <summary>
    /// Record one traced frame: bump the per-port + per-link counters and broadcast
    /// the decoded <see cref="MonitorEvent"/> to every SSE subscriber. Never throws
    /// — a fault is logged and swallowed so telemetry can't disturb the pump thread.
    /// </summary>
    public void Observe(string portId, Ax25FrameEventArgs e, IInboundRadioSource? radioSource = null)
    {
        try
        {
            bool rx = e.Direction == FrameDirection.Received;

            // Per-frame radio metadata is a received-frame concept only, and safe to read here:
            // Observe runs synchronously on the listener's single inbound-pump thread for an RX
            // frame, right after the tap yielded it — so LatestInboundRadio is still this frame's
            // (see InboundRadioTap). A TX frame has no inbound radio, so never read it for one.
            var radio = rx ? radioSource?.LatestInboundRadio : null;

            long s = Interlocked.Increment(ref seq);
            var evt = MonitorEventFactory.From(s, portId, e, radio);

            int payload = e.Frame.Info.Length;
            long nowTicks = e.Timestamp.UtcTicks;

            var pc = ports.GetOrAdd(portId, static _ => new PortCounters());
            if (rx)
            {
                Interlocked.Increment(ref pc.FramesIn);
            }
            else
            {
                Interlocked.Increment(ref pc.FramesOut);
            }

            // The link "peer" is the other end of the exchange: the source on RX, the
            // destination on TX.
            var key = new LinkKey(portId, rx ? evt.Source : evt.Dest);
            var lc = links.GetOrAdd(key, static _ => new LinkCounters());
            // FirstSeen is set exactly once (the connect/first-frame instant); 0 ⇒ unset.
            Interlocked.CompareExchange(ref lc.FirstSeenTicks, nowTicks, 0);
            Volatile.Write(ref lc.LastActivityTicks, nowTicks);
            if (rx)
            {
                Interlocked.Increment(ref lc.FramesIn);
                Interlocked.Add(ref lc.BytesIn, payload);
            }
            else
            {
                Interlocked.Increment(ref lc.FramesOut);
                Interlocked.Add(ref lc.BytesOut, payload);
            }
            switch (evt.Type)
            {
                case "REJ": Interlocked.Increment(ref lc.RejCount); break;
                case "SREJ": Interlocked.Increment(ref lc.SrejCount); break;
            }

            // MHeard (#454): a RECEIVED frame means we heard its SOURCE station on this port. The
            // heard log owns persistence + survival-across-teardown; this is just the feed. TX
            // frames are ours, so they are never a "hearing".
            if (rx)
            {
                heardLog?.Record(portId, evt.Source, e.Timestamp, radio?.RssiDbm, radio?.SnrDb);
            }

            lock (historyLock)
            {
                history.Enqueue(evt);
                while (history.Count > HistoryCapacity)
                {
                    history.Dequeue();
                }
            }

            Broadcast(evt);
        }
        catch (Exception ex)
        {
            LogObserveFault(ex, portId);
        }
    }

    /// <summary>
    /// A snapshot of the most recent frames (oldest → newest), up to
    /// <paramref name="limit"/>, for the monitor's bootstrap fetch. The web client
    /// seeds its table with this, then live-streams from <c>/events</c> and dedupes
    /// the overlap by <see cref="MonitorEvent.Seq"/>.
    /// </summary>
    public IReadOnlyList<MonitorEvent> RecentFrames(int limit)
    {
        if (limit <= 0)
        {
            return [];
        }
        lock (historyLock)
        {
            int skip = Math.Max(0, history.Count - limit);
            return history.Skip(skip).ToArray();
        }
    }

    private void Broadcast(MonitorEvent evt)
    {
        // DropOldest channels never reject a write, so this is a tight, non-blocking
        // fan-out even with a slow SSE consumer.
        foreach (var writer in subscribers.Values)
        {
            writer.TryWrite(evt);
        }
    }

    // ─── SSE subscription ─────────────────────────────────────────────────────

    /// <summary>
    /// Open a live frame subscription. Returns a reader the SSE endpoint drains; the
    /// returned <see cref="IDisposable"/> unsubscribes (and completes the channel) on
    /// client disconnect. The channel is bounded — a consumer that falls behind drops
    /// its oldest buffered frames rather than back-pressuring the pump threads.
    /// </summary>
    public IDisposable Subscribe(out ChannelReader<MonitorEvent> reader)
    {
        var channel = Channel.CreateBounded<MonitorEvent>(new BoundedChannelOptions(256)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = false,
        });
        var id = Guid.NewGuid();
        subscribers[id] = channel.Writer;
        reader = channel.Reader;
        return new Subscription(this, id, channel.Writer);
    }

    /// <summary>Number of live SSE subscribers (for tests).</summary>
    public int SubscriberCount => subscribers.Count;

    // ─── read projections (for the /ports, /links, /sessions endpoints) ───────

    /// <summary>Per-port frame totals; (0, 0) for a port with no traffic yet.</summary>
    public (long FramesIn, long FramesOut) PortFrames(string portId)
        => ports.TryGetValue(portId, out var c)
            ? (Volatile.Read(ref c.FramesIn), Volatile.Read(ref c.FramesOut))
            : (0, 0);

    /// <summary>Snapshot of every known (port, peer) link.</summary>
    public IReadOnlyList<LinkSnapshot> Links()
        => links.Select(kv => Snap(kv.Key, kv.Value)).ToArray();

    /// <summary>Snapshot of one (port, peer) link, or null if unseen.</summary>
    public LinkSnapshot? Link(string portId, string peer)
        => links.TryGetValue(new LinkKey(portId, peer), out var c)
            ? Snap(new LinkKey(portId, peer), c)
            : null;

    private static LinkSnapshot Snap(LinkKey key, LinkCounters c)
    {
        long firstTicks = Volatile.Read(ref c.FirstSeenTicks);
        long lastTicks = Volatile.Read(ref c.LastActivityTicks);
        return new LinkSnapshot(
            PortId: key.Port,
            Peer: key.Peer,
            FramesIn: Volatile.Read(ref c.FramesIn),
            FramesOut: Volatile.Read(ref c.FramesOut),
            BytesIn: Volatile.Read(ref c.BytesIn),
            BytesOut: Volatile.Read(ref c.BytesOut),
            RejCount: (int)Volatile.Read(ref c.RejCount),
            SrejCount: (int)Volatile.Read(ref c.SrejCount),
            FirstSeen: firstTicks == 0 ? DateTimeOffset.MinValue : new DateTimeOffset(firstTicks, TimeSpan.Zero),
            LastActivity: lastTicks == 0 ? DateTimeOffset.MinValue : new DateTimeOffset(lastTicks, TimeSpan.Zero));
    }

    private readonly record struct LinkKey(string Port, string Peer);

    private sealed class PortCounters
    {
        public long FramesIn;
        public long FramesOut;
    }

    private sealed class LinkCounters
    {
        public long FramesIn;
        public long FramesOut;
        public long BytesIn;
        public long BytesOut;
        public long RejCount;
        public long SrejCount;
        public long FirstSeenTicks;
        public long LastActivityTicks;
    }

    private sealed record Attachment(Ax25Listener Listener, EventHandler<Ax25FrameEventArgs> Tap);

    private sealed class Subscription(NodeTelemetry owner, Guid id, ChannelWriter<MonitorEvent> writer) : IDisposable
    {
        private int disposed;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref disposed, 1) != 0)
            {
                return;
            }
            owner.subscribers.TryRemove(id, out _);
            writer.TryComplete();
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Telemetry: frame tap on port {PortId} faulted (frame ignored).")]
    private partial void LogObserveFault(Exception ex, string portId);
}

/// <summary>An immutable read of one (port, peer) link's telemetry counters.</summary>
public sealed record LinkSnapshot(
    string PortId,
    string Peer,
    long FramesIn,
    long FramesOut,
    long BytesIn,
    long BytesOut,
    int RejCount,
    int SrejCount,
    DateTimeOffset FirstSeen,
    DateTimeOffset LastActivity);
