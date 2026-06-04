using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Packet.Ax25;
using Packet.Ax25.Session;
using Packet.Core;
using Packet.NetRom.Routing;
using Packet.NetRom.Wire;
using Packet.Node.Core.Configuration;

namespace Packet.Node.Core.NetRom;

/// <summary>
/// The node-level NET/ROM service: a singleton above the <see cref="Hosting.PortSupervisor"/>
/// that hears NODES routing broadcasts on every AX.25 port and maintains a
/// <see cref="NetRomRoutingTable"/>. This is the read-only "NET/ROM aware" slice —
/// it parses what it hears, builds routes, and surfaces them via
/// <see cref="INetRomRoutingView"/>; it <b>originates nothing on the air</b>.
/// </summary>
/// <remarks>
/// <para>
/// <b>It cannot disturb a QSO.</b> The only thing it touches on a port is the
/// existing <see cref="Ax25Listener.FrameTraced"/> event — a pure observation
/// tap that fires for every parsed inbound frame <em>before</em> address
/// filtering (so it hears NODES broadcasts, which are addressed to the literal
/// callsign <c>NODES</c>, not to us) and never gates, delays, or alters frame
/// handling. The handler only reads; it sends nothing and posts nothing into any
/// session. A throw in the handler is isolated by the listener's per-subscriber
/// guard, but the handler is written not to throw regardless.
/// </para>
/// <para>
/// The supervisor calls <see cref="AttachPort"/> as each port comes up and
/// <see cref="DetachPort"/> as it goes down, so the service follows the live port
/// set across hot reconfiguration without holding a port reference past teardown.
/// A periodic <see cref="Sweep"/> (driven by an injected
/// <see cref="TimeProvider"/> timer — no wall-clock, §2.7) ages routes out via
/// the obsolescence count.
/// </para>
/// </remarks>
public sealed partial class NetRomService : INetRomRoutingView, IDisposable
{
    private readonly NetRomConfig config;
    private readonly NetRomRoutingTable table;
    private readonly TimeProvider timeProvider;
    private readonly ILogger<NetRomService> logger;
    private readonly ITimer? sweepTimer;

    // Live port attachments: portId -> (myCall, the handler we subscribed so we
    // can unsubscribe on detach). Concurrent because attach/detach run on the
    // reconcile worker while FrameTraced fires on listener pump threads.
    private readonly ConcurrentDictionary<string, Attachment> attachments = new(StringComparer.Ordinal);
    private int disposed;

    /// <inheritdoc/>
    public bool Enabled => config.Enabled;

    /// <summary>Construct the service from the node's NET/ROM config.</summary>
    public NetRomService(NetRomConfig config, TimeProvider? timeProvider = null, ILogger<NetRomService>? logger = null)
    {
        this.config = config ?? throw new ArgumentNullException(nameof(config));
        this.timeProvider = timeProvider ?? TimeProvider.System;
        this.logger = logger ?? NullLogger<NetRomService>.Instance;

        var options = ResolveOptions(config);
        table = new NetRomRoutingTable(options, this.timeProvider);

        if (config.Enabled)
        {
            var interval = TimeSpan.FromSeconds(config.SweepIntervalSeconds is > 0 ? config.SweepIntervalSeconds.Value : 3600);
            // Fire the first sweep one interval in, then every interval. The
            // callback only ages the table; it never transmits.
            sweepTimer = this.timeProvider.CreateTimer(_ => OnSweep(), state: null, dueTime: interval, period: interval);
        }
    }

    private static NetRomRoutingOptions ResolveOptions(NetRomConfig config)
    {
        var d = NetRomRoutingOptions.Default;
        return d with
        {
            DefaultNeighbourQuality = config.DefaultNeighbourQuality ?? d.DefaultNeighbourQuality,
            MinQuality = config.MinQuality ?? d.MinQuality,
            ObsoleteInitial = config.ObsoleteInitial ?? d.ObsoleteInitial,
        };
    }

    /// <summary>
    /// Begin hearing NODES broadcasts on a port. Subscribes the port's
    /// <see cref="Ax25Listener.FrameTraced"/> tap. No-op if NET/ROM is disabled or
    /// the port is already attached. Safe to call from the reconcile worker.
    /// </summary>
    /// <param name="portId">The node-host port id (used for neighbour tracking).</param>
    /// <param name="myCall">The port's local callsign (for the trivial-loop guard).</param>
    /// <param name="listener">The port's AX.25 listener.</param>
    public void AttachPort(string portId, Callsign myCall, Ax25Listener listener)
    {
        ArgumentNullException.ThrowIfNull(portId);
        ArgumentNullException.ThrowIfNull(listener);
        if (!config.Enabled || Volatile.Read(ref disposed) != 0)
        {
            return;
        }

        // Capture the handler so detach can unsubscribe exactly it.
        void Handler(object? sender, Ax25FrameEventArgs e) => OnFrameTraced(portId, myCall, e);

        var attachment = new Attachment(myCall, listener, Handler);
        if (!attachments.TryAdd(portId, attachment))
        {
            return;   // already attached (e.g. a re-attach without a detach) — leave the first
        }

        listener.FrameTraced += Handler;
        var callText = myCall.ToString();
        LogAttached(portId, callText);
    }

    /// <summary>
    /// Stop hearing NODES broadcasts on a port and unsubscribe its tap. No-op if
    /// the port was not attached. Safe to call from the reconcile worker. Learned
    /// routes survive — a torn-down port doesn't wipe the table; obsolescence ages
    /// its neighbours out naturally.
    /// </summary>
    public void DetachPort(string portId)
    {
        ArgumentNullException.ThrowIfNull(portId);
        if (attachments.TryRemove(portId, out var attachment))
        {
            attachment.Listener.FrameTraced -= attachment.Handler;
            LogDetached(portId);
        }
    }

    /// <inheritdoc/>
    public NetRomRoutingSnapshot Snapshot()
        => config.Enabled ? table.Snapshot() : NetRomRoutingSnapshot.Empty;

    /// <summary>
    /// Age the routing table by one obsolescence tick. Exposed so a test can drive
    /// the decay deterministically without waiting on the timer.
    /// </summary>
    public int Sweep() => config.Enabled ? table.Sweep() : 0;

    // ─── The read-only tap ────────────────────────────────────────────

    private void OnFrameTraced(string portId, Callsign myCall, Ax25FrameEventArgs e)
    {
        // Defensive: the listener already isolates a throwing subscriber, but a
        // read-only consumer must never be the reason a frame's trace fan-out
        // misbehaves. Swallow anything unexpected.
        try
        {
            // Only inbound frames carry NODES we should learn from; our own TX of a
            // NODES broadcast (a future write slice) must not feed our own table.
            if (e.Direction != FrameDirection.Received)
            {
                return;
            }

            var frame = e.Frame;

            // NODES broadcasts are UI frames, PID 0xCF, AX.25 destination the literal
            // text callsign "NODES". Cheap gate first.
            if (!frame.IsUi)
            {
                return;
            }
            if (frame.Pid != Ax25Frame.PidNetRom)
            {
                return;
            }
            if (!IsNodesDestination(frame.Destination.Callsign))
            {
                return;
            }

            var originator = frame.Source.Callsign;
            var originatorText = originator.ToString();

            if (!NodesBroadcast.TryParse(frame.Info.Span, out var broadcast))
            {
                LogUnparseable(portId, originatorText);
                return;
            }

            table.Ingest(originator, myCall, portId, broadcast);
            LogHeard(portId, originatorText, broadcast.SenderAlias, broadcast.Entries.Count);
        }
        catch (Exception ex)
        {
            LogTapFault(ex, portId);
        }
    }

    // The NODES destination is the literal 6-char-or-fewer text "NODES" with SSID 0.
    private static bool IsNodesDestination(Callsign dest)
        => dest.Ssid == 0 && string.Equals(dest.Base, NodesBroadcast.NodesDestination, StringComparison.Ordinal);

    private void OnSweep()
    {
        try
        {
            int purged = Sweep();
            if (purged > 0)
            {
                LogSwept(purged);
            }
        }
        catch (Exception ex)
        {
            LogSweepFault(ex);
        }
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0)
        {
            return;
        }
        sweepTimer?.Dispose();
        foreach (var portId in attachments.Keys.ToArray())
        {
            DetachPort(portId);
        }
    }

    private sealed record Attachment(Callsign MyCall, Ax25Listener Listener, EventHandler<Ax25FrameEventArgs> Handler);

    [LoggerMessage(Level = LogLevel.Information, Message = "NET/ROM: listening for NODES on port {PortId} (as {Callsign}).")]
    private partial void LogAttached(string portId, string callsign);

    [LoggerMessage(Level = LogLevel.Information, Message = "NET/ROM: stopped listening on port {PortId}.")]
    private partial void LogDetached(string portId);

    [LoggerMessage(Level = LogLevel.Debug, Message = "NET/ROM: heard NODES from {Originator} (alias {Alias}) on {PortId} with {EntryCount} entr(ies).")]
    private partial void LogHeard(string portId, string originator, string alias, int entryCount);

    [LoggerMessage(Level = LogLevel.Debug, Message = "NET/ROM: NODES from {Originator} on {PortId} did not parse; ignored.")]
    private partial void LogUnparseable(string portId, string originator);

    [LoggerMessage(Level = LogLevel.Debug, Message = "NET/ROM: obsolescence sweep purged {Purged} route(s).")]
    private partial void LogSwept(int purged);

    [LoggerMessage(Level = LogLevel.Warning, Message = "NET/ROM: frame tap on port {PortId} faulted (frame ignored).")]
    private partial void LogTapFault(Exception ex, string portId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "NET/ROM: obsolescence sweep faulted.")]
    private partial void LogSweepFault(Exception ex);
}
