using System.Text;

namespace Packet.Node.Core.Tailscale;

/// <summary>
/// Resolves the tailnet node hostname for the embedded sidecar. An explicit
/// <c>tailscale.hostname</c> wins; otherwise it is derived as <c>&lt;callsign&gt;-pdn</c>
/// (the lowercased base callsign) so that multiple pdn nodes on one tailnet don't all
/// land on a bare <c>pdn</c> - Tailscale would suffix <c>-1</c>/<c>-2</c>, making the
/// FQDN (and therefore the passkey RP ID) unpredictable. Falls back to <c>pdn</c> when
/// no usable callsign is configured.
/// </summary>
public static class TailscaleHostname
{
    /// <summary>The effective tailnet node name: <paramref name="configured"/> when set,
    /// else <c>&lt;callsign&gt;-pdn</c>, else <c>pdn</c>.</summary>
    public static string Resolve(string? configured, string? callsign)
    {
        if (!string.IsNullOrWhiteSpace(configured))
        {
            return configured.Trim();
        }

        var baseCall = SanitizeCallsign(callsign);
        return baseCall.Length == 0 ? "pdn" : $"{baseCall}-pdn";
    }

    /// <summary>The base callsign as a legal hostname label: lowercased, SSID dropped
    /// (everything from the first <c>-</c>), and reduced to <c>[a-z0-9]</c>. E.g.
    /// <c>GB7RDG</c> → <c>gb7rdg</c>, <c>M0LTE-7</c> → <c>m0lte</c>. Empty when there's
    /// nothing usable (caller falls back to <c>pdn</c>).</summary>
    private static string SanitizeCallsign(string? callsign)
    {
        if (string.IsNullOrWhiteSpace(callsign))
        {
            return string.Empty;
        }

        var s = callsign.Trim();
        var dash = s.IndexOf('-');
        if (dash >= 0)
        {
            s = s[..dash];
        }

        var sb = new StringBuilder(s.Length);
        foreach (var c in s.ToLowerInvariant())
        {
            if (c is (>= 'a' and <= 'z') or (>= '0' and <= '9'))
            {
                sb.Append(c);
            }
        }
        return sb.ToString();
    }
}
