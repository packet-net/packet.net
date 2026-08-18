using System.Threading.Channels;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Packet.Node.Core.Api;
using Packet.Node.Core.Configuration;
using Packet.Node.Core.Telemetry;

namespace Packet.Node.Core.Traffic;

/// <summary>
/// The traffic-log writer: subscribes to the node's existing frame-trace stream
/// (<see cref="NodeTelemetry"/> - the same tap the web monitor's SSE feed rides,
/// so there is no second decode path) and persists every traced frame to the
/// <see cref="SqliteTrafficStore"/> in batches, with a periodic
/// <see cref="TimeProvider"/>-driven prune enforcing the configured retention +
/// size bounds.
/// </summary>
/// <remarks>
/// <para>
/// <b>The radio path can never be back-pressured.</b> Frames flow telemetry →
/// a bounded hand-off queue → the single writer loop. The forwarder does nothing
/// but a non-blocking <see cref="ChannelWriter{T}.TryWrite"/>; when the queue is
/// full (a slow/stalled disk) the frame is <b>dropped from the log</b> and
/// counted (<see cref="DroppedFrames"/>, surfaced in <c>GET /api/v1/status</c>
/// and logged) - never queued unboundedly, never blocked on. The telemetry
/// subscription itself is the same drop-oldest bounded channel the SSE feed
/// uses, so even a stalled forwarder cannot touch a pump thread.
/// </para>
/// <para>
/// <b>Bounds are read live.</b> <see cref="TrafficConfig.RetentionDays"/> and
/// <see cref="TrafficConfig.MaxMb"/> are re-read from <see cref="IConfigProvider.Current"/>
/// at every prune pass, so tightening them is a hot config edit;
/// <see cref="TrafficConfig.Enabled"/>/<see cref="TrafficConfig.Path"/> are
/// startup-bound (the composition root constructs this service only when enabled).
/// </para>
/// </remarks>
public sealed partial class TrafficLogService : BackgroundService
{
    /// <summary>Default bound on the store hand-off queue. Generous (frames are
    /// small) but finite - the whole point is that a stalled disk drops log rows
    /// instead of growing the heap.</summary>
    public const int DefaultQueueCapacity = 4096;

    private const int MaxBatch = 256;
    private static readonly TimeSpan PruneInterval = TimeSpan.FromMinutes(5);

    private readonly NodeTelemetry telemetry;
    private readonly SqliteTrafficStore store;
    private readonly IConfigProvider config;
    private readonly TimeProvider clock;
    private readonly ILogger<TrafficLogService> logger;
    private readonly Channel<MonitorEvent> queue;

    private long dropped;
    private long lastReportedDropped;   // writer-loop-only.

    public TrafficLogService(
        NodeTelemetry telemetry,
        SqliteTrafficStore store,
        IConfigProvider config,
        TimeProvider clock,
        ILogger<TrafficLogService>? logger = null,
        int queueCapacity = DefaultQueueCapacity)
    {
        ArgumentNullException.ThrowIfNull(telemetry);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentOutOfRangeException.ThrowIfLessThan(queueCapacity, 1);

        this.telemetry = telemetry;
        this.store = store;
        this.config = config;
        this.clock = clock;
        this.logger = logger ?? NullLogger<TrafficLogService>.Instance;
        // FullMode.Wait makes TryWrite return false when full - the overflow signal
        // the drop counter rides (DropOldest/DropWrite would "succeed" silently).
        queue = Channel.CreateBounded<MonitorEvent>(new BoundedChannelOptions(queueCapacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = true,
        });
    }

    /// <summary>Frames dropped from the traffic log because the hand-off queue was
    /// full (a slow disk) - the log's loss counter, never the radio path's.</summary>
    public long DroppedFrames => Volatile.Read(ref dropped);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Don't hold up host start on the first prune / store touch.
        await Task.Yield();

        using var subscription = telemetry.Subscribe(out var reader);
        using var pruneTimer = clock.CreateTimer(_ => PruneOnce(), state: null, PruneInterval, PruneInterval);

        // Enforce the bounds once at startup so a node that was down past its
        // retention window (or whose caps were tightened while off) catches up
        // immediately instead of after the first interval.
        PruneOnce();

        var forward = ForwardAsync(reader, stoppingToken);
        var write = WriteAsync(stoppingToken);
        await Task.WhenAll(forward, write).ConfigureAwait(false);
    }

    // ─── telemetry → queue (non-blocking; the only place frames are dropped) ───

    private async Task ForwardAsync(ChannelReader<MonitorEvent> reader, CancellationToken ct)
    {
        try
        {
            await foreach (var evt in reader.ReadAllAsync(ct).ConfigureAwait(false))
            {
                TryEnqueue(evt);
            }
        }
        catch (OperationCanceledException)
        {
            // Shutdown.
        }
        finally
        {
            queue.Writer.TryComplete();
        }
    }

    /// <summary>Hand one traced frame to the writer. Non-blocking by construction:
    /// a full queue drops the frame from the log and bumps <see cref="DroppedFrames"/>.
    /// Internal as the overflow test seam (InternalsVisibleTo Packet.Node.Tests).</summary>
    internal bool TryEnqueue(MonitorEvent evt)
    {
        if (queue.Writer.TryWrite(evt))
        {
            return true;
        }
        Interlocked.Increment(ref dropped);
        return false;
    }

    // ─── queue → store (batched; the only place SQLite is written) ─────────────

    private async Task WriteAsync(CancellationToken ct)
    {
        var batch = new List<TrafficRecord>(MaxBatch);
        try
        {
            while (await queue.Reader.WaitToReadAsync(ct).ConfigureAwait(false))
            {
                batch.Clear();
                while (batch.Count < MaxBatch && queue.Reader.TryRead(out var evt))
                {
                    batch.Add(TrafficRecord.From(evt));
                }
                if (batch.Count > 0)
                {
                    store.Append(batch);   // logs + degrades internally; a lost batch is never retried.
                    ReportDrops();
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Shutdown - any undrained rows are dropped (it's a diagnostic log).
        }
    }

    private void ReportDrops()
    {
        long total = Volatile.Read(ref dropped);
        if (total > lastReportedDropped)
        {
            LogDropped(total - lastReportedDropped, total);
            lastReportedDropped = total;
        }
    }

    // ─── periodic prune (TimeProvider-driven; bounds re-read live) ─────────────

    private void PruneOnce()
    {
        try
        {
            var traffic = config.Current.Traffic;
            var cutoff = clock.GetUtcNow().AddDays(-traffic.RetentionDays);
            int aged = store.PruneOlderThan(cutoff);
            int sized = store.PruneToSize(traffic.MaxMb * 1024L * 1024L);
            long totalDropped = Volatile.Read(ref dropped);
            if (aged > 0 || sized > 0)
            {
                LogPruned(aged, sized, totalDropped);
            }
        }
        catch (Exception ex)
        {
            // Like NetRomService.SaveSnapshot: a prune fault may never disturb the node.
            LogPruneFault(ex);
        }
    }

    /// <summary>Test seam (InternalsVisibleTo Packet.Node.Tests): run one prune
    /// pass deterministically - the same code the periodic timer fires.</summary>
    internal void PruneNow() => PruneOnce();

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "Traffic log: dropped {Dropped} frame(s) (writer behind - slow disk?); {Total} dropped since start. The radio path is unaffected.")]
    private partial void LogDropped(long dropped, long total);

    [LoggerMessage(Level = LogLevel.Information,
        Message = "Traffic log: pruned {Aged} row(s) past retention and {Sized} row(s) over the size cap ({Dropped} frames dropped since start).")]
    private partial void LogPruned(int aged, int sized, long dropped);

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "Traffic log: prune pass faulted (will retry on the next interval).")]
    private partial void LogPruneFault(Exception ex);
}
