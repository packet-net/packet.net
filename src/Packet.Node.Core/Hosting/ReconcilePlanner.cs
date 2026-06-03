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
/// <item><b>Enabled toggled</b> → bring up / tear down that port.</item>
/// <item><b>KISS params changed</b> (only) → apply live, no restart.</item>
/// <item><b>AX.25 params changed</b> (only) → recorded for next bring-up; the
/// live listener and its sessions are untouched (the engine seeds options at
/// construction and is consumed as-is, so retroactively re-seeding a running
/// listener would mean rebuilding it — which would drop sessions, violating
/// "don't mutate live sessions"). Documented slice-1 nuance.</item>
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
            if (!Equals(oldPort.Transport, newPort.Transport))
            {
                restart.Add(newPort);   // transport change → single-port restart
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
            TelnetChanged = telnetChanged,
            ServicesChanged = servicesChanged,
        };
    }
}
