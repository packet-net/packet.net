using System.Security.Cryptography;
using System.Text;

namespace Packet.Node.Core.Configuration;

/// <summary>
/// The config document's version token: a content fingerprint of a <see cref="NodeConfig"/>,
/// used as the <c>ETag</c> on <c>GET /api/v1/config</c> and as the <c>If-Match</c> a writer
/// sends back for compare-and-swap (review item C065, #694).
/// </summary>
/// <remarks>
/// <para>
/// A hash, not a counter, on purpose: the store keeps one singleton row with no revision
/// column, and a hash needs no schema change, survives a restart, is identical on two nodes
/// running identical config, and (unlike a counter) makes a write that changes nothing a no-op
/// for concurrency purposes. The canonical JSON serialisation is the one the store persists,
/// so the token is exactly "this document".
/// </para>
/// <para>
/// 128 bits of SHA-256, lower-case hex. Not a security primitive: it guards against a lost
/// update, not against an attacker forging a version.
/// </para>
/// </remarks>
public static class ConfigVersion
{
    /// <summary>The version token of <paramref name="config"/>.</summary>
    public static string Of(NodeConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(NodeConfigJson.Serialize(config)));
        return Convert.ToHexStringLower(bytes.AsSpan(0, 16));
    }

    /// <summary>
    /// True when an <c>If-Match</c> header value matches <paramref name="version"/>. Accepts the
    /// bare token, a quoted entity tag (<c>"abc"</c>), a weak one (<c>W/"abc"</c>), a
    /// comma-separated list of any of those, and the wildcard <c>*</c> (which matches any
    /// existing document, per RFC 9110).
    /// </summary>
    public static bool Matches(string? ifMatch, string version)
    {
        if (string.IsNullOrWhiteSpace(ifMatch))
        {
            return false;
        }
        foreach (var raw in ifMatch.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var tag = raw;
            if (tag == "*")
            {
                return true;
            }
            if (tag.StartsWith("W/", StringComparison.Ordinal))
            {
                tag = tag[2..];
            }
            tag = tag.Trim('"');
            if (string.Equals(tag, version, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>Render a version as an HTTP entity tag (a quoted string).</summary>
    public static string ToETag(string version) => $"\"{version}\"";
}
