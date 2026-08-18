using Packet.Node.Core.Configuration;

namespace Packet.Node.Core.Hosting;

/// <summary>
/// Computes the <see cref="ReconcilePlan"/> between two configs - the pure
/// "decide what changed" half of hot reconfiguration. Each port is matched by
/// its stable <see cref="PortConfig.Id"/>; the per-port restart class is decided
/// by which fields differ.
/// </summary>
/// <remarks>
/// Restart classes:
/// <list type="bullet">
/// <item><b>Identity callsign changed</b> → node-wide reset (every listener
/// recreated; all sessions end).</item>
/// <item><b>Transport changed</b> (on an enabled port) → single-port restart.</item>
/// <item><b>Channel profile changed</b> (on an enabled port) → single-port restart
/// (it can move both the AX.25 timer seed and the CSMA params; restart resolves
/// the effective values cleanly).</item>
/// <item><b>Radio attachment changed</b> (<see cref="PortConfig.Radio"/>, on an
/// enabled port) → single-port restart: the radio control channel is opened and the
/// RSSI-tagging transport wrap decided at construction time.</item>
/// <item><b>Enabled toggled</b> → bring up / tear down that port.</item>
/// <item><b>KISS params changed</b> (only) → apply live, no restart.</item>
/// <item><b>AX.25 params changed</b> (only) → live-reseed, no restart: the
/// running listener's per-session parameters are updated in place
/// (<see cref="Packet.Ax25.Session.Ax25Listener.UpdateSessionParameters"/>) so
/// <em>new</em> sessions pick them up, while every existing session keeps its
/// object identity and in-flight state. (Slice 1 deferred this to the next
/// bring-up because the engine seeded options at construction only; the engine
/// now exposes a live reseed, so this class is HOT - non-disrupting.)</item>
/// <item><b>Link policy changed</b> (only) → live-reseed via the same mechanism: the
/// reseeded record carries the listener's dial defaults, and the connector reads the new
/// policy on its next dial. Future dials only; no restart.</item>
/// <item><b>Compat profile changed</b> (only) → live-reseed via the same
/// mechanism: the reseeded parameter record carries the parse options (read
/// per inbound frame, so they apply to the very next frame) and the session
/// quirks (build-time, so new sessions only). No restart.</item>
/// <item><b>Telnet bind/port/enabled changed</b> → restart the telnet listener.</item>
/// <item><b>Services text changed</b> → reference swap (read live by the console).</item>
/// </list>
/// </remarks>
public static class ReconcilePlanner
{
    /// <summary>Compute the minimal reconcile plan to move from
    /// <paramref name="from"/> to <paramref name="to"/>.</summary>
    public static ReconcilePlan Plan(NodeConfig from, NodeConfig to)
    {
        ArgumentNullException.ThrowIfNull(from);
        ArgumentNullException.ThrowIfNull(to);

        bool callsignChanged = !string.Equals(
            from.Identity.Callsign, to.Identity.Callsign, StringComparison.Ordinal);

        bool telnetChanged = !Equals(from.Management.Telnet, to.Management.Telnet);
        bool servicesChanged = !Equals(from.Services, to.Services);

        if (callsignChanged)
        {
            // Node-wide reset: every listener is recreated under the new
            // callsign. The "bring up" set is the new enabled ports; there are no
            // incremental restart/hot lists (everything restarts). Telnet only
            // restarts if its own config changed (the callsign reset doesn't bind
            // telnet differently).
            return new ReconcilePlan
            {
                NodeWideReset = true,
                ToBringUp = to.Ports.Where(p => p.Enabled).ToList(),
                TelnetChanged = telnetChanged,
                ServicesChanged = servicesChanged,
            };
        }

        var fromById = from.Ports.ToDictionary(p => p.Id, StringComparer.Ordinal);
        var toById = to.Ports.ToDictionary(p => p.Id, StringComparer.Ordinal);

        var bringUp = new List<PortConfig>();
        var tearDown = new List<string>();
        var restart = new List<PortConfig>();
        var disable = new List<string>();
        var enable = new List<PortConfig>();
        var kissChanged = new List<PortConfig>();
        var ax25Changed = new List<PortConfig>();
        var compatChanged = new List<PortConfig>();
        var linkChanged = new List<PortConfig>();
        var netRomQualityChanged = new List<PortConfig>();
        var beaconChanged = new List<PortConfig>();
        var mqttInstanceChanged = new List<PortConfig>();

        // Removed ports.
        foreach (var oldPort in from.Ports)
        {
            if (!toById.ContainsKey(oldPort.Id))
            {
                if (oldPort.Enabled)
                {
                    tearDown.Add(oldPort.Id);   // only running ports need teardown
                }
            }
        }

        // Added / changed ports.
        foreach (var newPort in to.Ports)
        {
            if (!fromById.TryGetValue(newPort.Id, out var oldPort))
            {
                if (newPort.Enabled)
                {
                    bringUp.Add(newPort);   // brand-new enabled port
                }
                continue;
            }

            // Enabled toggled.
            if (oldPort.Enabled != newPort.Enabled)
            {
                if (newPort.Enabled)
                {
                    enable.Add(newPort);
                }
                else
                {
                    disable.Add(newPort.Id);
                }

                continue;   // the toggle subsumes any field change (we rebuild on enable)
            }

            // Both disabled - nothing running, nothing to do beyond record the new
            // config (the supervisor never holds a disabled port).
            if (!newPort.Enabled)
            {
                continue;
            }

            // Both enabled - classify the field change.
            // A transport change, or a channel-profile change (which can move both
            // the AX.25 timer seed - next-bring-up only - and the CSMA params), is a
            // single-port restart. Folding profile into restart keeps the effective
            // params unambiguous: the rebuilt listener picks up the resolved values.
            // kiss.ackMode is in the same class: it decides whether the modem is wrapped
            // in the PacingKissModem decorator at construction time, so it cannot be
            // applied live (unlike the TXDELAY/PERSIST/SLOTTIME/TXTAIL knobs, which the
            // KISS-live path re-sends to the running modem). Toggling it restarts the
            // port so the change actually takes effect rather than silently no-op'ing.
            // The radio attachment (port.radio) is construction-time too: it opens a
            // serial control channel and wraps the transport in the RSSI-tagging
            // decorator at bring-up, so adding / removing / re-pointing it restarts
            // the port.
            // The rig attachment (port.rig) is construction-time as well: the CAT backend is
            // dialled and capability-probed at bring-up, so adding / removing / re-pointing it
            // restarts the port. (A hot-swap of the side-poller is possible in principle -
            // promote it to the hot class if restart churn ever matters here.)
            if (!Equals(oldPort.Transport, newPort.Transport) ||
                !string.Equals(oldPort.Profile, newPort.Profile, StringComparison.OrdinalIgnoreCase) ||
                AckModeChanged(oldPort.Kiss, newPort.Kiss) ||
                !Equals(oldPort.Radio, newPort.Radio) ||
                !Equals(oldPort.Rig, newPort.Rig))
            {
                restart.Add(newPort);   // transport / profile / ackMode / radio / rig change → single-port restart
                continue;
            }

            // Transport unchanged; check the hot-class fields independently.
            if (!Equals(oldPort.Kiss, newPort.Kiss))
            {
                kissChanged.Add(newPort);
            }
            if (!Equals(oldPort.Ax25, newPort.Ax25))
            {
                ax25Changed.Add(newPort);
            }
            if (!Equals(oldPort.Compat, newPort.Compat))
            {
                compatChanged.Add(newPort);
            }
            // Per-port link policy (dial preference / pre-connect XID): hot, like compat. It
            // decides what the NEXT outbound dial offers; a session already up keeps the version
            // it negotiated, so nothing needs restarting.
            if (!Equals(oldPort.Link, newPort.Link))
            {
                linkChanged.Add(newPort);
            }
            // Per-port NET/ROM awareness knobs (QUALITY / MINQUAL / NODESPACLEN): a hot edit
            // (NET/ROM awareness + advertisement is read-only - it never disturbs a session),
            // applied by swapping the port's attachment quality/minqual/paclen. Any of the
            // three changing schedules the same light-touch hot-apply.
            if (oldPort.NetRomQuality != newPort.NetRomQuality ||
                oldPort.NetRomMinQuality != newPort.NetRomMinQuality ||
                oldPort.NodesPaclen != newPort.NodesPaclen)
            {
                netRomQualityChanged.Add(newPort);
            }
            // The per-port ID-beacon override: hot (the beacon service re-arms its timers from
            // the live config), but it MUST appear in the plan. Without an arm a beacon-only
            // edit was a genuine no-op, so the pre-apply preview told the operator "nothing will
            // change" and the node then started keying up on a timer (#722).
            if (!Equals(oldPort.Beacon, newPort.Beacon))
            {
                beaconChanged.Add(newPort);
            }
            // The kissproxy MQTT topic label: read live per frame by MqttFrameEmitter, so
            // nothing restarts - the arm exists so the edit is not invisible to IsNoOp.
            if (!string.Equals(oldPort.MqttInstance, newPort.MqttInstance, StringComparison.Ordinal))
            {
                mqttInstanceChanged.Add(newPort);
            }
        }

        return new ReconcilePlan
        {
            ToBringUp = bringUp,
            ToTearDown = tearDown,
            ToRestart = restart,
            ToDisable = disable,
            ToEnable = enable,
            KissParamsChanged = kissChanged,
            Ax25ParamsChanged = ax25Changed,
            CompatChanged = compatChanged,
            LinkChanged = linkChanged,
            NetRomQualityChanged = netRomQualityChanged,
            BeaconChanged = beaconChanged,
            MqttInstanceChanged = mqttInstanceChanged,
            TelnetChanged = telnetChanged,
            ServicesChanged = servicesChanged,
        };
    }

    /// <summary>
    /// How a single <see cref="PortConfig"/> field is reconciled. Every public property of
    /// <see cref="PortConfig"/> is claimed by exactly one of these in
    /// <see cref="FieldClasses"/>, and a reflection test fails when a newly added field is not
    /// - so <see cref="ReconcilePlan.IsNoOp"/> genuinely means "nothing happens" rather than
    /// "nothing I remembered to compare" (packet-net/packet.net#722).
    /// </summary>
    public enum PortFieldClass
    {
        /// <summary>The reconcile key itself: old and new ports are matched by it, so a change
        /// reads as remove-the-old + add-the-new rather than a field diff.</summary>
        Key,

        /// <summary>Decides whether the port runs at all (bring up / tear down).</summary>
        Lifecycle,

        /// <summary>Construction-time: a change restarts the port.</summary>
        Restart,

        /// <summary>Applied to the running port (or read live) with no restart.</summary>
        Live,

        /// <summary>Part restart-class, part live - see the field's entry for the split.</summary>
        Mixed,
    }

    /// <summary>
    /// Which reconcile class each <see cref="PortConfig"/> field belongs to, by property name.
    /// The table is the planner's own contract with itself: <see cref="Plan"/> compares every
    /// field here that is not <see cref="PortFieldClass.Key"/>, and the exhaustiveness test
    /// asserts the key set matches <see cref="PortConfig"/>'s public properties exactly.
    /// </summary>
    public static IReadOnlyDictionary<string, PortFieldClass> FieldClasses { get; } =
        new Dictionary<string, PortFieldClass>(StringComparer.Ordinal)
        {
            [nameof(PortConfig.Id)] = PortFieldClass.Key,
            [nameof(PortConfig.Enabled)] = PortFieldClass.Lifecycle,
            [nameof(PortConfig.Transport)] = PortFieldClass.Restart,
            [nameof(PortConfig.Profile)] = PortFieldClass.Restart,
            [nameof(PortConfig.Radio)] = PortFieldClass.Restart,
            [nameof(PortConfig.Rig)] = PortFieldClass.Restart,
            // ackMode / t1FromTxComplete decide how the modem chain is BUILT (the pacing
            // decorator, the TX-complete T1 restart) so they restart the port; TXDELAY /
            // PERSIST / SLOTTIME / TXTAIL are re-sent to the running modem.
            [nameof(PortConfig.Kiss)] = PortFieldClass.Mixed,
            [nameof(PortConfig.Ax25)] = PortFieldClass.Live,
            [nameof(PortConfig.Compat)] = PortFieldClass.Live,
            [nameof(PortConfig.Beacon)] = PortFieldClass.Live,
            [nameof(PortConfig.NetRomQuality)] = PortFieldClass.Live,
            [nameof(PortConfig.NetRomMinQuality)] = PortFieldClass.Live,
            [nameof(PortConfig.NodesPaclen)] = PortFieldClass.Live,
            [nameof(PortConfig.MqttInstance)] = PortFieldClass.Live,
            // Dial policy affects new dials only; it live-reseeds through the same path as Ax25.
            [nameof(PortConfig.Link)] = PortFieldClass.Live,
        };

    // Did the ACKMODE-pacing flag flip between two ports' KISS settings? A null Kiss
    // block means ackMode defaults to false, so a present-but-false block compares
    // equal to absent here. No channel profile sets ackMode, so comparing the explicit
    // per-port config (rather than the profile-resolved value) is exact.
    private static bool AckModeChanged(KissParams? oldKiss, KissParams? newKiss)
        => (oldKiss?.AckMode ?? false) != (newKiss?.AckMode ?? false)
        // t1FromTxComplete is likewise a construction-time choice (it changes how
        // the listener sends, decided at build) - a toggle needs the restart too.
        || (oldKiss?.T1FromTxComplete ?? false) != (newKiss?.T1FromTxComplete ?? false);
}
