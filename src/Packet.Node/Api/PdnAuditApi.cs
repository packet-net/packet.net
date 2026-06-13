using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Packet.Node.Core.Audit;

namespace Packet.Node.Api;

/// <summary>
/// The audit-log read API: <c>GET /api/v1/audit</c>, returning recent privileged-action
/// records (newest first) from the node-wide <see cref="IAuditLog"/> (pdn.db). Gated
/// <c>admin</c> — who-did-what is sensitive — and pass-through when
/// <c>management.auth.enabled</c> is off (the same contract as the rest of the API).
/// The write side records entries inline (MCP write tools today; REST writes adopt the
/// same seam). See docs/mcp-design.md / §6.
/// </summary>
public static class PdnAuditApi
{
    /// <summary>Map the audit read endpoint under <c>/api/v1</c>, admin-gated.</summary>
    public static void MapPdnAuditApi(this WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);

        var v1 = app.MapGroup("/api/v1").RequireAuthorization(PdnAuthPolicies.Admin);

        // Recent audit entries, newest first. limit defaults to 200, capped at 2000.
        v1.MapGet("/audit", ([FromServices] IAuditLog audit, int? limit)
            => Results.Ok(audit.Recent(Math.Clamp(limit ?? 200, 1, 2000))));
    }
}
