using Packet.Core;
using Packet.Node.Core.Configuration;

namespace Packet.Node.Core.Applications.Packages;

/// <summary>
/// The node is the <b>callsign authority</b> for its apps (<c>docs/app-packages.md</c>
/// § Application packet identity). This pure helper resolves, deterministically, the on-air L2
/// callsign every enabled packet-capable app should bind - a pinned <c>callsign</c> when the
/// owner set one, else an auto-assigned <c>&lt;node-base&gt;-&lt;lowest free SSID&gt;</c>. The
/// result feeds three consumers from one authority: the supervisor's <c>PDN_APP_CALLSIGN</c>
/// injection (<see cref="AppServiceSupervisor"/>), the bare command-verb loopback connect
/// (the console), and the opt-in NET/ROM alias advert (<c>NetRomService</c>).
/// </summary>
/// <remarks>
/// <para>
/// <b>Determinism.</b> The auto-assign walk processes apps in a stable order (inline entries
/// first in config order, then discovered packages by id), and at each step picks the lowest
/// SSID 1..15 not already taken by another app's resolved callsign, the node's own callsign, or
/// a pin earlier in the walk. Pins are reserved <i>before</i> any auto-assign so an auto-assigned
/// app never collides with a pinned one regardless of order. The same config in ⇒ the same map
/// out, across reconciles.
/// </para>
/// <para>
/// <b>What counts as a packet app.</b> An enabled app is resolved a callsign when it grants
/// packet access (<see cref="AppCapabilities.GrantsPacketAccess"/>) or the owner pinned a
/// <c>callsign</c> / set a <c>netrom</c> advert for it. A pure session app (e.g. WALL/LOBBY)
/// with neither stays session-only - it never bound a callsign before and gets no
/// <c>PDN_APP_CALLSIGN</c> (back-compat: such apps reach users over the session attachment, not
/// a separate L2 identity).
/// </para>
/// </remarks>
public static class AppCallsignResolver
{
    /// <summary>The SSID range an auto-assignment may use. 0 is the node's own bare callsign; an
    /// app is always an SSID of the node base, so the free walk starts at 1.</summary>
    private const int MinAutoSsid = 1;
    private const int MaxAutoSsid = 15;

    /// <summary>One resolved app identity: the app id, whether the callsign was pinned by the
    /// owner or auto-assigned, and the resolved callsign itself.</summary>
    public sealed record ResolvedAppCallsign(string Id, Callsign Callsign, bool Pinned);

    /// <summary>
    /// Resolve the callsign map for every enabled packet-capable app in <paramref name="config"/>
    /// (the union of inline <c>applications:</c> and the enabled, error-free discovered packages,
    /// supplied via <paramref name="packages"/>). Returns an id → resolution map; an app with no
    /// resolvable callsign (a pure session app with no pin) is simply absent. Total: an unparsable
    /// pin or an exhausted SSID space drops that app from the map rather than throwing (validation
    /// reports the bad pin; the resolver stays a pure best-effort projection the live path can
    /// always call).
    /// </summary>
    /// <param name="config">The live node config (the node identity + the inline app list +
    /// per-app pins).</param>
    /// <param name="packages">The discovered packages (the catalog's <see cref="IAppPackageCatalog.Discover"/>
    /// result). Only enabled, error-free, packet-capable entries participate. Pass an empty list
    /// when there is no catalog (inline-only, the pre-package shape).</param>
    public static IReadOnlyDictionary<string, ResolvedAppCallsign> Resolve(
        NodeConfig config, IReadOnlyList<DiscoveredAppPackage> packages)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(packages);

        var result = new Dictionary<string, ResolvedAppCallsign>(StringComparer.OrdinalIgnoreCase);

        // The node's own identity - its base anchors auto-assignment, its full callsign (with
        // SSID) is a taken slot an app must never collide with.
        if (!Callsign.TryParse(config.Identity.Callsign, out var node))
        {
            return result;   // no usable node identity → no resolvable app callsigns.
        }
        var nodeBase = node.Base;

        // Taken SSIDs on the node base: the node's own SSID is always taken.
        var taken = new HashSet<byte>();
        if (NodeBaseSsid(nodeBase, node) is { } nodeSsid)
        {
            taken.Add(nodeSsid);
        }

        // The candidate apps, in the deterministic walk order: inline first (config order),
        // then discovered packages by id. Each candidate carries its id + pin + whether it is
        // a packet app at all.
        var candidates = CollectCandidates(config, packages);

        // Pass 1 - reserve every pin that parses to a callsign on the node base (or a full
        // callsign whose base matches). A pin off the node base is honoured verbatim and does
        // not consume a node-base SSID slot.
        var pending = new List<Candidate>();
        foreach (var c in candidates)
        {
            if (TryResolvePin(c.Pin, nodeBase, out var pinned, out var onNodeBase))
            {
                result[c.Id] = new ResolvedAppCallsign(c.Id, pinned, Pinned: true);
                if (onNodeBase)
                {
                    taken.Add(pinned.Ssid);
                }
            }
            else if (!string.IsNullOrWhiteSpace(c.Pin))
            {
                // A pin was given but did not parse - skip the app (validation flags it); do not
                // auto-assign over an owner's explicit (if broken) intent.
                continue;
            }
            else
            {
                pending.Add(c);   // no pin → auto-assign in pass 2.
            }
        }

        // Pass 2 - auto-assign the lowest free SSID to each remaining packet app, deterministically.
        foreach (var c in pending)
        {
            byte? free = null;
            for (int ssid = MinAutoSsid; ssid <= MaxAutoSsid; ssid++)
            {
                if (!taken.Contains((byte)ssid))
                {
                    free = (byte)ssid;
                    break;
                }
            }
            if (free is null)
            {
                continue;   // SSID space exhausted - drop (no identity rather than a duplicate).
            }
            taken.Add(free.Value);
            result[c.Id] = new ResolvedAppCallsign(c.Id, new Callsign(nodeBase, free.Value), Pinned: false);
        }

        return result;
    }

    /// <summary>The node's own SSID as a slot to reserve when its base matches the app base -
    /// always, since apps are SSIDs of the node base.</summary>
    private static byte? NodeBaseSsid(string nodeBase, Callsign node) =>
        string.Equals(node.Base, nodeBase, StringComparison.Ordinal) ? node.Ssid : null;

    private sealed record Candidate(string Id, string? Pin);

    /// <summary>The packet apps to resolve, deterministically ordered (inline first, then
    /// packages by id). A pure session app with no pin and no packet capability is excluded -
    /// it binds no callsign.</summary>
    private static List<Candidate> CollectCandidates(NodeConfig config, IReadOnlyList<DiscoveredAppPackage> packages)
    {
        var list = new List<Candidate>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var app in config.Applications)
        {
            if (!app.Enabled || string.IsNullOrWhiteSpace(app.Id) || !seen.Add(app.Id))
            {
                continue;
            }
            if (NeedsCallsign(app.Capabilities, app.Callsign, app.Netrom))
            {
                list.Add(new Candidate(app.Id, app.Callsign));
            }
        }

        foreach (var pkg in packages.Where(p => p.Enabled && p.Error is null)
                     .OrderBy(p => p.Id, StringComparer.Ordinal))
        {
            if (string.IsNullOrWhiteSpace(pkg.Id) || !seen.Add(pkg.Id))
            {
                continue;
            }
            var caps = pkg.Manifest?.Capabilities;
            var pin = pkg.Override?.Callsign;
            var netrom = pkg.Override?.Netrom;
            // A packet-capable app (or one the owner pinned a callsign / set a netrom alias for)
            // binds a node-resolved callsign. A pure session app (e.g. WALL/LOBBY: it attaches
            // over the session, not a separate L2 identity) binds none - back-compat, and it
            // doesn't consume an SSID slot.
            if (NeedsCallsign(caps, pin, netrom))
            {
                list.Add(new Candidate(pkg.Id, pin));
            }
        }

        return list;
    }

    /// <summary>An app needs a node-assigned callsign when it grants packet access, or the owner
    /// pinned a callsign, or set a NET/ROM advert (an alias must point at a callsign).</summary>
    private static bool NeedsCallsign(IReadOnlyList<string>? capabilities, string? pin, AppNetromConfig? netrom) =>
        AppCapabilities.GrantsPacketAccess(capabilities)
        || !string.IsNullOrWhiteSpace(pin)
        || !string.IsNullOrWhiteSpace(netrom?.Alias);

    /// <summary>
    /// Resolve an owner pin to a callsign. Accepts a full callsign (<c>M9YYY-1</c>) or a bare
    /// <c>-N</c> SSID suffix appended to the node base (<c>-7</c> → <c>&lt;node-base&gt;-7</c>).
    /// <paramref name="onNodeBase"/> reports whether the result sits on the node base (so the
    /// caller reserves its SSID against auto-assignment); a full callsign on a different base is
    /// honoured but does not consume a node-base slot.
    /// </summary>
    internal static bool TryResolvePin(string? pin, string nodeBase, out Callsign callsign, out bool onNodeBase)
    {
        callsign = default;
        onNodeBase = false;
        if (string.IsNullOrWhiteSpace(pin))
        {
            return false;
        }
        var text = pin.Trim();

        // A bare "-N" SSID suffix → the node base at that SSID.
        if (text.StartsWith('-'))
        {
            if (byte.TryParse(text.AsSpan(1), out var ssid) && ssid <= 15 && nodeBase.Length > 0)
            {
                callsign = new Callsign(nodeBase, ssid);
                onNodeBase = true;
                return true;
            }
            return false;
        }

        if (!Callsign.TryParse(text, out callsign))
        {
            return false;
        }
        onNodeBase = string.Equals(callsign.Base, nodeBase, StringComparison.Ordinal);
        return true;
    }
}
