namespace Packet.Node.Core.Audit;

/// <summary>
/// One persisted audit record: a privileged action and who invoked it. Written to
/// the consolidated <c>pdn.db</c> by <see cref="IAuditLog"/>. The §6 audit promise
/// ("all write endpoints audit-logged — actor, source, action, target — into the DB").
/// </summary>
/// <param name="Id">Auto-assigned row id (0 on an unsaved entry).</param>
/// <param name="TimestampUtc">When the action was invoked (UTC, from the node's clock).</param>
/// <param name="Actor">Who: a username/token subject, or <c>local-stdio</c> for the local MCP user.</param>
/// <param name="Source">Where from: e.g. <c>mcp:stdio</c>, <c>mcp:sse</c>, <c>rest</c>, <c>console</c>.</param>
/// <param name="Action">What: the tool name or endpoint verb (e.g. <c>reset_port</c>, <c>PUT /config</c>).</param>
/// <param name="Target">The subject of the action (port id, session id, …); may be empty.</param>
/// <param name="Outcome">Result class: <c>ok</c> | <c>denied</c> | <c>error</c> | <c>requested</c>.</param>
/// <param name="Detail">A short human detail line (no secrets — payloads are summarised/hashed).</param>
/// <param name="ClientIp">Remote IP when known (SSE/REST); null for the local stdio user.</param>
public sealed record AuditEntry(
    long Id,
    DateTimeOffset TimestampUtc,
    string Actor,
    string Source,
    string Action,
    string Target,
    string Outcome,
    string Detail,
    string? ClientIp)
{
    /// <summary>Build an unsaved entry (Id 0) — the store assigns the id on insert.</summary>
    public static AuditEntry New(
        DateTimeOffset timestampUtc, string actor, string source, string action,
        string target, string outcome, string detail, string? clientIp = null)
        => new(0, timestampUtc, actor, source, action, target, outcome, detail, clientIp);
}
