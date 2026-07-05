namespace Packet.Node.Core.HeadEnd;

/// <summary>
/// Parses the manual <c>host:port</c> address of a head-end's HTTP control plane (the
/// <see cref="Configuration.HeadEndConfig.Address"/> operators type in Stage 3a — mDNS
/// resolution of an instance id → current address lands in Stage 3b). Shared by the config
/// validator (reject a malformed address at apply time) and <see cref="HeadEndClient"/> (build
/// the base <see cref="Uri"/> the inventory / line verbs hang off).
/// </summary>
/// <remarks>
/// An explicit port is <b>required</b>: the head-end HTTP API is not on :80 (its default is 7300),
/// so a bare host would silently dial the wrong endpoint. A bare host is therefore rejected rather
/// than defaulted. An optional <c>http://</c> / <c>https://</c> scheme is tolerated on input but the
/// base URI is always rebuilt as <c>http://host:port/</c> (the API is plain HTTP at the root).
/// </remarks>
public static class HeadEndAddress
{
    /// <summary>Try to parse <paramref name="address"/> into its <paramref name="host"/> and
    /// <paramref name="port"/>. Returns false for null/blank, an unparseable authority, a missing
    /// explicit port, or an out-of-range port.</summary>
    public static bool TryParse(string? address, out string host, out int port)
    {
        host = "";
        port = 0;
        if (string.IsNullOrWhiteSpace(address))
        {
            return false;
        }

        var text = address.Contains("://", StringComparison.Ordinal) ? address : $"http://{address}";
        if (!Uri.TryCreate(text, UriKind.Absolute, out var uri) || string.IsNullOrEmpty(uri.Host))
        {
            return false;
        }

        // Require an explicit port — the head-end API port (e.g. 7300) is never the scheme default.
        if (!HasExplicitPort(uri))
        {
            return false;
        }

        if (uri.Port is <= 0 or > 65535)
        {
            return false;
        }

        host = uri.Host;
        port = uri.Port;
        return true;
    }

    /// <summary>Build the base <see cref="Uri"/> (<c>http://host:port/</c>) the head-end verbs
    /// resolve against. Throws <see cref="FormatException"/> on a malformed address (validation
    /// runs first in production, so this is defensive).</summary>
    public static Uri ToBaseUri(string address)
    {
        if (!TryParse(address, out var host, out var port))
        {
            throw new FormatException(
                $"'{address}' is not a valid head-end address — expected host:port (e.g. 192.168.1.10:7300).");
        }
        return new Uri($"http://{host}:{port}/", UriKind.Absolute);
    }

    private static bool HasExplicitPort(Uri uri)
    {
        // authority is host[:port]; for an IPv6 literal the host is bracketed ([::1]) and any
        // port colon comes after the closing bracket, so compare the last ':' against the ']'.
        var authority = uri.Authority;
        var lastColon = authority.LastIndexOf(':');
        var closeBracket = authority.LastIndexOf(']');
        return lastColon > closeBracket;
    }
}
