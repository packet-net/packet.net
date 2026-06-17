using Packet.Node.Core.Configuration;

namespace Packet.Node.Core.Hosting;

/// <summary>
/// Computes the <see cref="ReconcilePlan"/> between two configs — the pure
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
/// <item><b>Enabled toggled</b> → bring up / tear down that port.</item>
/// <item><b>KISS params changed</b> (only) → apply live, no restart.</item>
/// <item><b>AX.25 params changed</b> (only) → live-reseed, no restart: the
/// running listener's per-session parameters are updated in place
/// (<see cref="Packet.Ax25.Session.Ax25Listener.UpdateSessionParameters"/>) so
/// <em>new</em> sessions pick them up, while every existing session keeps its
/// object identity and in-flight state. (Slice 1 deferred this to the next
/// bring-up because the engine seeded options at construction only; the engine
/// now exposes a live reseed, so this class is HOT — non-disrupting.)</item>
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
        var netRomQualityChanged = new List<PortConfig>();

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
                if (newPort.Enabled) enable.Add(newPort);
                else disable.Add(newPort.Id);
                continue;   // the toggle subsumes any field change (we rebuild on enable)
            }

            // Both disabled — nothing running, nothing to do beyond record the new
            // config (the supervisor never holds a disabled port).
            if (!newPort.Enabled)
            {
                continue;
            }

            // Both enabled — classify the field change.
            // A transport change, or a channel-profile change (which can move both
            // the AX.25 timer seed — next-bring-up only — and the CSMA params), is a
            // single-port restart. Folding profile into restart keeps the effective
            // params unambiguous: the rebuilt listener picks up the resolved values.
            // kiss.ackMode is in the same class: it decides whether the modem is wrapped
            // in the PacingKissModem decorator at construction time, so it cannot be
            // applied live (unlike the TXDELAY/PERSIST/SLOTTIME/TXTAIL knobs, which the
            // KISS-live path re-sends to the running modem). Toggling it restarts the
            // port so the change actually takes effect rather than silently no-op'ing.
            if (!Equals(oldPort.Transport, newPort.Transport) ||
                !string.Equals(oldPort.Profile, newPort.Profile, StringComparison.OrdinalIgnoreCase) ||
                AckModeChanged(oldPort.Kiss, newPort.Kiss))
            {
                restart.Add(newPort);   // transport / profile / ackMode change → single-port restart
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
            // Per-port NET/ROM QUALITY: a hot edit (NET/ROM awareness is read-only — it
            // never disturbs a session), applied by swapping the port's attachment quality.
            if (oldPort.NetRomQuality != newPort.NetRomQuality)
            {
                netRomQualityChanged.Add(newPort);
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
            NetRomQualityChanged = netRomQualityChanged,
            TelnetChanged = telnetChanged,
            ServicesChanged = servicesChanged,
        };
    }

    // Did the ACKMODE-pacing flag flip between two ports' KISS settings? A null Kiss
    // block means ackMode defaults to false, so a present-but-false block compares
    // equal to absent here. No channel profile sets ackMode, so comparing the explicit
    // per-port config (rather than the profile-resolved value) is exact.
    private static bool AckModeChanged(KissParams? oldKiss, KissParams? newKiss)
        => (oldKiss?.AckMode ?? false) != (newKiss?.AckMode ?? false)
        // t1FromTxComplete is likewise a construction-time choice (it changes how
        // the listener sends, decided at build) — a toggle needs the restart too.
        || (oldKiss?.T1FromTxComplete ?? false) != (newKiss?.T1FromTxComplete ?? false);
}
