using System.Collections.Concurrent;
using System.Threading.Channels;
using Packet.Tune.Core;

namespace Packet.Node.Core.Heard;

/// <summary>
/// The MHeard log (#454): remembers which stations have been heard on each port, with a
/// first-heard / last-heard timestamp and a frame count, so the <c>MH</c> console verb (and the
/// REST surface) can list recently heard stations the way operators expect from BPQ / Linux-AX.25
/// nodes — and, unlike the live <c>/api/v1/links</c> telemetry, the log <b>survives port teardown
/// and node restart</b>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Feed.</b> <see cref="Record"/> is called from <see cref="Telemetry.NodeTelemetry.Observe"/>
/// for every <i>received</i> frame, with the transmitting station's source callsign. So the heard
/// log is a derivation of the very same frame-trace stream that feeds <c>/api/v1/links</c> — one
/// source of truth, not a parallel tap. It runs on listener pump threads, so the hot dictionary is
/// a <see cref="ConcurrentDictionary{TKey,TValue}"/> and the per-entry count is bumped under a
/// short lock on the entry object (entries are rare-churn relative to frames).
/// </para>
/// <para>
/// <b>Persistence + survival.</b> A hot dictionary keyed by (port, callsign) is hydrated from
/// <see cref="IHeardStore.All"/> on construction and written through on every update. Crucially —
/// and unlike <see cref="Telemetry.NodeTelemetry"/>, whose counters are dropped when a port detaches
/// because a bounced port should count from zero — the heard log is the <em>history</em>: it is
/// never cleared on teardown, only by the retention policy (<see cref="Prune"/>) or an explicit
/// operator <see cref="Forget"/> / <see cref="Clear"/>.
/// </para>
/// <para>
/// <b>Optional store.</b> A null store ⇒ in-memory only (the log still works for the run, it just
/// doesn't survive a restart) — keeping tests and embedders that don't supply a <c>pdn.db</c>
/// unaffected, mirroring the capability cache + NET/ROM service.
/// </para>
/// <para>
/// <b>The radio path is never back-pressured (C077).</b> <see cref="Record"/> runs on the AX.25
/// inbound pump, BEFORE the frame is dispatched, so anything slow there is channel downtime. Only
/// the hot-dictionary update is inline; the snapshot is handed to a bounded drop-oldest channel
/// drained by ONE writer that coalesces per (port, callsign) and upserts the batch in a single
/// transaction, and the opportunistic retention pass runs on that writer too. This is the
/// <see cref="Traffic.TrafficLogService"/> shape, applied to the same frame stream: a stalled disk
/// costs heard-log freshness, never RX. An explicit <see cref="Prune"/> and the operator verbs
/// (<see cref="Forget"/> / <see cref="Clear"/>) stay synchronous - they are caller-initiated, not
/// on the pump.
/// </para>
/// </remarks>
public sealed class HeardLog : IDisposable
{
    /// <summary>Default age-out window — a station not heard for this long is pruned.</summary>
    public static readonly TimeSpan DefaultRetention = TimeSpan.FromDays(30);

    /// <summary>Default per-port cap — at most this many heard stations are kept per port (the
    /// oldest-heard beyond it are pruned). Bounds growth on a busy channel.</summary>
    public const int DefaultMaxPerPort = 500;

    private readonly IHeardStore? store;
    private readonly TimeProvider time;
    private readonly TimeSpan retention;
    private readonly int maxPerPort;

    private readonly ConcurrentDictionary<(string PortId, string Callsign), Entry> hot = new();

    // Opportunistic-prune throttle: instead of a background timer, re-apply the retention policy
    // once every this-many recorded hearings. Cheap (a dictionary scan), bounds both the age-out
    // lag and the per-port size without owning a timer to dispose. Construction prunes once too.
    // With a store attached the pass runs on the WRITER, not the pump that asked for it.
    private const int PruneEveryRecords = 2048;
    private long recordsSincePrune;
    private int pruneRequested;

    /// <summary>Bound on the writer hand-off queue. Generous (a snapshot is small) but finite:
    /// a stalled writer drops heard-log freshness, never RX. Drop-OLDEST because the writer
    /// coalesces per (port, callsign) anyway, so the newest snapshot for a station is the only
    /// one that carries information; the hot dictionary remains the truth either way, and the
    /// station's next frame re-enqueues it.</summary>
    private const int WriteQueueCapacity = 4096;

    // Null when there is no store: nothing to write, so no queue and no writer task, and the
    // opportunistic prune (pure in-memory then) stays inline, exactly as before.
    private readonly Channel<HeardEntry>? writes;
    private readonly CancellationTokenSource? writerStop;

    // Serialises the drain so the background loop and an explicit Flush/Dispose can never
    // interleave: whoever holds it takes the pending snapshots AND writes them, so once Flush
    // acquires it, everything enqueued before the call has reached the store.
    private readonly object writeGate = new();
    private int disposed;

    /// <summary>Build the log over an optional <paramref name="store"/> (null ⇒ in-memory only),
    /// an optional <paramref name="time"/> source, and the retention policy (age-out window +
    /// per-port cap; null/non-positive fall back to the defaults). Hydrates the hot dictionary from
    /// the store on construction, then prunes once so a restart re-applies the policy to the
    /// persisted rows.</summary>
    public HeardLog(
        IHeardStore? store = null,
        TimeProvider? time = null,
        TimeSpan? retention = null,
        int? maxPerPort = null)
    {
        this.store = store;
        this.time = time ?? TimeProvider.System;
        this.retention = retention is { } r && r > TimeSpan.Zero ? r : DefaultRetention;
        this.maxPerPort = maxPerPort is { } m && m > 0 ? m : DefaultMaxPerPort;

        if (store is not null)
        {
            foreach (var e in store.All())
            {
                hot[(e.PortId, e.Callsign)] = Entry.From(e);
            }

            writes = Channel.CreateBounded<HeardEntry>(new BoundedChannelOptions(WriteQueueCapacity)
            {
                FullMode = BoundedChannelFullMode.DropOldest,
                SingleReader = true,    // one drainer at a time, enforced by writeGate
                SingleWriter = false,   // every port's inbound pump records
            });
            writerStop = new CancellationTokenSource();
            // Not awaited or held: the scheduler roots a running task, and Dispose flushes the
            // tail through the same gate rather than by waiting on this one.
            _ = Task.Run(() => WriteLoopAsync(writerStop.Token));
        }

        // Re-apply the retention policy to the hydrated set, so a restart drops stations that
        // aged out while we were down (and re-caps a port that grew before the policy changed).
        Prune();
    }

    /// <summary>
    /// Record one hearing: a frame received from <paramref name="callsign"/> on
    /// <paramref name="portId"/> at <paramref name="at"/>. Sets first-heard once, advances
    /// last-heard, bumps the count, and writes through to the store. Never throws — the telemetry
    /// tap that calls this already swallows faults, but this is total anyway.
    /// <paramref name="rssiDbm"/> is the per-frame RSSI (dBm) when a radio control channel measured
    /// it, else <c>null</c>; it becomes the entry's last-heard RSSI whenever this frame advances
    /// last-heard (so the stored figure tracks the newest frame, and a null on a later frame does
    /// not erase a real earlier reading — only a newer real reading replaces it).
    /// <paramref name="snrDb"/> is the matching per-frame SNR (dB) and tracks last-heard the same way.
    /// <paramref name="preDataCarrierMs"/> is the frame's measured carrier-rise→first-data lead in ms
    /// (only burst-opening frames carry one); unlike the last-heard RSSI/SNR it feeds a rolling
    /// per-station median window (last 32 measured samples) rather than a newest-wins field, because
    /// the peer's effective TXDELAY is a slow-moving property a single frame shouldn't redefine.
    /// </summary>
    public void Record(
        string portId, string callsign, DateTimeOffset at,
        float? rssiDbm = null, float? snrDb = null, float? preDataCarrierMs = null)
    {
        ArgumentNullException.ThrowIfNull(portId);
        ArgumentNullException.ThrowIfNull(callsign);
        if (callsign.Length == 0)
        {
            return;
        }

        var entry = hot.GetOrAdd((portId, callsign), key => new Entry(at) { PortId = key.PortId, Callsign = key.Callsign });
        HeardEntry snapshot;
        lock (entry)
        {
            if (at < entry.FirstHeard)
            {
                entry.FirstHeard = at;   // an out-of-order earlier frame still moves first-heard back
            }
            if (at >= entry.LastHeard)
            {
                entry.LastHeard = at;
                // Last-heard RSSI / SNR track the newest frame; a newer frame with no reading leaves
                // the last real one in place rather than nulling it.
                if (rssiDbm is not null)
                {
                    entry.LastRssiDbm = rssiDbm;
                }
                if (snrDb is not null)
                {
                    entry.LastSnrDb = snrDb;
                }
            }
            if (preDataCarrierMs is { } pre)
            {
                // Rolling median, not newest-wins: the sample joins the entry's bounded window
                // (created lazily — most stations on a radio-less port never measure one) and the
                // stored median/count are recomputed from the live window. A restart hydrates the
                // persisted median/count for display, but the window itself restarts empty — the
                // first new sample honestly resets the count to 1.
                entry.PreData ??= new PreDataCarrierWindow();
                entry.PreData.Add(pre);
                entry.MedianPreDataCarrierMs = entry.PreData.MedianMs is { } median ? (float)median : null;
                entry.PreDataCarrierSamples = entry.PreData.Count;
            }
            entry.Count++;
            snapshot = new HeardEntry(
                portId, callsign, entry.FirstHeard, entry.LastHeard, entry.Count,
                entry.LastRssiDbm, entry.LastSnrDb, entry.MedianPreDataCarrierMs, entry.PreDataCarrierSamples);
        }
        // Timer-free retention: re-apply the policy every PruneEveryRecords hearings.
        bool prune = false;
        if (Interlocked.Increment(ref recordsSincePrune) >= PruneEveryRecords)
        {
            Interlocked.Exchange(ref recordsSincePrune, 0);
            prune = true;
        }

        if (writes is null)
        {
            // No store: nothing to write and the prune is a pure in-memory scan, so it stays
            // where it was. (This is the tests-and-embedders path.)
            if (prune)
            {
                Prune();
            }
            return;
        }

        if (prune)
        {
            Volatile.Write(ref pruneRequested, 1);
        }

        // The ONLY thing this method does with the store: a non-blocking hand-off. TryWrite on a
        // drop-oldest channel cannot block and cannot fail, so a wedged SQLite writer can never
        // stall the inbound pump between TraceFrame and DispatchInbound.
        writes.Writer.TryWrite(snapshot);
    }

    /// <summary>
    /// Write every pending hearing through to the store now, on the calling thread. The
    /// deterministic seam for tests and for <see cref="Dispose"/>; a no-op with no store.
    /// After it returns, every snapshot enqueued before the call has been persisted.
    /// </summary>
    public void Flush()
    {
        if (writes is not null)
        {
            DrainOnce();
        }
    }

    private async Task WriteLoopAsync(CancellationToken ct)
    {
        try
        {
            while (await writes!.Reader.WaitToReadAsync(ct).ConfigureAwait(false))
            {
                DrainOnce();
            }
        }
        catch (Exception)
        {
            // Disposal, or a store fault the store itself already logged. Total by design: this
            // is a background task on the node's shutdown path, so an escaping fault would be an
            // unobserved exception, never a diagnosis.
        }
    }

    // Take everything queued and write it as ONE coalesced, batched transaction. Coalescing is
    // what makes a busy channel cheap: a station heard 50 times between two drains is one row
    // write, not 50. Runs under writeGate so a concurrent Flush/Dispose serialises behind it.
    private void DrainOnce()
    {
        lock (writeGate)
        {
            Dictionary<(string PortId, string Callsign), HeardEntry>? batch = null;
            while (writes!.Reader.TryRead(out var snapshot))
            {
                batch ??= [];
                batch[(snapshot.PortId, snapshot.Callsign)] = snapshot;   // newest wins
            }

            if (batch is not null)
            {
                store!.Upsert([.. batch.Values]);
            }

            if (Interlocked.Exchange(ref pruneRequested, 0) == 1)
            {
                Prune();
            }
        }
    }

    /// <summary>Stop the writer and flush what it still holds. Idempotent.</summary>
    public void Dispose()
    {
        if (writes is null || Interlocked.Exchange(ref disposed, 1) == 1)
        {
            return;
        }

        writerStop!.Cancel();
        writes.Writer.TryComplete();

        // Flush the tail synchronously, but do NOT block on the writer task and do NOT dispose
        // the CTS the loop is still parked on. DI disposes this singleton on the host's shutdown
        // path; a blocking wait on a thread-pool continuation there deadlocks whenever the pool
        // is saturated. DrainOnce takes the same writeGate the loop holds, so it is a bounded
        // Monitor wait (no pool involvement) and still guarantees everything enqueued before this
        // call has reached the store.
        DrainOnce();
    }

    /// <summary>Every heard entry across all ports (per (port, callsign)), most-recently-heard
    /// first.</summary>
    public IReadOnlyList<HeardEntry> All() => Sorted(hot.Values.Select(Snapshot));

    /// <summary>The heard entries for one port, most-recently-heard first.</summary>
    public IReadOnlyList<HeardEntry> ForPort(string portId)
    {
        ArgumentNullException.ThrowIfNull(portId);
        return Sorted(hot
            .Where(kv => string.Equals(kv.Key.PortId, portId, StringComparison.Ordinal))
            .Select(kv => Snapshot(kv.Value)));
    }

    /// <summary>
    /// The node-wide view: each callsign merged across every port it was heard on — the earliest
    /// first-heard, the latest last-heard, the summed count, and the count of distinct ports it was
    /// heard on. Most-recently-heard first. (The per-port view is <see cref="All"/> / <see cref="ForPort"/>.)
    /// </summary>
    public IReadOnlyList<HeardStationSummary> NodeWide()
    {
        var byCall = new Dictionary<string, HeardStationSummary>(StringComparer.Ordinal);
        foreach (var entry in hot.Values.Select(Snapshot))
        {
            if (byCall.TryGetValue(entry.Callsign, out var s))
            {
                // The merged last-heard RSSI follows the merged last-heard: the reading from
                // whichever port heard this station most recently (keep the incumbent on a tie).
                bool entryNewer = entry.LastHeard > s.LastHeard;
                byCall[entry.Callsign] = s with
                {
                    FirstHeard = entry.FirstHeard < s.FirstHeard ? entry.FirstHeard : s.FirstHeard,
                    LastHeard = entryNewer ? entry.LastHeard : s.LastHeard,
                    Count = s.Count + entry.Count,
                    Ports = s.Ports + 1,
                    LastRssiDbm = entryNewer ? entry.LastRssiDbm : s.LastRssiDbm,
                    LastSnrDb = entryNewer ? entry.LastSnrDb : s.LastSnrDb,
                    MedianPreDataCarrierMs = entryNewer ? entry.MedianPreDataCarrierMs : s.MedianPreDataCarrierMs,
                    PreDataCarrierSamples = entryNewer ? entry.PreDataCarrierSamples : s.PreDataCarrierSamples,
                };
            }
            else
            {
                byCall[entry.Callsign] = new HeardStationSummary(
                    entry.Callsign, entry.FirstHeard, entry.LastHeard, entry.Count, Ports: 1,
                    entry.LastRssiDbm, entry.LastSnrDb, entry.MedianPreDataCarrierMs, entry.PreDataCarrierSamples);
            }
        }
        return byCall.Values
            .OrderByDescending(s => s.LastHeard)
            .ThenBy(s => s.Callsign, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>Forget one (port, callsign) — clears the store row and the hot entry. Returns
    /// whether the hot entry was present (the store delete is best-effort).</summary>
    public bool Forget(string portId, string callsign)
    {
        ArgumentNullException.ThrowIfNull(portId);
        ArgumentNullException.ThrowIfNull(callsign);
        store?.Clear(portId, callsign);
        return hot.TryRemove((portId, callsign), out _);
    }

    /// <summary>Forget everything — the operator reset. Returns the number of hot entries removed.</summary>
    public int Clear()
    {
        store?.ClearAll();
        int n = hot.Count;
        hot.Clear();
        return n;
    }

    /// <summary>
    /// Apply the retention policy now: drop entries last heard before the age-out window, then cap
    /// each port to <see cref="DefaultMaxPerPort"/> (configurable) by dropping its oldest-heard
    /// entries. Mutates the hot dictionary and the store in step. Returns the number of entries
    /// removed. Called once at construction and after a configurable interval by the host's
    /// sweep — cheap and idempotent.
    /// </summary>
    public int Prune()
    {
        var cutoff = time.GetUtcNow() - retention;
        int removed = 0;

        // 1) Age-out.
        foreach (var kv in hot)
        {
            if (Snapshot(kv.Value).LastHeard < cutoff && hot.TryRemove(kv.Key, out _))
            {
                removed++;
            }
        }

        // 2) Per-port cap: keep the newest @maxPerPort per port, drop the rest.
        foreach (var portGroup in hot.Keys.GroupBy(k => k.PortId, StringComparer.Ordinal))
        {
            var keys = portGroup.ToList();
            if (keys.Count <= maxPerPort)
            {
                continue;
            }
            var toDrop = keys
                .Select(k => (Key: k, Last: hot.TryGetValue(k, out var e) ? Snapshot(e).LastHeard : DateTimeOffset.MinValue))
                .OrderByDescending(x => x.Last)
                .Skip(maxPerPort)
                .Select(x => x.Key);
            foreach (var key in toDrop)
            {
                if (hot.TryRemove(key, out _))
                {
                    removed++;
                }
            }
        }

        // Mirror the prune to the store in one pass (it applies the same policy server-side).
        store?.Prune(cutoff, maxPerPort);
        return removed;
    }

    private HeardEntry Snapshot(Entry e)
    {
        lock (e)
        {
            return new HeardEntry(
                e.PortId, e.Callsign, e.FirstHeard, e.LastHeard, e.Count,
                e.LastRssiDbm, e.LastSnrDb, e.MedianPreDataCarrierMs, e.PreDataCarrierSamples);
        }
    }

    private static List<HeardEntry> Sorted(IEnumerable<HeardEntry> entries) =>
        entries
            .OrderByDescending(e => e.LastHeard)
            .ThenBy(e => e.Callsign, StringComparer.Ordinal)
            .ToList();

    // Mutable hot cell guarded by its own monitor; the (port, callsign) key lives on the cell so a
    // snapshot is self-contained.
    private sealed class Entry
    {
        public string PortId = string.Empty;
        public string Callsign = string.Empty;
        public DateTimeOffset FirstHeard;
        public DateTimeOffset LastHeard;
        public long Count;
        public float? LastRssiDbm;
        public float? LastSnrDb;

        // The live rolling window (created on the first measured sample) and the stored
        // median/count it maintains. Hydration restores median/count for display only —
        // the window restarts empty, so the first post-restart sample resets the count.
        public PreDataCarrierWindow? PreData;
        public float? MedianPreDataCarrierMs;
        public int PreDataCarrierSamples;

        public Entry(DateTimeOffset at)
        {
            FirstHeard = at;
            LastHeard = at;
        }

        public static Entry From(HeardEntry e) => new(e.FirstHeard)
        {
            PortId = e.PortId,
            Callsign = e.Callsign,
            FirstHeard = e.FirstHeard,
            LastHeard = e.LastHeard,
            Count = e.Count,
            LastRssiDbm = e.LastRssiDbm,
            LastSnrDb = e.LastSnrDb,
            MedianPreDataCarrierMs = e.MedianPreDataCarrierMs,
            PreDataCarrierSamples = e.PreDataCarrierSamples,
        };
    }
}

/// <summary>
/// A station merged across every port it was heard on — the node-wide MHeard view
/// (<see cref="HeardLog.NodeWide"/>). The per-port rows are <see cref="HeardEntry"/>.
/// </summary>
/// <param name="Callsign">The station callsign.</param>
/// <param name="FirstHeard">Earliest first-heard across all ports.</param>
/// <param name="LastHeard">Latest last-heard across all ports.</param>
/// <param name="Count">Total frames heard across all ports.</param>
/// <param name="Ports">Number of distinct ports the station was heard on.</param>
/// <param name="LastRssiDbm">Last-heard RSSI (dBm) from the port that heard this station most
/// recently, or <c>null</c> when none of those ports had a radio measuring it.</param>
/// <param name="LastSnrDb">Last-heard SNR (dB) from the port that heard this station most recently,
/// or <c>null</c> when none of those ports had a radio measuring it.</param>
/// <param name="MedianPreDataCarrierMs">Rolling median measured pre-data carrier (ms) from the port
/// that heard this station most recently (the merge follows last-heard, like the RSSI/SNR fields),
/// or <c>null</c> when never measured.</param>
/// <param name="PreDataCarrierSamples">Samples behind <see cref="MedianPreDataCarrierMs"/> on that
/// port; 0 when never measured.</param>
public sealed record HeardStationSummary(
    string Callsign,
    DateTimeOffset FirstHeard,
    DateTimeOffset LastHeard,
    long Count,
    int Ports,
    float? LastRssiDbm = null,
    float? LastSnrDb = null,
    float? MedianPreDataCarrierMs = null,
    int PreDataCarrierSamples = 0);
