using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Packet.Node.Core.Audit;
using Packet.Node.Core.Hosting;

namespace Packet.Node.Api;

/// <summary>
/// The action side of the per-peer AX.25 capability cache control surface: forget one
/// learned (port, peer) record (<c>DELETE /api/v1/capabilities/{id}</c>, id = <c>port:peer</c>).
/// The READ side (<c>GET /api/v1/capabilities</c>) lives in <see cref="PdnReadApi"/>'s read
/// group; this is the operate-gated mutation, modelled verbatim on
/// <c>PdnSessionsApi</c>'s <c>DELETE /sessions/{id}</c> (split the id, audit the request,
/// forget through the host's <see cref="NodeHostedService.Capabilities"/> handle, 204/404).
/// </summary>
/// <remarks>
/// Forgetting a cached capability is a deliberate operator action - it makes the next dial to
/// that neighbour re-probe from the optimistic defaults - so, like the session/port/transmit
/// actions, it is <c>operate</c>-gated and audited (<c>clear_capability</c>). The id is split
/// on the FIRST ':' the same way a session id is (a callsign peer carries no ':'), reusing
/// <see cref="PdnSessionsApi.TrySplitSessionId"/>. A malformed id, an absent cache (default-off
/// host), or an unknown (port, peer) all return 404; a forgotten record returns 204.
/// </remarks>
public static class PdnCapabilitiesApi
{
    /// <summary>
    /// Map the capability-cache action endpoint under <c>/api/v1</c>. Called from the node
    /// composition root beside the other action APIs and before the SPA fallback (the specific
    /// route wins over the <c>/api/{**rest}</c> catch-all regardless of order).
    /// </summary>
    public static void MapPdnCapabilitiesApi(this WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);

        // Forgetting a learned capability is a write/action → `operate`. The gate is a no-op
        // when management.auth.enabled is off (ScopeRequirementHandler passes through).
        var v1 = app.MapGroup("/api/v1").RequireAuthorization(PdnAuthPolicies.Operate);

        // Forget one cached (port, peer) capability by {id} (port:peer). Split the id, audit
        // the request, and forget through the host's cache handle. Absent cache / malformed id /
        // unknown record → 404, else 204. (Modelled on DELETE /sessions/{id}.)
        v1.MapDelete("/capabilities/{id}", (string id, HttpContext ctx, NodeHostedService host, IAuditLog audit, TimeProvider clock) =>
        {
            // Forgetting a capability makes the next dial re-probe this neighbour - audit it.
            audit.RecordRest(ctx, clock, "clear_capability", id, "requested", "");

            if (host.Capabilities is null || !PdnSessionsApi.TrySplitSessionId(id, out var portId, out var peer))
            {
                return Results.NotFound();
            }

            return host.Capabilities.Forget(portId, peer) ? Results.NoContent() : Results.NotFound();
        });
    }
}
