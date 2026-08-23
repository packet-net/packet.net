using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Packet.Node.Core.Audit;
using Packet.Node.Core.Radios;

namespace Packet.Node.Api;

/// <summary>
/// The test-transmission surface of the pdn node API: <c>POST /api/v1/ports/{id}/radio/test-tx</c>
/// keys the port's attached Tait for about a second with no modulation and reports what its forward
/// and reverse power detectors read, plus an estimated VSWR and a verdict.
/// <list type="bullet">
///   <item>404 - unknown port, or the port is not running.</item>
///   <item>400 - the port has no Tait CCDI radio.</item>
///   <item>409 - a tuning session or a programming run holds the port.</item>
///   <item>502 - the radio was reached but faulted part-way; the transmitter was still unkeyed.</item>
/// </list>
/// <b>Admin</b>-scoped and <b>audited</b>, like every other endpoint here that puts RF on the air.
/// The response carries <see cref="TaitTestTransmitService.Caveat"/> saying so in words.
/// </summary>
public static class PdnRadioTestTxApi
{
    /// <summary>The POST body: how long to hold the key. Everything else is read off the radio.</summary>
    /// <param name="Milliseconds">Key length, clamped to
    /// <see cref="TaitTestTransmitService.MinimumMilliseconds"/>..<see cref="TaitTestTransmitService.MaximumMilliseconds"/>.
    /// Omitted means <see cref="TaitTestTransmitService.DefaultMilliseconds"/>.</param>
    public sealed record TestTxRequest(int? Milliseconds);

    /// <summary>Map the test-transmission endpoint under <c>/api/v1</c>. Mapped before the SPA
    /// fallback so the specific route wins over the <c>/api/{**rest}</c> catch-all.</summary>
    public static void MapPdnRadioTestTxApi(this WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);

        var admin = app.MapGroup("/api/v1").RequireAuthorization(PdnAuthPolicies.Admin);
        admin.MapPost("/ports/{id}/radio/test-tx", TestAsync);
    }

    private static async Task<IResult> TestAsync(
        string id,
        TestTxRequest? body,
        HttpContext ctx,
        TaitTestTransmitService testTx,
        IAuditLog audit,
        TimeProvider clock,
        CancellationToken ct)
    {
        audit.RecordRest(
            ctx, clock, "radio_test_tx", id, "requested",
            $"ms={body?.Milliseconds} - keys the radio on air with no modulation");

        try
        {
            var result = await testTx.RunAsync(id, body?.Milliseconds, ct).ConfigureAwait(false);
            return Results.Ok(new { result, caveat = TaitTestTransmitService.Caveat });
        }
        catch (TaitTestTxException ex)
        {
            return ex.Error switch
            {
                TaitTestTxError.NotFound => Results.NotFound(new { error = ex.Message }),
                TaitTestTxError.Conflict => Results.Conflict(new { error = ex.Message }),
                TaitTestTxError.RadioFault => Results.Json(new { error = ex.Message }, statusCode: StatusCodes.Status502BadGateway),
                _ => Results.BadRequest(new { error = ex.Message }),
            };
        }
    }
}
