using Packet.Core;
using Packet.NetRom.Wire;
using Packet.Node.Core.Applications.Packages;
using Packet.Node.Core.Configuration;

namespace Packet.Node.Core.NetRom;

/// <summary>
/// Builds the opt-in app NET/ROM advert entries (<c>docs/app-packages.md</c> § Application
/// packet identity) the node appends to its NODES broadcast. The contract is off-by-default: an
/// app contributes an entry <b>only</b> when its owner set <c>netrom.alias</c>. Each entry maps
/// the alias → the app's node-resolved callsign, advertised AT this node (best-neighbour =
/// <paramref name="nodeCall"/>) at the configured quality, so a station that <c>C</c>s the alias
/// routes to us and then to the app. Pure projection - feeds
/// <see cref="NetRomService.AppAdvertSource"/>.
/// </summary>
public static class AppNetromAdvert
{
    /// <summary>
    /// Project the enabled apps in <paramref name="config"/> (inline + the discovered
    /// <paramref name="packages"/> union) to NODES advert entries - one per app with an
    /// <c>netrom.alias</c> set and a resolved callsign. Apps without an alias contribute nothing
    /// (the anti-noise default). Deterministic: inline first (config order), then packages by id.
    /// </summary>
    /// <param name="config">The live node config (inline app list + per-app netrom/pins).</param>
    /// <param name="packages">The discovered packages (the catalog's discover result); empty when
    /// there is no catalog.</param>
    /// <param name="nodeCall">The node's own callsign - the best-neighbour every app advert points
    /// through (the app is one hop away, directly via us).</param>
    public static IReadOnlyList<NodesBroadcastBuilder.Entry> Build(
        NodeConfig config, IReadOnlyList<DiscoveredAppPackage> packages, Callsign nodeCall)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(packages);

        var callsigns = AppCallsignResolver.Resolve(config, packages);
        var entries = new List<NodesBroadcastBuilder.Entry>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void Add(string id, AppNetromConfig? netrom)
        {
            if (netrom is null || string.IsNullOrWhiteSpace(netrom.Alias))
            {
                return;   // opt-in: no alias ⇒ nothing advertised.
            }
            if (!callsigns.TryGetValue(id, out var resolved))
            {
                return;   // an alias must point at a callsign; the app had none resolvable.
            }
            var alias = netrom.Alias!.Trim();
            if (!seen.Add(alias))
            {
                return;   // a duplicate alias is a config error (the validator flags it) - advertise once.
            }
            var quality = (byte)Math.Clamp(netrom.Quality ?? AppNetromConfig.DefaultQuality, 0, 255);
            entries.Add(new NodesBroadcastBuilder.Entry(resolved.Callsign, alias, nodeCall, quality));
        }

        foreach (var app in config.Applications)
        {
            if (app.Enabled && !string.IsNullOrWhiteSpace(app.Id))
            {
                Add(app.Id, app.Netrom);
            }
        }
        foreach (var pkg in packages.Where(p => p.Enabled && p.Error is null)
                     .OrderBy(p => p.Id, StringComparer.Ordinal))
        {
            Add(pkg.Id, pkg.Override?.Netrom);
        }

        return entries;
    }
}
