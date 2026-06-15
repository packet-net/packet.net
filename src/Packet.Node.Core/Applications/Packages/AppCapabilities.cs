namespace Packet.Node.Core.Applications.Packages;

/// <summary>
/// Capability-string display normalisation for the app platform's trust prompt.
/// </summary>
/// <remarks>
/// <para>
/// Capabilities are free-form strings an app declares in its <c>pdn-app.yaml</c> manifest
/// (<see cref="AppPackageManifest.Capabilities"/>) or a catalog entry — there is no closed
/// enum. They surface to the node owner only as the install/enable <i>trust prompt</i>
/// ("this app will run with: …"). They are NOT enforced in v1.
/// </para>
/// <para>
/// <b>The <c>network</c> → <c>packet</c> rename.</b> "network" reads as TCP/IP / LAN /
/// internet, but the capability it names is <i>full packet-radio network access</i> — the app
/// binds a callsign and sends/receives over AX.25 / NET-ROM. "packet" is transport-accurate
/// (RF / KISS-TCP / AXUDP / sim) and unambiguous. We display-normalise <c>network</c> →
/// <c>packet</c> wherever a capability list is projected to the API, so an app whose manifest
/// still declares the old spelling shows the new one. <c>network</c> stays a back-compat alias
/// — accepted on input forever; only the surfaced label changes. (The catalog ships
/// <c>packet</c> directly; this alias covers third-party app manifests in their own repos that
/// have not yet renamed.)
/// </para>
/// </remarks>
public static class AppCapabilities
{
    /// <summary>The legacy capability spelling, kept as a back-compat input alias.</summary>
    public const string LegacyNetwork = "network";

    /// <summary>The transport-accurate replacement for <see cref="LegacyNetwork"/>.</summary>
    public const string Packet = "packet";

    /// <summary>Map one capability string to its display spelling: <c>network</c> →
    /// <c>packet</c> (case-insensitive), everything else unchanged.</summary>
    public static string Normalize(string capability) =>
        string.Equals(capability, LegacyNetwork, StringComparison.OrdinalIgnoreCase)
            ? Packet
            : capability;

    /// <summary>Project a declared capability list to its display spellings, preserving order
    /// and any duplicates (the trust prompt shows exactly what the manifest declared, only with
    /// <c>network</c> re-labelled <c>packet</c>).</summary>
    public static IReadOnlyList<string> NormalizeAll(IReadOnlyList<string>? capabilities) =>
        capabilities is null or { Count: 0 }
            ? []
            : [.. capabilities.Select(Normalize)];

    /// <summary>Whether a declared capability list grants packet-radio network access, accepting
    /// both the new <c>packet</c> spelling and the legacy <c>network</c> alias. (No C# code
    /// semantically gates on this capability in v1 — capabilities are display-only — but any
    /// future check should use this so both spellings match.)</summary>
    public static bool GrantsPacketAccess(IReadOnlyList<string>? capabilities) =>
        capabilities is not null && capabilities.Any(c =>
            string.Equals(c, Packet, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(c, LegacyNetwork, StringComparison.OrdinalIgnoreCase));
}
