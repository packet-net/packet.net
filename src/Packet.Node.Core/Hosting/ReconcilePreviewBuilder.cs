using Packet.Node.Core.Api;
using Packet.Node.Core.Configuration;

namespace Packet.Node.Core.Hosting;

/// <summary>
/// Turns the field-level <see cref="ReconcilePlan"/> (the "decide" half of hot
/// reconfiguration) into the operator-facing <see cref="ReconcilePreview"/> the web
/// config editor shows before applying — changes grouped by how disruptive they are
/// (apply live · restart a port · restart the node), each a plain-language line that
/// never leaks an internal type name.
/// </summary>
/// <remarks>
/// The <see cref="ReconcilePlanner"/> covers what the running node reconciles
/// <b>without a process restart</b> (ports, telnet, service text, the callsign reset).
/// Two edited areas it does not hot-apply — the identity alias/grid (cosmetic, read
/// live by the status API) and the whole NET/ROM block (the service is built once at
/// start) — are diffed here directly: the cosmetic ones as <c>live</c>, and NET/ROM as
/// <c>node-reset</c> with an honest "applies after a node restart" note rather than
/// pretending it took effect live.
/// </remarks>
public static class ReconcilePreviewBuilder
{
    private const string Live = "live";
    private const string PortRestart = "port-restart";
    private const string NodeReset = "node-reset";

    /// <summary>Build the preview of moving from <paramref name="from"/> to
    /// <paramref name="to"/>. Assumes both are valid (the caller validates first and
    /// returns a 422 instead when they aren't), so <see cref="ReconcilePreview.Valid"/>
    /// is always true here.</summary>
    public static ReconcilePreview Build(NodeConfig from, NodeConfig to)
    {
        ArgumentNullException.ThrowIfNull(from);
        ArgumentNullException.ThrowIfNull(to);

        var plan = ReconcilePlanner.Plan(from, to);
        var live = new List<ReconcileChange>();
        var portRestart = new List<ReconcileChange>();
        var nodeReset = new List<ReconcileChange>();

        if (plan.NodeWideReset)
        {
            nodeReset.Add(new ReconcileChange(
                "identity.callsign", NodeReset,
                $"Station callsign → {to.Identity.Callsign}: every port restarts and all sessions end."));
        }
        else
        {
            // Cosmetic identity fields are read live by the status API (no on-air effect).
            if (!string.Equals(from.Identity.Alias, to.Identity.Alias, StringComparison.Ordinal))
            {
                live.Add(new ReconcileChange("identity.alias", Live, $"Alias → {Show(to.Identity.Alias)}."));
            }
            if (!string.Equals(from.Identity.Grid, to.Identity.Grid, StringComparison.Ordinal))
            {
                live.Add(new ReconcileChange("identity.grid", Live, $"Locator → {Show(to.Identity.Grid)}."));
            }
        }

        foreach (var p in plan.ToBringUp)
        {
            portRestart.Add(new ReconcileChange($"ports.{p.Id}", PortRestart, $"Port '{p.Id}' added and brought up."));
        }
        foreach (var p in plan.ToEnable)
        {
            portRestart.Add(new ReconcileChange($"ports.{p.Id}", PortRestart, $"Port '{p.Id}' enabled and brought up."));
        }
        foreach (var id in plan.ToTearDown)
        {
            portRestart.Add(new ReconcileChange($"ports.{id}", PortRestart, $"Port '{id}' removed (torn down)."));
        }
        foreach (var id in plan.ToDisable)
        {
            portRestart.Add(new ReconcileChange($"ports.{id}", PortRestart, $"Port '{id}' disabled (torn down)."));
        }
        foreach (var p in plan.ToRestart)
        {
            portRestart.Add(new ReconcileChange($"ports.{p.Id}", PortRestart, $"Port '{p.Id}' restarted (transport/identity change)."));
        }
        foreach (var p in plan.KissParamsChanged)
        {
            live.Add(new ReconcileChange($"ports.{p.Id}.kiss", Live, $"Port '{p.Id}' KISS timing applied live (no restart)."));
        }
        foreach (var p in plan.Ax25ParamsChanged)
        {
            live.Add(new ReconcileChange($"ports.{p.Id}.ax25", Live, $"Port '{p.Id}' AX.25 timers reseeded live (new sessions use them)."));
        }
        foreach (var p in plan.CompatChanged)
        {
            live.Add(new ReconcileChange($"ports.{p.Id}.compat", Live,
                $"Port '{p.Id}' AX.25 compatibility profile applied live (inbound parsing from the next frame; session quirks for new sessions)."));
        }
        foreach (var p in plan.NetRomQualityChanged)
        {
            live.Add(new ReconcileChange($"ports.{p.Id}.netRom", Live,
                $"Port '{p.Id}' NET/ROM settings (quality / minQuality / nodesPaclen) applied live (the next NODES ingest/broadcast on this port uses them)."));
        }

        if (plan.TelnetChanged)
        {
            portRestart.Add(new ReconcileChange("management.telnet", PortRestart, "Telnet console restarted (existing telnet sessions drop)."));
        }
        if (plan.ServicesChanged)
        {
            live.Add(new ReconcileChange("services", Live, "Banner/prompt updated (applies to new sessions)."));
        }

        // The NET/ROM block is not hot-reconciled — the service is constructed once at
        // start — so an edit persists but takes effect on the next restart. Be honest
        // about that rather than implying it went live.
        if (!from.NetRom.Equals(to.NetRom))
        {
            nodeReset.Add(new ReconcileChange("netRom", NodeReset, "NET/ROM settings changed — applies after a node restart."));
        }

        return new ReconcilePreview(Valid: true, Live: live, PortRestart: portRestart, NodeReset: nodeReset);
    }

    private static string Show(string? s) => string.IsNullOrEmpty(s) ? "(none)" : s;
}
