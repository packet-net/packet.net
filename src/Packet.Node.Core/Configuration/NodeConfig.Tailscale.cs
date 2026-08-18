namespace Packet.Node.Core.Configuration;

/// <summary>
/// The embedded Tailscale node (<c>tsnet</c> Go sidecar) configuration - the blessed
/// remote + passkey path. <b>Default-OFF</b>: pdn serves plain HTTP (loopback + LAN)
/// and stays HTTP-only until the operator sets <see cref="Enabled"/>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Parsed + validated, INERT in S1.</b> This block carries the operator's intent;
/// the consuming <c>TailscaleSidecarHostedService</c> arrives in S2. When enabled there,
/// the sidecar joins the operator's tailnet, obtains a real Let's Encrypt cert for
/// <c>pdn.&lt;tailnet&gt;.ts.net</c> via <c>ListenTLS</c>, terminates TLS, and
/// reverse-proxies to <see cref="Target"/> (pdn's loopback HTTP). The browser then sees
/// trusted HTTPS → passkeys work remotely with no public DNS, port-forward, or cert
/// management. See <c>docs/network-access.md</c>.
/// </para>
/// <para>
/// <b>The auth key is sensitive</b> (first-join only). Prefer <see cref="AuthKeyFile"/>
/// (a 0600 file, packetnet-owned) over an inline <see cref="AuthKey"/>; supplying neither
/// leaves first join to interactive login (the sidecar prints a <c>login.tailscale.com</c>
/// URL). After first join the node identity lives in <see cref="StateDir"/>.
/// </para>
/// </remarks>
public sealed record TailscaleConfig
{
    /// <summary>Whether the embedded Tailscale sidecar runs. Default <c>false</c> - pdn
    /// stays HTTP-only until opted in.</summary>
    public bool Enabled { get; init; }

    /// <summary>A tailnet pre-auth key used on first join only. Null (the default) =
    /// none. Prefer <see cref="AuthKeyFile"/> for secrets; this and
    /// <see cref="AuthKeyFile"/> must not both be set (see
    /// <see cref="TailscaleConfigValidator"/>).</summary>
    public string? AuthKey { get; init; }

    /// <summary>Path to a 0600 file holding the tailnet pre-auth key (preferred over an
    /// inline <see cref="AuthKey"/> so the secret never lives in the config text). Null
    /// (the default) = none.</summary>
    public string? AuthKeyFile { get; init; }

    /// <summary>The desired node name → <c>&lt;hostname&gt;.&lt;tailnet&gt;.ts.net</c> (the
    /// actual name is read back from the sidecar). <b>Empty (the default) ⇒ derive
    /// <c>&lt;callsign&gt;-pdn</c></b> (the lowercased base callsign) so multiple nodes on
    /// one tailnet don't collide on a bare <c>pdn</c> - see
    /// <see cref="Tailscale.TailscaleHostname"/>. When set explicitly it must match
    /// <c>^[a-z0-9-]+$</c>.</summary>
    public string Hostname { get; init; } = "";

    /// <summary>Tailnet tags applied to the node (e.g. <c>tag:server</c> — a
    /// tailnet-owned node, right for an always-on box). Default empty.</summary>
    public IReadOnlyList<string> Tags { get; init; } = [];

    /// <summary>The PERSISTENT state directory the sidecar uses to rejoin as the same
    /// node (and keep the same cert) across restarts. Load-bearing for a stable hostname
    /// → stable passkeys. Default <c>/var/lib/packetnet/tsnet</c>.</summary>
    public string StateDir { get; init; } = "/var/lib/packetnet/tsnet";

    /// <summary>The loopback HTTP endpoint (<c>host:port</c>) the sidecar reverse-proxies
    /// to — pdn's own HTTP listener. Default <c>127.0.0.1:8080</c>.</summary>
    public string Target { get; init; } = "127.0.0.1:8080";

    /// <summary>Opt-in public exposure via Tailscale Funnel (vs tailnet-only). Default
    /// <c>false</c>; a <c>true</c> value with <see cref="Enabled"/> off is inert (a
    /// validation warning, not an error — see <see cref="TailscaleConfigValidator"/>).</summary>
    public bool Funnel { get; init; }

    // Records compare a collection member by REFERENCE, so two configs with equal-but-
    // distinct Tags lists would be unequal — breaking the YAML round-trip identity
    // (serialise→parse yields a fresh list). Compare the list by value so equality is
    // content-based, matching every other config record (see ConfigEquality).
    public bool Equals(TailscaleConfig? other) =>
        other is not null
        && Enabled == other.Enabled
        && AuthKey == other.AuthKey
        && AuthKeyFile == other.AuthKeyFile
        && Hostname == other.Hostname
        && StateDir == other.StateDir
        && Target == other.Target
        && Funnel == other.Funnel
        && ConfigEquality.ListEqual(Tags, other.Tags);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(Enabled);
        hash.Add(AuthKey);
        hash.Add(AuthKeyFile);
        hash.Add(Hostname);
        hash.Add(StateDir);
        hash.Add(Target);
        hash.Add(Funnel);
        hash.Add(ConfigEquality.ListHash(Tags));
        return hash.ToHashCode();
    }
}
