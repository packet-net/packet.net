namespace Packet.Node.Core.Console;

/// <summary>
/// Expands the operator-facing <c>{node}</c> / <c>{call}</c> placeholders that the
/// services banner / prompt - and the ID beacon - share. <b>The single source of
/// truth</b> for that substitution: <see cref="NodeCommandService"/> (banner / prompt)
/// and <c>BeaconService</c> (beacon text) both call this so the two surfaces can never
/// drift. <c>{call}</c> is the station callsign; <c>{node}</c> is the alias if set,
/// else the callsign (the "node name").
/// </summary>
public static class NodeTextTemplate
{
    /// <summary>
    /// Expand <c>{node}</c> → <paramref name="nodeName"/> and <c>{call}</c> →
    /// <paramref name="callsign"/> in <paramref name="template"/>. Ordinal, allocation-
    /// frugal (no-op when the template has no placeholders).
    /// </summary>
    public static string Expand(string template, string nodeName, string callsign)
    {
        ArgumentNullException.ThrowIfNull(template);
        return template
            .Replace("{node}", nodeName, StringComparison.Ordinal)
            .Replace("{call}", callsign, StringComparison.Ordinal);
    }

    /// <summary>The node's display name - the alias if set, else the callsign.
    /// Matches <c>NodeConsoleEnvironment.NodeName</c>.</summary>
    public static string NodeName(string callsign, string? alias)
        => string.IsNullOrWhiteSpace(alias) ? callsign : alias;
}
