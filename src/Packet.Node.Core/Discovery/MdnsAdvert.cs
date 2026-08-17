using System.Globalization;
using System.Net;
using Packet.Node.Core.Configuration;

namespace Packet.Node.Core.Discovery;

/// <summary>
/// The mDNS / DNS-SD advertisement <em>plan</em> for the node — what would be published
/// for <c>_pdn._tcp</c>. Pure (no sockets, no process): <see cref="MdnsAdvert.Plan"/>
/// maps a <see cref="NodeConfig"/> to either an <see cref="AdvertPlan"/> or null (with a
/// reason) so the decision logic — enabled, the loopback-bind guard, the callsign-as-
/// identity TXT, the instance-name fallback — is unit-testable on its own. The hosted
/// service consumes the plan and supervises the actual <c>avahi-publish</c> child.
/// </summary>
public sealed record AdvertPlan(string Instance, int Port, IReadOnlyList<string> Txt)
{
    /// <summary>The DNS-SD service type advertised.</summary>
    public const string ServiceType = "_pdn._tcp";

    /// <summary>The <c>avahi-publish</c> argument vector (without the binary itself):
    /// <c>-f -s -- &lt;instance&gt; _pdn._tcp &lt;port&gt; &lt;txt&gt;…</c>.</summary>
    public IReadOnlyList<string> ToAvahiArgs()
    {
        // -f (--no-fail): block + auto-attach if avahi-daemon isn't up yet (it commonly starts
        //   concurrently with the node) or restarts, instead of exiting — the behaviour a long-
        //   lived supervised publisher wants.
        // -- : end option parsing, so a configured instance name beginning with '-' can never be
        //   misread as a flag (belt to the validator's braces).
        var args = new List<string>(6 + Txt.Count)
        {
            "-f",
            "-s",
            "--",
            Instance,
            ServiceType,
            Port.ToString(CultureInfo.InvariantCulture),
        };
        args.AddRange(Txt);
        return args;
    }
}

public static class MdnsAdvert
{
    /// <summary>
    /// Decide what (if anything) to advertise. Returns null with <paramref name="skipReason"/>
    /// set when the node should NOT advertise (disabled, a loopback web bind that would publish
    /// an unreachable endpoint, or an out-of-range port).
    /// </summary>
    public static AdvertPlan? Plan(NodeConfig config, string? version, out string? skipReason)
    {
        skipReason = null;
        var mdns = config.Management.Mdns;
        if (!mdns.Enabled)
        {
            skipReason = "management.mdns.enabled is false";
            return null;
        }

        var bind = config.Management.Http.Bind;
        if (IsLoopback(bind))
        {
            skipReason = $"management.http.bind '{bind}' is loopback - bind a LAN address for discovery";
            return null;
        }

        var port = config.Management.Http.Port;
        if (port is < 1 or > 65535)
        {
            skipReason = $"management.http.port {port} is out of range";
            return null;
        }

        var callsign = config.Identity.Callsign;
        var instance = FirstNonBlank(mdns.InstanceName, callsign);

        var txt = new List<string> { $"cs={callsign}" };
        if (!string.IsNullOrWhiteSpace(config.Identity.Alias))
        {
            txt.Add($"name={config.Identity.Alias}");
        }
        if (!string.IsNullOrWhiteSpace(version))
        {
            txt.Add($"v={version}");
        }

        return new AdvertPlan(instance, port, txt);
    }

    /// <summary>A loopback bind means the advertised host address won't accept the connection —
    /// treat it as "don't advertise". <c>0.0.0.0</c> / <c>::</c> (all interfaces) and any specific
    /// LAN address are fine. An unparseable host mirrors Kestrel's bind fallback (loopback), so it
    /// reads as dormant too.</summary>
    public static bool IsLoopback(string? bind)
    {
        if (string.IsNullOrWhiteSpace(bind))
        {
            return false;
        }

        var b = bind.Trim();
        if (string.Equals(b, "localhost", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return IPAddress.TryParse(b, out var ip) ? IPAddress.IsLoopback(ip) : true;
    }

    private static string FirstNonBlank(string? a, string b) =>
        string.IsNullOrWhiteSpace(a) ? b : a.Trim();
}
