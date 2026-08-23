using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Packet.Node.Core.Audit;
using Packet.Node.Core.Radios.Programming;

namespace Packet.Node.Api;

/// <summary>
/// The Tait codeplug-programming surface of the pdn node API (packet-net/packet.net#779): the port
/// editor's "Program radio" panel, which writes one channel - frequency, bandwidth, power - into an
/// attached Tait TM8100/TM8200 and optionally applies a PDN upgrade profile.
/// <list type="bullet">
///   <item><c>POST /api/v1/ports/{id}/radio/program</c> - start a run (404 unknown port · 400 no
///     Tait radio / head-end-bound / bad settings · 409 the port is already busy). Returns the run
///     plus the caveat describing what it costs.</item>
///   <item><c>POST /api/v1/ports/{id}/radio/program/read</c> - start a <b>read-only</b> run: the
///     same port-down and power-cycle, no write. The settings read off the radio land in the run's
///     <c>current</c>. Same refusals as a write.</item>
///   <item><c>GET /api/v1/ports/{id}/radio/program</c> - the run on this port, live or last
///     finished (404 when there has been none).</item>
///   <item><c>GET /api/v1/ports/{id}/radio/program/events</c> - SSE feed of the run: state changes,
///     progress and the power-cycle prompt.</item>
///   <item><c>POST /api/v1/ports/{id}/radio/program/cancel</c> - abandon a live run.</item>
/// </list>
/// The mutating verbs are <b>admin</b>-scoped and <b>audited</b>: a run rewrites the radio's
/// codeplug and takes the port off the air for minutes, which is at least as consequential as the
/// transmitting endpoints that already sit at that bar. The status + event feeds are
/// <b>read</b>-scoped, pure observation. The port is <b>always</b> restored when a run ends,
/// whatever the outcome.
/// </summary>
public static class PdnRadioProgrammingApi
{
    /// <summary>Map the programming endpoints under <c>/api/v1</c>. Mapped before the SPA fallback
    /// so the specific routes win over the <c>/api/{**rest}</c> catch-all.</summary>
    public static void MapPdnRadioProgrammingApi(this WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);

        var read = app.MapGroup("/api/v1").RequireAuthorization(PdnAuthPolicies.Read);
        read.MapGet("/ports/{id}/radio/program", (string id, TaitProgrammingService programming)
            => programming.Get(id) is { } info
                ? Results.Ok(info)
                : Results.NotFound(new { error = $"no codeplug programming run on port '{id}'" }));

        // The marker is what lets the browser's EventSource present its JWT as ?access_token=
        // (no header API); without it this feed 401s on an auth-on node.
        read.MapGet("/ports/{id}/radio/program/events", ProgramEventsAsync)
            .WithMetadata(AcceptsQueryAccessToken.Instance);

        var admin = app.MapGroup("/api/v1").RequireAuthorization(PdnAuthPolicies.Admin);
        admin.MapPost("/ports/{id}/radio/program", StartAsync);
        admin.MapPost("/ports/{id}/radio/program/read", StartReadAsync);
        admin.MapPost("/ports/{id}/radio/program/cancel", CancelAsync);
    }

    private static async Task<IResult> StartAsync(
        string id,
        TaitProgramRequest? body,
        HttpContext ctx,
        TaitProgrammingService programming,
        IAuditLog audit,
        TimeProvider clock,
        CancellationToken ct)
    {
        audit.RecordRest(
            ctx, clock, "radio_program", id, "requested",
            $"rxHz={body?.RxFrequencyHz} txHz={body?.TxFrequencyHz} bandwidth={body?.Bandwidth} " +
            $"power={body?.Power} profile={body?.Profile} replaceChannelTable={body?.ReplaceChannelTable} " +
            "- rewrites the radio's codeplug and stops the port");

        try
        {
            var info = await programming.StartAsync(id, body, ct).ConfigureAwait(false);
            return Results.Ok(new { run = info, caveat = TaitProgramCaveat.Text });
        }
        catch (TaitProgramStartException ex)
        {
            return ex.Error switch
            {
                TaitProgramStartError.NotFound => Results.NotFound(new { error = ex.Message }),
                TaitProgramStartError.Conflict => Results.Conflict(new { error = ex.Message }),
                _ => Results.BadRequest(new { error = ex.Message }),
            };
        }
    }

    private static async Task<IResult> StartReadAsync(
        string id,
        HttpContext ctx,
        TaitProgrammingService programming,
        IAuditLog audit,
        TimeProvider clock,
        CancellationToken ct)
    {
        audit.RecordRest(
            ctx, clock, "radio_program_read", id, "requested",
            "reads the radio's codeplug and stops the port; writes nothing");

        try
        {
            var info = await programming.StartReadAsync(id, ct).ConfigureAwait(false);
            return Results.Ok(new { run = info, caveat = TaitProgramCaveat.ReadText });
        }
        catch (TaitProgramStartException ex)
        {
            return ex.Error switch
            {
                TaitProgramStartError.NotFound => Results.NotFound(new { error = ex.Message }),
                TaitProgramStartError.Conflict => Results.Conflict(new { error = ex.Message }),
                _ => Results.BadRequest(new { error = ex.Message }),
            };
        }
    }

    private static async Task<IResult> CancelAsync(
        string id, HttpContext ctx, TaitProgrammingService programming, IAuditLog audit, TimeProvider clock)
    {
        audit.RecordRest(ctx, clock, "radio_program_cancel", id, "requested");
        bool cancelled = await programming.CancelAsync(id).ConfigureAwait(false);
        return cancelled
            ? Results.Ok(new { cancelled = true })
            : Results.NotFound(new { error = $"no live codeplug programming run on port '{id}'" });
    }

    private static async Task ProgramEventsAsync(
        string id, HttpContext ctx, TaitProgrammingService programming, TimeProvider clock)
    {
        if (programming.Subscribe(id, out var reader) is not { } subscription)
        {
            ctx.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        using (subscription)
        {
            var ct = ctx.RequestAborted;
            SseWriter.Begin(ctx);

            // Flush headers so the client's onopen fires promptly.
            await SseWriter.CommentAsync(ctx, "connected", ct);

            await SseWriter.PumpAsync(ctx, reader, clock, "program",
                evt => JsonSerializer.Serialize(evt, JsonSerializerOptions.Web), ct);
        }
    }
}
