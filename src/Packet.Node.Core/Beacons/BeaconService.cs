using System.Collections.Concurrent;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Packet.Ax25;
using Packet.Core;
using Packet.Node.Core.Configuration;
using Packet.Node.Core.Console;

namespace Packet.Node.Core.Beacons;

/// <summary>
/// The node's periodic ID beacon: a singleton that, per attached port, arms a
/// <see cref="TimeProvider"/>-driven timer and transmits a connectionless AX.25 UI
/// frame (dest the literal <c>BEACON</c> callsign, PID
/// <see cref="Ax25Frame.PidNoLayer3"/>) carrying the port's expanded beacon text.
/// </summary>
/// <remarks>
/// <para>
/// <b>Default-OFF, no regression.</b> The effective beacon for a port is the per-port
/// override (<see cref="PortConfig.Beacon"/>) merged over the system default
/// (<see cref="NodeConfig.Beacon"/>) — see <see cref="EffectiveBeacon.Resolve"/>.
/// Both default to <see cref="BeaconConfig.Enabled"/> = <c>false</c>, so a node that
/// never configured a beacon never arms a timer and never transmits — byte-for-byte as
/// before beacons existed. The service only ever <see cref="IBeaconChannel.SendUiAsync"/>es
/// when the effective beacon is enabled.
/// </para>
/// <para>
/// <b>Lifecycle.</b> <see cref="AttachPort"/> / <see cref="DetachPort"/> mirror
/// <c>NodeTelemetry.AttachPort/DetachPort</c> and the NET/ROM attach lifecycle — the
/// supervisor calls them as ports come up and go down. On attach, if the effective
/// beacon is enabled, a periodic timer is armed; on detach the timer is stopped and
/// disposed.
/// </para>
/// <para>
/// <b>Hot-reload.</b> The effective beacon is resolved <em>live</em> from
/// <see cref="IConfigProvider.Current"/> at attach and at each <see cref="Reapply"/>,
/// so a config edit that changes the interval / text / enabled flag re-arms cleanly.
/// The host calls <see cref="Reapply"/> after every reconcile (a beacon-only edit is a
/// no-op for the port supervisor's reconcile plan, so the host must re-arm explicitly —
/// the same way the console reads <c>ServicesConfig</c> live).
/// </para>
/// <para>
/// <b>Never throws onto the timer.</b> The timer callback awaits the send inside an
/// async method and swallows + logs any fault (like <c>NodeTelemetry.Observe</c>), so a
/// modem hiccup can never crash the timer or leave an unobserved Task. A self-overlap
/// guard skips a tick if the previous send is still in flight (a wedged modem can't pile
/// up sends).
/// </para>
/// </remarks>
public sealed partial class BeaconService : IAsyncDisposable
{
    /// <summary>The literal AX.25 destination callsign for an ID beacon.</summary>
    private static readonly Callsign BeaconDestination = Callsign.Parse("BEACON");

    private readonly IConfigProvider config;
    private readonly TimeProvider timeProvider;
    private readonly ILogger<BeaconService> logger;

    // Live per-port attachments. Concurrent because attach/detach/reapply run on the
    // reconcile worker while the timer callbacks fire on TimeProvider timer threads.
    private readonly ConcurrentDictionary<string, PortBeacon> ports = new(StringComparer.Ordinal);

    private int disposed;

    public BeaconService(IConfigProvider config, TimeProvider? timeProvider = null, ILogger<BeaconService>? logger = null)
    {
        this.config = config ?? throw new ArgumentNullException(nameof(config));
        this.timeProvider = timeProvider ?? TimeProvider.System;
        this.logger = logger ?? NullLogger<BeaconService>.Instance;
    }

    /// <summary>
    /// Begin beaconing on a port. Resolves the effective beacon live from the current
    /// config; arms a periodic timer only when it is enabled. No-op if already attached
    /// or the service is disposed. <paramref name="channel"/> is the UI-send seam (a
    /// live <see cref="ListenerBeaconChannel"/> in production).
    /// </summary>
    public void AttachPort(string portId, IBeaconChannel channel)
    {
        ArgumentNullException.ThrowIfNull(portId);
        ArgumentNullException.ThrowIfNull(channel);
        if (Volatile.Read(ref disposed) != 0)
        {
            return;
        }

        var port = new PortBeacon(portId, channel);
        if (!ports.TryAdd(portId, port))
        {
            return;
        }
        Arm(port);
    }

    /// <summary>Stop beaconing on a port and dispose its timer. No-op if unattached.</summary>
    public void DetachPort(string portId)
    {
        ArgumentNullException.ThrowIfNull(portId);
        if (ports.TryRemove(portId, out var port))
        {
            port.DisposeTimer();
        }
    }

    /// <summary>
    /// Re-arm every attached port from the current config. Called by the host after a
    /// config reconcile so a beacon-only edit (which is invisible to the port-supervisor
    /// reconcile plan) takes effect: each port re-resolves its effective beacon and the
    /// timer is re-armed (or disarmed). Idempotent in the strong sense: re-applying an
    /// unchanged beacon leaves the running timer completely alone, so a config edit
    /// elsewhere never resets a beacon's phase (and never repeats the "armed" line).
    /// </summary>
    public void Reapply()
    {
        if (Volatile.Read(ref disposed) != 0)
        {
            return;
        }
        foreach (var port in ports.Values)
        {
            Arm(port);
        }
    }

    /// <summary>Number of ports with a live (enabled) beacon timer — for tests.</summary>
    internal int ArmedCount => ports.Values.Count(p => p.IsArmed);

    // Resolve the effective beacon for the port from the LIVE config and (re-)arm or
    // disarm its timer accordingly. The interval is clamped to ≥ 1 minute defensively
    // (the validator guarantees it, but a direct-constructed config in a test might not).
    private void Arm(PortBeacon port)
    {
        var current = config.Current;
        var portConfig = current.Ports.FirstOrDefault(p => string.Equals(p.Id, port.PortId, StringComparison.Ordinal));
        var effective = EffectiveBeacon.Resolve(current.Beacon, portConfig?.Beacon);

        if (!effective.Enabled)
        {
            port.DisposeTimer();
            LogDisabled(port.PortId);
            return;
        }

        var minutes = Math.Max(1, effective.IntervalMinutes);
        var period = TimeSpan.FromMinutes(minutes);
        // Resolve {node}/{call} from the live identity; the same expansion the banner uses.
        var identity = current.Identity;
        var nodeName = NodeTextTemplate.NodeName(identity.Callsign, identity.Alias);
        var text = NodeTextTemplate.Expand(effective.Text, nodeName, identity.Callsign);

        // Only touch the timer when the effective beacon actually changed. The host calls
        // Reapply after EVERY reconcile (a KISS tweak on another port lands here too), and a
        // blind re-arm would push the next beacon a full interval out each time: a node
        // edited more often than its beacon interval would never transmit.
        var change = port.Apply(timeProvider, period, text, OnTick);
        if (change != BeaconArm.Unchanged)
        {
            LogArmed(port.PortId, minutes, text);
        }
    }

    /// <summary>What <see cref="PortBeacon.Apply"/> actually did, so <see cref="Arm"/> logs
    /// (and disturbs the schedule) only when something really changed.</summary>
    private enum BeaconArm
    {
        /// <summary>Same period, same text: the running timer was left exactly as it was.</summary>
        Unchanged,

        /// <summary>Only the text changed: swapped in place, the timer (and its phase) kept.</summary>
        TextChanged,

        /// <summary>The timer was created or retuned to a new period.</summary>
        Rearmed,
    }

    // The periodic timer callback. Async + fault-swallowing: a send fault is logged, not
    // thrown (a beacon bug must never crash the timer or surface an unobserved Task). The
    // per-port self-overlap guard means a wedged send is skipped rather than queued.
    private async Task OnTick(PortBeacon port)
    {
        if (Volatile.Read(ref disposed) != 0)
        {
            return;
        }
        if (!port.TryBeginSend())
        {
            // Previous beacon still in flight (a stalled modem) — skip this tick.
            return;
        }
        try
        {
            var text = port.Text;
            var info = Encoding.UTF8.GetBytes(text);
            await port.Channel.SendUiAsync(BeaconDestination, info, Ax25Frame.PidNoLayer3).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            LogSendFault(ex, port.PortId);
        }
        finally
        {
            port.EndSend();
        }
    }

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0)
        {
            return;
        }
        foreach (var port in ports.Values)
        {
            port.DisposeTimer();
        }
        ports.Clear();
        await ValueTask.CompletedTask.ConfigureAwait(false);
    }

    // One attached port: its send seam plus the live timer + expanded text + a one-shot
    // in-flight guard. Mutations are guarded by the instance lock so attach/reapply (which
    // re-arm) can't race the timer thread.
    private sealed class PortBeacon(string portId, IBeaconChannel channel)
    {
        private readonly object gate = new();
        private ITimer? timer;
        private int sending;

        // The period the live timer is currently armed with (Zero when disarmed). Compared on
        // every Apply so an unchanged beacon is left running untouched.
        private TimeSpan armedPeriod;

        public string PortId { get; } = portId;
        public IBeaconChannel Channel { get; } = channel;

        // Read by the timer callback; written under the gate on (re)arm. Volatile so the
        // callback thread sees the latest text after a hot reconfigure.
        private volatile string text = string.Empty;
        public string Text => text;

        public bool IsArmed
        {
            get { lock (gate)
                {
                    return timer is not null;
                }
            }
        }

        // Bring the timer in line with the wanted (period, text), doing the LEAST that gets
        // there:
        //   * nothing armed        -> create the timer (first beacon one interval from now);
        //   * same period + text   -> leave it completely alone (phase preserved);
        //   * text only changed    -> swap the volatile string the callback reads, timer kept;
        //   * period changed       -> ITimer.Change on the SAME timer (a new interval has no
        //                             meaning against the old phase, so the due time moves).
        public BeaconArm Apply(TimeProvider clock, TimeSpan period, string newText, Func<PortBeacon, Task> onTick)
        {
            lock (gate)
            {
                if (timer is null)
                {
                    text = newText;
                    armedPeriod = period;
                    timer = clock.CreateTimer(_ => _ = onTick(this), state: null, dueTime: period, period: period);
                    return BeaconArm.Rearmed;
                }

                bool periodChanged = armedPeriod != period;
                bool textChanged = !string.Equals(text, newText, StringComparison.Ordinal);
                if (!periodChanged && !textChanged)
                {
                    return BeaconArm.Unchanged;
                }

                text = newText;
                if (!periodChanged)
                {
                    return BeaconArm.TextChanged;
                }

                armedPeriod = period;
                timer.Change(period, period);
                return BeaconArm.Rearmed;
            }
        }

        public void DisposeTimer()
        {
            lock (gate)
            {
                timer?.Dispose();
                timer = null;
                armedPeriod = TimeSpan.Zero;
            }
        }

        // Returns true if this caller may send (no send in flight); the caller MUST call
        // EndSend in a finally. CompareExchange so two timer ticks can't both send.
        public bool TryBeginSend() => Interlocked.CompareExchange(ref sending, 1, 0) == 0;
        public void EndSend() => Interlocked.Exchange(ref sending, 0);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Beacon armed on port {PortId}: every {Minutes} min - \"{Text}\".")]
    private partial void LogArmed(string portId, int minutes, string text);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Beacon disabled on port {PortId} (no timer).")]
    private partial void LogDisabled(string portId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Beacon transmit on port {PortId} faulted (beacon skipped).")]
    private partial void LogSendFault(Exception ex, string portId);
}
