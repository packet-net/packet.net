using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Packet.Node.Core.Audit;
using Packet.Node.Core.Hail;

namespace Packet.Node.Api;

/// <summary>Request body for <c>POST /api/v1/ports/{id}/hail</c>.</summary>
/// <param name="PeerSdmId">The peer radio's 8-character SDM data identity to hail.</param>
public sealed record HailRequest(string? PeerSdmId);

/// <summary>
/// The <b>station-hail</b> surface of the pdn node API: <c>POST /api/v1/ports/{id}/hail</c> sends a
/// hail to a peer over the port's SDM side channel and returns the peer's
/// <see cref="Packet.Node.Core.Api.PortHailStatus"/> — its callsign, current NinoTNC mode/bitrate,
/// channel and capabilities. Because the side channel rides the radio's own FFSK modem (independent
/// of the packet modulation), the hail succeeds — and reveals the peer's mode — even when the two
/// stations cannot reach each other on the packet path because of a mode mismatch.
/// <para>
/// The hail <b>transmits</b> (an SDM to the peer), so the endpoint is <b>admin</b>-scoped and
/// <b>audited</b>, mirroring the tuning / doctor-interrupt endpoints. Errors map to
/// 404 (unknown/not-running port), 400 (no radio / bad peer id / SDM disabled), 504 (the peer never
/// answered), 502 (the side channel could not carry the hail).
/// </para>
/// </summary>
public static class PdnPortHailApi
{
    /// <summary>Map the hail endpoint under <c>/api/v1</c> (before the SPA fallback).</summary>
    public static void MapPdnPortHailApi(this WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);

        var admin = app.MapGroup("/api/v1").RequireAuthorization(PdnAuthPolicies.Admin);
        admin.MapPost("/ports/{id}/hail", async (
            string id,
            HailRequest? body,
            HttpContext ctx,
            PortHailService hail,
            IAuditLog audit,
            TimeProvider clock,
            CancellationToken ct) =>
        {
            string peer = body?.PeerSdmId ?? string.Empty;
            audit.RecordRest(ctx, clock, "port_hail", id, "requested", $"peer={peer}");
            try
            {
                var status = await hail.HailAsync(id, peer, ct).ConfigureAwait(false);
                return Results.Ok(status);
            }
            catch (HailException ex)
            {
                return MapError(ex);
            }
        });
    }

    private static IResult MapError(HailException ex) => ex.Error switch
    {
        HailError.NotFound => Results.NotFound(new { error = ex.Message }),
        HailError.Timeout => Results.Json(new { error = ex.Message, outcome = "timeout" }, statusCode: StatusCodes.Status504GatewayTimeout),
        HailError.LinkFailed => Results.Json(new { error = ex.Message, outcome = "link-failed" }, statusCode: StatusCodes.Status502BadGateway),
        _ => Results.BadRequest(new { error = ex.Message }),
    };
}
