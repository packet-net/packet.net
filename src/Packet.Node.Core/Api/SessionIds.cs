namespace Packet.Node.Core.Api;

/// <summary>
/// The node's <b>session identity</b>: the one place the <c>/sessions</c> id is minted and parsed
/// (packet-net/packet.net#723 item 5). Two forms, both addressing a live circuit:
/// <list type="bullet">
/// <item><c>{portId}:{remote}</c> - a session to the node's own callsign (the node console). The
/// long-standing form; unchanged, so every existing id keeps working.</item>
/// <item><c>{portId}:{remote}&gt;{local}</c> - a session to some OTHER local callsign on that
/// port: an application callsign the node answers for, or an alias. The <c>&gt;</c> reads the way
/// a packet operator writes a direction.</item>
/// </list>
/// </summary>
/// <remarks>
/// <para>
/// <b>Why the local half is in the id.</b> The AX.25 engine deliberately keys a session on
/// <c>(Local, Remote)</c> per listener, precisely because one station can hold a link to the node
/// console and another to a bound application callsign at the same time. The API id used to carry
/// the remote only, so those two circuits produced two <c>/sessions</c> rows with the SAME id and
/// <c>DELETE /sessions/{id}</c> (or <c>/send</c>) hit whichever enumerated first: you could drop
/// the BBS link when you meant the console. Carrying the full key makes the two independently
/// addressable.
/// </para>
/// <para>
/// <b>The id stays opaque to clients.</b> The SPA, the MCP backend and scripts pass it back
/// verbatim; nothing outside this class parses it. A callsign contains neither <c>':'</c> nor
/// <c>'&gt;'</c>, so the split is unambiguous in both directions.
/// </para>
/// </remarks>
public static class SessionIds
{
    /// <summary>The separator between the port and the remote callsign.</summary>
    public const char PortSeparator = ':';

    /// <summary>The separator introducing the LOCAL callsign, present only when it is not the
    /// node's own.</summary>
    public const char LocalSeparator = '>';

    /// <summary>
    /// Mint the id for a session on <paramref name="portId"/> with <paramref name="remote"/>,
    /// answered locally as <paramref name="local"/>. Emits the short form when
    /// <paramref name="local"/> is the port's own callsign (<paramref name="nodeCall"/>) - or
    /// when the caller has no node callsign to compare against, which is the honest fallback for
    /// a projection that is not AX.25 at all (a NET/ROM circuit).
    /// </summary>
    public static string Format(string portId, string remote, string? local, string? nodeCall)
    {
        ArgumentNullException.ThrowIfNull(portId);
        ArgumentNullException.ThrowIfNull(remote);
        bool isNodeCall = string.IsNullOrEmpty(local)
            || string.IsNullOrEmpty(nodeCall)
            || string.Equals(local, nodeCall, StringComparison.OrdinalIgnoreCase);
        return isNodeCall
            ? $"{portId}{PortSeparator}{remote}"
            : $"{portId}{PortSeparator}{remote}{LocalSeparator}{local}";
    }

    /// <summary>
    /// Parse an id back into its parts. <paramref name="local"/> is null for the short form,
    /// which means "the session to this port's own callsign" - a resolver must match the
    /// listener's <c>MyCall</c>, not "any local". Returns false for anything malformed (no
    /// <c>':'</c>, an empty part), which callers turn into a 404 / "bad session id".
    /// </summary>
    public static bool TryParse(string id, out string portId, out string remote, out string? local)
    {
        portId = string.Empty;
        remote = string.Empty;
        local = null;
        if (string.IsNullOrEmpty(id))
        {
            return false;
        }

        int colon = id.IndexOf(PortSeparator, StringComparison.Ordinal);
        if (colon <= 0 || colon >= id.Length - 1)
        {
            return false;
        }
        portId = id[..colon];
        var rest = id[(colon + 1)..];

        int arrow = rest.IndexOf(LocalSeparator, StringComparison.Ordinal);
        if (arrow < 0)
        {
            remote = rest;
            return true;
        }
        if (arrow == 0 || arrow >= rest.Length - 1)
        {
            return false;   // ">LOCAL" or "REMOTE>" is not an id we ever mint.
        }
        remote = rest[..arrow];
        local = rest[(arrow + 1)..];
        return true;
    }
}
