using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Packet.Node.Core.Audit;

namespace Packet.Node.Api;

/// <summary>
/// Records a REST write/action into the node-wide audit log (<c>pdn.db</c>) with the
/// caller identity derived from the request principal — the §6 promise that every
/// privileged write endpoint (config, ports, sessions, transmit, system) is
/// attributable: <b>who</b>, from <b>where</b>, <b>what</b>, the <b>target</b>, the
/// <b>outcome</b>. The network/transmit actions (connect, send, ping, port lifecycle)
/// are audited so RF activity an operator — or an MCP client driving these same
/// endpoints — generates stays transparent to the node owner.
/// </summary>
/// <remarks>
/// Source is always <c>rest</c> (mirrors <c>mcp:stdio</c>/<c>mcp:sse</c> on the MCP
/// side). Actor derivation matches <see cref="Mcp.HttpContextMcpCallerAccessor"/>:
/// the principal's name, else its <c>sub</c> claim, else <c>owner</c> when auth is off
/// (no principal — the local operator). Best-effort: <see cref="IAuditLog.Record"/>
/// never throws, so a wrapped call can't take an action path down. No secrets in
/// <paramref name="detail"/> — payloads are summarised (lengths, ids), never logged raw.
/// </remarks>
internal static class AuditHttpExtensions
{
    /// <summary>Record one REST action. <paramref name="outcome"/> is <c>ok</c> |
    /// <c>requested</c> | <c>denied</c> | <c>error</c> (see <see cref="AuditEntry"/>).</summary>
    public static void RecordRest(
        this IAuditLog audit, HttpContext ctx, TimeProvider clock,
        string action, string target, string outcome, string detail = "")
    {
        var user = ctx.User;
        string actor = user.Identity?.Name
            ?? user.FindFirstValue("sub")
            ?? "owner";

        audit.Record(AuditEntry.New(
            clock.GetUtcNow(), actor, "rest", action, target, outcome, detail,
            ctx.Connection.RemoteIpAddress?.ToString()));
    }
}
